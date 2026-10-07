using System.Numerics;

namespace XivSurface.Core.Tests;

// Frozen pre-scratch-reuse implementation: semantic oracle, intentionally allocating.
internal sealed class FrozenAllocatingClothContactSolver
{
    // Exact copies of the original assembly-internal finite predicates.
    private static class MathEx
    {
        public static bool Finite(Vector2 v) => float.IsFinite(v.X) && float.IsFinite(v.Y);
        public static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    }

    public const double MaximumStepSeconds = 1d / 120;
    public const int MaximumSubsteps = 12;
    public const double MaximumElapsedSeconds = MaximumStepSeconds * MaximumSubsteps;
    public const float MaximumGatherHeight = .05f;
    public const float GatherFraction = .6f;
    public const float MaximumVerticalSpeed = 2;
    private const float Spring = 144, Damping = 24, NeighbourSpring = 18;
    private float[] heights = [], velocities = [], nextHeights = [], nextVelocities = [];
    private Vector2[] coordinates = [];
    private double previousTime = double.NaN;
    public int LastSubsteps { get; private set; }

    public void Reset()
    {
        heights = velocities = nextHeights = nextVelocities = []; coordinates = [];
        previousTime = double.NaN; LastSubsteps = 0;
    }

    /// <summary>Caller owns zone/geometry invalidation and calls Reset(). Moving
    /// windows preserve only exactly matching, unambiguous validated X/Z nodes.</summary>
    public ClothMesh Update(ClothMesh target, ReadOnlySpan<ClothFootContact> feet, double now, float visualLift = 0,
        ReadOnlySpan<ClothImpulse> impulses = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!double.IsFinite(now) || now < 0 || !float.IsFinite(visualLift) || visualLift is < 0 or > 8)
            throw new ArgumentException("Invalid solver clock or lift.");
        var count = target.Positions.Length;
        if (target.Size is < 2 or > ClothSurface.MaximumSize || count != target.Size * target.Size
            || target.Normals.Length != count || target.UV.Length != count || target.Indices.Length % 3 != 0
            || target.GroundMinimum is not { } ground || ground.Length != count)
            throw new ArgumentException("Every cloth vertex requires explicit geometric ground support.", nameof(target));
        for (var i = 0; i < count; i++)
            if (!MathEx.Finite(target.Positions[i]) || !float.IsFinite(ground[i]) || Math.Abs(ground[i]) > 1_000_000
                || !MathEx.Finite(target.UV[i])) throw new ArgumentException("Invalid supported cloth vertex.", nameof(target));
        foreach (var index in target.Indices)
            if ((uint)index >= (uint)count) throw new ArgumentException("Invalid cloth topology.", nameof(target));
        var contactPadding = ClothContactConstraint.RequiredContactPadding(target);

        var elapsed = now - previousTime;
        var discontinuity = !double.IsFinite(previousTime) || elapsed < 0 || elapsed > .25;
        PrepareState(target, ground, discontinuity);
        previousTime = now;
        var desired = new float[count]; var ring = new float[count]; var upperBounds = new float[count];
        double removed = 0, ringTotal = 0;
        for (var i = 0; i < count; i++)
        {
            var free = Math.Max(ground[i] + ClothContactConstraint.Clearance, target.Positions[i].Y);
            var sample = Project(free, i, feet);
            desired[i] = Math.Max(ground[i] + ClothContactConstraint.Clearance, sample.Height - visualLift);
            // Anchor feathered ceilings to the ORIGINAL free surface, not
            // repeatedly to the last constrained height. Otherwise a paused
            // frame/substep count would slowly multiply the feather away.
            upperBounds[i] = sample.Weight > 0 ? desired[i] : float.PositiveInfinity;
            removed += Math.Max(0, free - desired[i]);
            ring[i] = sample.GatherWeight; ringTotal += ring[i];
        }
        if (ringTotal > 1e-8 && removed > 0)
            for (var i = 0; i < count; i++)
                desired[i] += (float)Math.Min(MaximumGatherHeight, removed * GatherFraction * ring[i] / ringTotal);
        // Real producer events excite the cloth's existing spring state.
        // No events means exactly zero music force; wall-clock alone cannot
        // generate notes/onsets. Combined energy is bounded even for16 peaks.
        for (var i = 0; i < count; i++)
            desired[i] = Math.Max(ground[i] + ClothContactConstraint.Clearance, desired[i]
                + ClothImpulse.Displacement(coordinates[i], now, impulses));

        // Large gaps do not create time debt. Ground and contact projection
        // still happen immediately; spring motion advances at most100ms.
        var dt = discontinuity ? 0 : Math.Min(elapsed, MaximumElapsedSeconds);
        LastSubsteps = dt > 0 ? Math.Min(MaximumSubsteps, (int)Math.Ceiling(dt / MaximumStepSeconds)) : 0;
        var step = LastSubsteps == 0 ? 0 : (float)(dt / LastSubsteps);
        for (var iteration = 0; iteration < LastSubsteps; iteration++)
        {
            for (var i = 0; i < count; i++)
            {
                var error = heights[i] - desired[i];
                var x = i % target.Size; var z = i / target.Size;
                var neighbourError = 0f; var neighbours = 0;
                if (x > 0) Add(i - 1); if (x + 1 < target.Size) Add(i + 1);
                if (z > 0) Add(i - target.Size); if (z + 1 < target.Size) Add(i + target.Size);
                var acceleration = -Spring * error - Damping * velocities[i]
                    + NeighbourSpring * (neighbourError / Math.Max(1, neighbours) - error);
                var velocity = Math.Clamp(velocities[i] + acceleration * step, -MaximumVerticalSpeed, MaximumVerticalSpeed);
                var candidate = heights[i] + velocity * step;
                // Contact is a constraint, never a lagging target that the
                // visible foot must wait for the simulated fabric to reach.
                var constrained = Math.Max(ground[i] + ClothContactConstraint.Clearance, Math.Min(candidate, upperBounds[i]));
                if (Math.Abs(constrained - candidate) > .000001f) velocity = 0;
                nextHeights[i] = constrained; nextVelocities[i] = velocity;
                void Add(int j) { neighbourError += heights[j] - desired[j]; neighbours++; }
            }
            (heights, nextHeights) = (nextHeights, heights);
            (velocities, nextVelocities) = (nextVelocities, velocities);
        }
        var positions = new Vector3[count];
        for (var i = 0; i < count; i++)
        {
            var final = Math.Max(ground[i] + ClothContactConstraint.Clearance, Math.Min(heights[i], upperBounds[i]));
            if (Math.Abs(final - heights[i]) > .000001f) velocities[i] = 0;
            heights[i] = final;
            positions[i] = new(target.Positions[i].X, final, target.Positions[i].Z);
        }
        return target with { Positions = positions, Normals = Normals(positions, target.Indices) };

        ClothContactProjection Project(float y, int i, ReadOnlySpan<ClothFootContact> contacts) => ClothContactConstraint.ProjectHeight(
            new(target.Positions[i].X, y + visualLift, target.Positions[i].Z), ground[i], contacts, visualLift, contactPadding);
    }

    private void PrepareState(ClothMesh target, float[] ground, bool reset)
    {
        var count = target.Positions.Length;
        var same = !reset && coordinates.Length == count;
        if (same)
            for (var i = 0; i < count; i++)
                if (coordinates[i] != new Vector2(target.Positions[i].X, target.Positions[i].Z)) { same = false; break; }
        if (same) return;
        Dictionary<Vector2, (float Height, float Velocity)>? previous = null;
        if (!reset && coordinates.Length > 0)
        {
            previous = new(coordinates.Length);
            for (var i = 0; i < coordinates.Length; i++)
                // Collapsed wall nodes share coordinates but may have distinct
                // material folds. Do not guess which old fold a new vertex owns.
                if (!previous.TryAdd(coordinates[i], (heights[i], velocities[i]))) previous[coordinates[i]] = (float.NaN, 0);
        }
        heights = new float[count]; velocities = new float[count];
        nextHeights = new float[count]; nextVelocities = new float[count]; coordinates = new Vector2[count];
        for (var i = 0; i < count; i++)
        {
            var p = target.Positions[i]; var key = new Vector2(p.X, p.Z); coordinates[i] = key;
            var minimum = ground[i] + ClothContactConstraint.Clearance;
            if (previous is not null && previous.TryGetValue(key, out var state) && float.IsFinite(state.Height))
            { heights[i] = Math.Max(minimum, state.Height); velocities[i] = state.Height < minimum ? 0 : state.Velocity; }
            else heights[i] = Math.Max(minimum, p.Y);
        }
    }

    private static Vector3[] Normals(Vector3[] positions, int[] indices)
    {
        var normals = new Vector3[positions.Length];
        for (var i = 0; i < indices.Length; i += 3)
        {
            var a = indices[i]; var b = indices[i + 1]; var c = indices[i + 2];
            var normal = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
            if (normal.Y < 0) normal = -normal;
            normals[a] += normal; normals[b] += normal; normals[c] += normal;
        }
        for (var i = 0; i < normals.Length; i++) normals[i] = MathEx.Finite(normals[i]) && normals[i].LengthSquared() > 1e-12f
            ? Vector3.Normalize(normals[i]) : Vector3.UnitY;
        return normals;
    }
}
