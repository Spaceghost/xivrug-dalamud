using System.Numerics;

namespace XivSurface.Core;

public readonly record struct ClothContactProjection(float Height, float Weight, float GatherWeight);

/// <summary>A measured audio onset mapped into local monotonic time. Tone is a
/// normalized band-balance hint, NOT a MIDI note or an invented musical event.</summary>
public readonly record struct ClothImpulse(Vector2 Center, double Time, float Strength, float Tone)
{
    public const int MaximumEvents = 16;
    public const double LifetimeSeconds = 2.5;
    public const float MaximumDisplacement = .04f;
    // Leave2mm of the visible displacement budget for spring overshoot when
    // many simultaneous attacks saturate the forcing. Ordinary single-event
    // amplitudes are unchanged; this only softens extreme overlapping peaks.
    private const float MaximumDriveDisplacement = .038f;
    public const double AttackSeconds = .15;
    public const double ReleaseSeconds = .45;
    public bool Valid => MathEx.Finite(Center) && Math.Abs(Center.X) <= 1_000_000 && Math.Abs(Center.Y) <= 1_000_000
        && double.IsFinite(Time) && Time >= 0 && float.IsFinite(Strength) && Strength is > 0 and <= 1
        && float.IsFinite(Tone) && Tone is >= 0 and <= 1;

    public static float Displacement(Vector2 at, double now, ReadOnlySpan<ClothImpulse> impulses)
    {
        if (!MathEx.Finite(at) || !double.IsFinite(now)) return 0;
        double displacement = 0;
        foreach (var impulse in impulses[..Math.Min(impulses.Length, MaximumEvents)])
        {
            var age = now - impulse.Time;
            if (!impulse.Valid || age < 0 || age > LifetimeSeconds) continue;
            var distance = Vector2.Distance(at, impulse.Center);
            // The cloth spring's natural frequency is about1.9Hz. Fast
            //3.5--15.6Hz forcing mostly disappears in that physical response.
            // These broad travelling crests sweep at.56--.99Hz instead:
            // audible attacks become visible swells, not vertex jitter.
            var speed = .8 + impulse.Tone * .3;
            var front = distance - age * speed;
            var envelope = Smooth(age / AttackSeconds) * Smooth((LifetimeSeconds - age) / ReleaseSeconds)
                * Math.Exp(-age / 2.5) * Math.Exp(-front * front / (.55 * .55));
            var wave = Math.Cos(front * (.7 + impulse.Tone * .2) * Math.Tau);
            displacement += MaximumDisplacement * impulse.Strength * envelope * wave;
        }
        return (float)Math.Clamp(displacement, -MaximumDriveDisplacement, MaximumDriveDisplacement);
    }

    private static double Smooth(double value)
    { var t = Math.Clamp(value, 0, 1); return t * t * (3 - 2 * t); }
}

/// <summary>
/// Continuous heel-to-toe pressure, not a visibility mask. Ground is the actual
/// supplied geometric support, WITHOUT the old blanket material clearance.
/// All candidates start at the original presented height; overlap is order
/// independent. CPU and final vertex-shader projection must use this contract.
/// </summary>
public static class ClothContactConstraint
{
    public const float Clearance = .001f;
    public const float Gap = .001f;
    public const float FeatherMultiplier = 1.5f;
    public const float FullContactDistance = .06f;
    public const float MaximumContactDistance = .16f;
    public const float MaximumVisualFlourish = .012f;
    public const float GatherInnerMultiplier = 1.5f;
    public const float GatherOuterMultiplier = 2.5f;
    /// <summary>Above the diagonal of the supported256y-wide material grid.
    /// Invalid larger triangles are rejected, never silently under-padded.</summary>
    public const float MaximumContactPadding = 512f;

    /// <summary>Maximum actual triangle XZ diameter, rounded upward. If a
    /// triangle touches a foot core, every corner is within this additional
    /// radius. Pressing all those corners also bounds their interpolated
    /// interior, without removing faces or introducing a visibility mask.</summary>
    public static float RequiredContactPadding(ClothMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var count = mesh.Positions.Length;
        if (count is < 3 or > ClothSurface.MaximumSize * ClothSurface.MaximumSize
            || mesh.Indices.Length is < 3 or > (ClothSurface.MaximumSize - 1) * (ClothSurface.MaximumSize - 1) * 6
            || mesh.Indices.Length % 3 != 0)
            throw new ArgumentException("Contact padding requires bounded actual triangle geometry.", nameof(mesh));
        double maximumSquared = 0;
        for (var i = 0; i < mesh.Indices.Length; i += 3)
        {
            var a = Vertex(mesh.Indices[i]); var b = Vertex(mesh.Indices[i + 1]); var c = Vertex(mesh.Indices[i + 2]);
            Edge(a, b); Edge(b, c); Edge(c, a);
        }
        if (maximumSquared > (double)MaximumContactPadding * MaximumContactPadding)
            throw new ArgumentException("Cloth triangle diameter exceeds the supported contact bound.", nameof(mesh));
        var padding = (float)Math.Sqrt(maximumSquared);
        if ((double)padding * padding < maximumSquared) padding = MathF.BitIncrement(padding);
        return padding;

        Vector3 Vertex(int index)
        {
            if ((uint)index >= (uint)count || !MathEx.Finite(mesh.Positions[index]))
                throw new ArgumentException("Contact padding requires finite indexed vertices.", nameof(mesh));
            return mesh.Positions[index];
        }
        void Edge(Vector3 a, Vector3 b)
        {
            var x = (double)a.X - b.X; var z = (double)a.Z - b.Z;
            maximumSquared = Math.Max(maximumSquared, x * x + z * z);
        }
    }

    /// <param name="position">Final presented position, including visual lift/flutter.</param>
    /// <param name="visualLift">Eligibility references the lifted support; geometric lower bound never moves.</param>
    /// <param name="contactPadding">Actual rendered triangle diameter, not stale-foot motion or opacity inflation.</param>
    public static ClothContactProjection ProjectHeight(Vector3 position, float ground,
        ReadOnlySpan<ClothFootContact> feet, float visualLift = 0, float contactPadding = 0)
    {
        if (!MathEx.Finite(position) || !float.IsFinite(ground) || Math.Abs(ground) > 1_000_000
            || !float.IsFinite(visualLift) || visualLift is < 0 or > 8
            || !float.IsFinite(contactPadding) || contactPadding is < 0 or > MaximumContactPadding)
            throw new ArgumentException("Contact projection requires finite supported geometry and bounded lift.");
        var original = Math.Max(position.Y, ground + Clearance);
        var height = original; var weight = 0f; var gather = 0f;
        var at = new Vector2(position.X, position.Z);
        var contacts = feet[..Math.Min(feet.Length, ClothFootClearance.MaximumContacts)];
        // Endpoint gather rings are not dominated by the capsule's ring;
        // retain all raw pressure primitives even for identical envelopes.
        foreach (var foot in contacts) Apply(foot, foot.Center);
        for (var i = 0; i + 1 < contacts.Length; i += 2)
        {
            var a = contacts[i]; var b = contacts[i + 1];
            if (!CanPair(a, b)) continue;
            var segment = b.Center - a.Center;
            var length = segment.LengthSquared();
            var t = length > .000001f ? Math.Clamp(Vector2.Dot(at - a.Center, segment) / length, 0, 1) : 0;
            Apply(a with { FootY = Math.Min(a.FootY, b.FootY), Radius = Math.Min(a.Radius, b.Radius) }, a.Center + t * segment);
        }
        return new(Math.Max(ground + Clearance, height), weight, gather * (1 - weight));

        void Apply(ClothFootContact foot, Vector2 center)
        {
            if (!foot.Valid || foot.Radius > ClothFootClearance.MaximumBootRadius) return;
            var distance = Vector2.Distance(at, center) / (foot.Radius + contactPadding);
            // A conservative sole below the support means compressed contact,
            // not permission to drill below that support or punch a hole.
            var referenceGround = ground;
            var gap = Math.Max(0, foot.FootY - (referenceGround + visualLift));
            var eligibility = Smooth((MaximumContactDistance - gap) / (MaximumContactDistance - FullContactDistance));
            var influence = eligibility * Smooth((FeatherMultiplier - distance) / (FeatherMultiplier - 1));
            // A common sole-height plateau is NOT safe on a slope: clamping
            // different corners to max(floor,sole) can interpolate ABOVE the
            // sole inside the triangle. Full planted contact follows the
            // actual affine floor; only a uniform safe airborne offset may
            // remain. Conservative below-floor soles still mean thin cloth.
            var safeLift = Math.Max(0, Math.Min(visualLift, foot.FootY - referenceGround - Gap - Clearance));
            var ceiling = ground + Clearance + safeLift;
            var bounded = Math.Min(original, ceiling);
            height = Math.Min(height, influence >= 1 ? bounded : original + (bounded - original) * influence);
            weight = Math.Max(weight, influence);
            var ring = (distance - GatherInnerMultiplier) / (GatherOuterMultiplier - GatherInnerMultiplier);
            var envelope = ring is > 0 and < 1 ? 16 * ring * ring * (1 - ring) * (1 - ring) : 0;
            gather = Math.Max(gather, envelope * eligibility);
        }
    }

    public static bool CanPair(ClothFootContact a, ClothFootContact b) => a.Valid && b.Valid
        && a.Radius <= ClothFootClearance.MaximumBootRadius && b.Radius <= ClothFootClearance.MaximumBootRadius
        && Math.Abs(a.FootY - b.FootY) <= .0001f && Math.Abs(a.Radius - b.Radius) <= .0001f
        && Vector2.DistanceSquared(a.Center, b.Center) <= .75f * .75f;

    /// <summary>
    /// Final render constraints for the CURRENT mesh and CURRENT age-swept
    /// contacts. Reducing adjacent triangle constraints into shared indices
    /// avoids cracks. Inside a full foot core, every corner is constrained to
    /// its actual floor plus one triangle-constant safe offset: interpolation
    /// therefore stays affine instead of forming a raised sole-height plateau.
    /// Apply these ceilings AFTER all lift/flutter, then clamp to floor+.001.
    /// This deforms continuous geometry; it is never an opacity mask.
    /// </summary>
    public static float[] BuildRenderCeilings(ClothMesh mesh, ReadOnlySpan<ClothFootContact> feet,
        float lift, float maximumFlutter = MaximumVisualFlourish)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if(mesh.Positions.Length is <3 or >ClothSurface.MaximumSize*ClothSurface.MaximumSize)
            throw new ArgumentException("Render contacts require bounded vertices.",nameof(mesh));
        var ceilings=new float[mesh.Positions.Length];
        BuildRenderCeilings(mesh,feet,lift,ceilings,maximumFlutter);
        return ceilings;
    }

    /// <summary>Allocation-free rendering overload. Every destination value is
    /// reset before processing, including the no-feet case; no stale ceiling
    /// can survive a reused GPU scratch buffer.</summary>
    public static void BuildRenderCeilings(ClothMesh mesh, ReadOnlySpan<ClothFootContact> feet,
        float lift, Span<float> ceilings, float maximumFlutter = MaximumVisualFlourish)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (!float.IsFinite(lift) || lift is < 0 or > 8 || !float.IsFinite(maximumFlutter)
            || maximumFlutter is < 0 or > MaximumVisualFlourish)
            throw new ArgumentException("Render contact lift and flutter must be finite and bounded.");
        _ = RequiredContactPadding(mesh); // bounded topology/position validation
        if (mesh.GroundMinimum is not { } ground || ground.Length != mesh.Positions.Length)
            throw new ArgumentException("Render contacts require every vertex's actual ground support.", nameof(mesh));
        if(ceilings.Length!=mesh.Positions.Length)
            throw new ArgumentException("Every render vertex requires a destination ceiling.",nameof(ceilings));
        if(ground.AsSpan().Overlaps(ceilings))
            throw new ArgumentException("Render ceilings must not overwrite immutable ground support.",nameof(ceilings));
        foreach (var value in ground)
            if (!float.IsFinite(value) || Math.Abs(value) > 1_000_000)
                throw new ArgumentException("Render contact ground support must be finite.", nameof(mesh));
        var contacts = feet[..Math.Min(feet.Length, ClothFootClearance.MaximumContacts)];
        foreach (var foot in contacts)
            if (foot != default && (!foot.Valid || foot.Radius > ClothFootRenderFrame.MaximumClothRenderRadius))
                throw new ArgumentException("Render foot contact exceeds the admitted swept envelope.", nameof(feet));
        ceilings.Fill(float.MaxValue);
        Span<RenderPrimitive> primitives = stackalloc RenderPrimitive[6];
        var primitiveCount = 0;
        for (var i = 0; i < contacts.Length; i += 2)
        {
            var a = contacts[i]; var b = i + 1 < contacts.Length ? contacts[i + 1] : default;
            var paired = RenderPair(a, b);
            if (!paired || !IdenticalEnvelope(a, b))
            {
                if (a.Valid) primitives[primitiveCount++] = new(a, a.Center, a.Center);
                if (b.Valid) primitives[primitiveCount++] = new(b, b.Center, b.Center);
            }
            if (paired) primitives[primitiveCount++] = new(
                a with { FootY = Math.Min(a.FootY, b.FootY), Radius = Math.Min(a.Radius, b.Radius) },
                a.Center, b.Center, IdenticalEnvelope(a, b));
        }
        if (primitiveCount == 0) return;
        for (var i = 0; i < mesh.Indices.Length; i += 3)
        {
            var a = mesh.Indices[i]; var b = mesh.Indices[i + 1]; var c = mesh.Indices[i + 2];
            var pa = mesh.Positions[a]; var pb = mesh.Positions[b]; var pc = mesh.Positions[c];
            var groundMax = Math.Max(ground[a], Math.Max(ground[b], ground[c]));
            var freeMax = Math.Max(pa.Y, Math.Max(pb.Y, pc.Y));
            var diameter = Diameter(pa, pb, pc);
            var minimum = new Vector2(Math.Min(pa.X, Math.Min(pb.X, pc.X)), Math.Min(pa.Z, Math.Min(pb.Z, pc.Z)));
            var maximum = new Vector2(Math.Max(pa.X, Math.Max(pb.X, pc.X)), Math.Max(pa.Z, Math.Max(pb.Z, pc.Z)));
            foreach (var primitive in primitives[..primitiveCount]) Apply(primitive.Foot, primitive.Start, primitive.End, primitive.IncludesEndpoints,ceilings);

            void Apply(ClothFootContact foot, Vector2 start, Vector2 end, bool includesEndpoints,Span<float> destination)
            {
                // Both predicates and the offset are uniform over the triangle.
                // Maximum input cloth height also catches an already-raised
                // fold beneath a raised foot, before GPU displacement is added.
                if (foot.FootY > groundMax + lift + FullContactDistance
                    && freeMax + lift + maximumFlutter < foot.FootY - Gap) return;
                var offset = Math.Clamp(foot.FootY - Gap - (groundMax + Clearance), 0, lift);
                var radius = foot.Radius + diameter;
                // Outward-rounded capsule AABB is a conservative broad phase,
                // not a replacement for exact capsule distance below. Include
                // the FULL feather and round coordinate subtraction outward.
                var extent = MathF.BitIncrement(FeatherMultiplier * radius);
                if (maximum.X < MathF.BitDecrement(Math.Min(start.X, end.X) - extent)
                    || minimum.X > MathF.BitIncrement(Math.Max(start.X, end.X) + extent)
                    || maximum.Y < MathF.BitDecrement(Math.Min(start.Y, end.Y) - extent)
                    || minimum.Y > MathF.BitIncrement(Math.Max(start.Y, end.Y) + extent)) return;
                Corner(a,destination); Corner(b,destination); Corner(c,destination);
                void Corner(int index,Span<float> output)
                {
                    var vertex = mesh.Positions[index]; var at = new Vector2(vertex.X, vertex.Z);
                    var segment = end - start; var length = segment.LengthSquared();
                    var t = length > .000001f ? Math.Clamp(Vector2.Dot(at - start, segment) / length, 0, 1) : 0;
                    var squared = Vector2.DistanceSquared(at, start + t * segment);
                    // Float closest-point rounding (especially tiny segments
                    // at large world coordinates) can put the projected point
                    // slightly farther away than an original endpoint. Keep
                    // the exact old union while doing only one sqrt/ceiling.
                    if (includesEndpoints) squared = Math.Min(squared,
                        Math.Min(Vector2.DistanceSquared(at, start), Vector2.DistanceSquared(at, end)));
                    var distance = MathF.Sqrt(squared) / radius;
                    var weight = Smooth((FeatherMultiplier - distance) / (FeatherMultiplier - 1));
                    if (weight <= 0) return;
                    var floor = ground[index] + Clearance;
                    var original = Math.Max(floor, vertex.Y + lift + maximumFlutter);
                    var bounded = Math.Min(original, floor + offset);
                    var candidate = weight >= 1 ? bounded : original + (bounded - original) * weight;
                    output[index] = Math.Min(output[index], Math.Max(floor, candidate));
                }
            }
        }
    }

    private static bool RenderPair(ClothFootContact a, ClothFootContact b) => a.Valid && b.Valid
        && a.Radius <= ClothFootRenderFrame.MaximumClothRenderRadius && b.Radius <= ClothFootRenderFrame.MaximumClothRenderRadius
        && Math.Abs(a.FootY - b.FootY) <= .0001f && Math.Abs(a.Radius - b.Radius) <= .0001f
        && Vector2.DistanceSquared(a.Center, b.Center) <= .75f * .75f;

    private static bool IdenticalEnvelope(ClothFootContact a, ClothFootContact b) => a.FootY == b.FootY && a.Radius == b.Radius;
    private readonly record struct RenderPrimitive(ClothFootContact Foot, Vector2 Start, Vector2 End, bool IncludesEndpoints = false);

    private static float Diameter(Vector3 a, Vector3 b, Vector3 c)
    {
        var squared = Math.Max(Edge(a, b), Math.Max(Edge(b, c), Edge(c, a)));
        var result = (float)Math.Sqrt(squared);
        return (double)result * result < squared ? MathF.BitIncrement(result) : result;
        static double Edge(Vector3 x, Vector3 y)
        { var dx = (double)x.X - y.X; var dz = (double)x.Z - y.Z; return dx * dx + dz * dz; }
    }

    private static float Smooth(float value)
    { var t = Math.Clamp(value, 0, 1); return t * t * (3 - 2 * t); }
}

/// <summary>
/// Persistent, bounded vertical cloth relaxation. This is a lightweight cloth
/// contact approximation, not a full material/volume-conserving simulation.
/// Contact depression feeds a surrounding gather ring; spring/damping releases
/// it smoothly. X/Z, topology and UVs are never changed or removed. Foot and
/// geometric constraints are projected after EVERY step and once more last.
/// The renderer must repeat projection after its own lift/hem displacement.
/// </summary>
public sealed class ClothContactSolver
{
    public const double MaximumStepSeconds = 1d / 120;
    public const int MaximumSubsteps = 12;
    public const double MaximumElapsedSeconds = MaximumStepSeconds * MaximumSubsteps;
    public const float MaximumGatherHeight = .05f;
    public const float GatherFraction = .6f;
    public const float MaximumVerticalSpeed = 2;
    private const float Spring = 144, Damping = 24, NeighbourSpring = 18;
    private float[] heights = [], velocities = [], nextHeights = [], nextVelocities = [];
    // Private, fully overwritten scratch. None of these buffers escapes in a
    // published mesh; keep immutable position/normal outputs independently owned.
    private float[] desired = [], ring = [], upperBounds = [];
    private Vector2[] coordinates = [];
    private double previousTime = double.NaN;
    public int LastSubsteps { get; private set; }

    public void Reset()
    {
        heights = velocities = nextHeights = nextVelocities = []; coordinates = [];
        desired = ring = upperBounds = [];
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
        if (desired.Length < count)
        {
            // Commit together so a failed allocation cannot leave mismatched
            // capacities. Shrinking/alternating windows retain the high-water size.
            var newDesired = new float[count]; var newRing = new float[count]; var newUpperBounds = new float[count];
            (desired, ring, upperBounds) = (newDesired, newRing, newUpperBounds);
        }
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
