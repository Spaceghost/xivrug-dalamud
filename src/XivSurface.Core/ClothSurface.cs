using System.Numerics;

namespace XivSurface.Core;

/// <summary>A single connected material grid. UVs belong to the cloth, so the map follows its folds.</summary>
public sealed record ClothMesh(Vector3[] Positions, Vector3[] Normals, Vector2[] UV, int[] Indices, int Size,
    float[]? GroundMinimum = null);

/// <summary>A local fold pressure. Optional ContactY is an absolute sole height;
/// omitted height preserves existing deliberately persistent stomp patches.</summary>
public readonly record struct ClothPressure(Vector2 Center, float Radius, float Strength, float? ContactY = null)
{
    public const float FullContactDistance = .06f;
    public const float MaximumContactDistance = .16f;

    public float Influence(Vector2 position, float? measuredMinimumY = null)
    {
        if (!MathEx.Finite(Center) || !MathEx.Finite(position) || !float.IsFinite(Radius) || Radius <= 0 ||
            !float.IsFinite(Strength) || Strength <= 0) return 0;
        var heightWeight = 1f;
        if (ContactY is { } sole)
        {
            // Compare with this vertex's validated floor minimum, never player
            // Y (which also rises during a jump) or an already-raised fold.
            if (!float.IsFinite(sole) || measuredMinimumY is not { } minimum || !float.IsFinite(minimum)) return 0;
            var gap = Math.Abs(sole - minimum);
            var t = Math.Clamp((MaximumContactDistance - gap) / (MaximumContactDistance - FullContactDistance), 0, 1);
            heightWeight = t * t * (3 - 2 * t);
        }
        var dx = (double)position.X - Center.X; var dz = (double)position.Y - Center.Y;
        var distanceSquared = dx * dx + dz * dz;
        if (distanceSquared >= (double)Radius * Radius) return 0;
        var inside = 1 - (float)(Math.Sqrt(distanceSquared) / Radius);
        return heightWeight * Math.Clamp(Strength, 0, 1) * inside * inside * (3 - 2 * inside);
    }
}

/// <summary>
/// Builds a tensioned cloth over supplied collision contacts. It never removes an interior face, lowers a
/// vertex through its contact, or guesses a surface where a contact is missing. Horizontal contact positions
/// are retained exactly: the caller's wall clearance cannot be undone by a smoothing or wrinkle operation.
/// </summary>
public static class ClothSurface
{
    public const float Clearance = 0.035f;
    /// <summary>Maximum vertical change per unit of original material distance, including gathered folds.</summary>
    public const float MaximumMaterialSlope = 2f;
    public const int MaximumSize = 129;

    /// <param name="contacts">Row-major measured surface positions, X increasing across each row, Z across rows.</param>
    /// <param name="cellCeilings">
    /// Optional row-major upper surface heights for all (size-1)^2 cells. Each supplied bound raises all four
    /// corners, so their entire triangles remain above that bound. Midpoint samples may contribute to these
    /// bounds, but a midpoint alone cannot prove the maximum of arbitrary unsampled scene geometry.
    /// </param>
    public static ClothMesh Build(Vector2 center, Vector2 halfSize, float anchorY,
        ReadOnlySpan<Vector3> contacts, int size, float time, bool motion, ReadOnlySpan<float> cellCeilings = default,
        ReadOnlySpan<ClothPressure> pressures = default, int diagonalParity = 0,
        Vector2? materialCenter = null, Vector2? materialHalf = null, ReadOnlySpan<bool> planarCells = default,
        ReadOnlySpan<float> cellLifts = default)
    {
        Validate(center, halfSize, anchorY, size, time);
        var textileCenter = materialCenter ?? center;
        var textileHalf = materialHalf ?? halfSize;
        Validate(textileCenter, textileHalf, anchorY, size, time);
        var count = size * size;
        if (contacts.Length != count) throw new ArgumentException("Every cloth vertex needs a measured contact.", nameof(contacts));
        if (!cellCeilings.IsEmpty && cellCeilings.Length != (size - 1) * (size - 1))
            throw new ArgumentException("Cell bounds must cover the entire material grid.", nameof(cellCeilings));
        if (!planarCells.IsEmpty && (cellCeilings.IsEmpty || planarCells.Length != cellCeilings.Length))
            throw new ArgumentException("Planar evidence must accompany every cell bound.", nameof(planarCells));
        if (!cellLifts.IsEmpty && (cellCeilings.IsEmpty || cellLifts.Length != cellCeilings.Length))
            throw new ArgumentException("Conformal evidence must accompany every cell bound.", nameof(cellLifts));

        var positions = contacts.ToArray();
        var uv = new Vector2[count];
        var minimum = new float[count];
        var heights = new float[count];
        var compression = new Vector3[count]; // material X compression, material Z compression, lateral displacement
        var step = 2 * halfSize / (size - 1);
        for (var z = 0; z < size; z++)
        for (var x = 0; x < size; x++)
        {
            var i = z * size + x;
            if (!ValidContact(positions[i])) throw new ArgumentException("Cloth contacts must be finite measured world positions.", nameof(contacts));
            uv[i] = new((float)x / (size - 1), (float)z / (size - 1));
            minimum[i] = positions[i].Y + Clearance;
        }

        if (!cellCeilings.IsEmpty)
        {
            for (var z = 0; z < size - 1; z++)
            for (var x = 0; x < size - 1; x++)
            {
                var ceiling = cellCeilings[z * (size - 1) + x];
                if (!float.IsFinite(ceiling) || MathF.Abs(ceiling) > 1_000_000)
                    throw new ArgumentException("Every supplied cell bound must be finite.", nameof(cellCeilings));
                var a = z * size + x; var b = a + 1; var c = a + size; var d = c + 1;
                if (!cellLifts.IsEmpty && !float.IsNaN(cellLifts[z * (size - 1) + x]))
                {
                    var lift = cellLifts[z * (size - 1) + x];
                    if (!float.IsFinite(lift) || lift < 0 || lift > 2_000_000)
                        throw new ArgumentException("Measured conformal lifts must be finite and nonnegative, or NaN when unavailable.", nameof(cellLifts));
                    // The sampler proved this residual above BOTH possible
                    // contact-triangle diagonals. Keep each corner's own floor
                    // height; neighboring cells may only increase its bound.
                    minimum[a] = MathF.Max(minimum[a], positions[a].Y + lift + Clearance);
                    minimum[b] = MathF.Max(minimum[b], positions[b].Y + lift + Clearance);
                    minimum[c] = MathF.Max(minimum[c], positions[c].Y + lift + Clearance);
                    minimum[d] = MathF.Max(minimum[d], positions[d].Y + lift + Clearance);
                    continue;
                }
                // Only the sampler's actual collision-triangle coverage proof
                // authorizes following the per-corner plane. Equal sample
                // heights alone are not evidence about the intervening floor.
                if (!planarCells.IsEmpty && planarCells[z * (size - 1) + x]) continue;
                var bound = MathF.Max(ceiling,
                    MathF.Max(MathF.Max(positions[a].Y, positions[b].Y), MathF.Max(positions[c].Y, positions[d].Y))) + Clearance;
                minimum[a] = MathF.Max(minimum[a], bound); minimum[b] = MathF.Max(minimum[b], bound);
                minimum[c] = MathF.Max(minimum[c], bound); minimum[d] = MathF.Max(minimum[d], bound);
            }
        }

        for (var z = 0; z < size; z++)
        for (var x = 0; x < size; x++)
        {
            var i = z * size + x;
            float cx = 0, cz = 0; int nx = 0, nz = 0;
            if (x > 0) { cx += Compression(positions[i], positions[i - 1], step.X); nx++; }
            if (x + 1 < size) { cx += Compression(positions[i], positions[i + 1], step.X); nx++; }
            if (z > 0) { cz += Compression(positions[i], positions[i - size], step.Y); nz++; }
            if (z + 1 < size) { cz += Compression(positions[i], positions[i + size], step.Y); nz++; }
            var rest = center + (uv[i] * 2 - Vector2.One) * halfSize;
            var moved = Vector2.Distance(rest, new(positions[i].X, positions[i].Z));
            compression[i] = new(cx / nx, cz / nz, Math.Clamp(moved / (MathF.Min(halfSize.X, halfSize.Y) * 0.3f), 0, 1));
        }

        var phase = motion ? (time * 0.35f) % MathF.Tau : 0;
        var wavelength = Math.Clamp(MathF.Min(textileHalf.X, textileHalf.Y) * 0.4f, 0.3f, 0.8f);
        var frequency = MathF.Tau / wavelength;
        var amplitude = MathF.Min(0.20f, MathF.Min(textileHalf.X, textileHalf.Y) * 0.14f);
        for (var z = 0; z < size; z++)
        for (var x = 0; x < size; x++)
        {
            var i = z * size + x;
            var pressure = Vector3.Zero;
            var weight = 0f;
            // Smooth strain before generating folds, rather than smoothing actual contact positions through walls.
            for (var dz = -1; dz <= 1; dz++)
            for (var dx = -1; dx <= 1; dx++)
            {
                var xx = x + dx; var zz = z + dz;
                if (xx < 0 || zz < 0 || xx >= size || zz >= size) continue;
                var w = (dx == 0 ? 2 : 1) * (dz == 0 ? 2 : 1);
                pressure += compression[zz * size + xx] * w;
                weight += w;
            }
            pressure /= weight;
            // Collision support stays world anchored; only the textile phase
            // follows the requested material window. Never shift a contact XZ.
            var local = center + (uv[i] * 2 - Vector2.One) * halfSize - textileCenter;
            var waveX = 0.5f + 0.5f * MathF.Cos(local.X * frequency + local.Y * 0.7f + phase);
            var waveZ = 0.5f + 0.5f * MathF.Cos(local.Y * frequency - local.X * 0.7f - phase);
            var folds = amplitude * (pressure.X * waveX + pressure.Y * waveZ) / MathF.Max(1, pressure.X + pressure.Y);
            folds += amplitude * 0.3f * pressure.Z * (0.5f + 0.5f * MathF.Sin((local.X + local.Y) * frequency * 0.7f + phase));
            var edge = MathF.Pow(Math.Clamp(MathF.Max(MathF.Abs(local.X / textileHalf.X), MathF.Abs(local.Y / textileHalf.Y)), 0, 1), 8);
            var flutter = motion ? 0.006f * edge * (0.5f + 0.5f * MathF.Sin(local.X * 3 + local.Y * 4 + phase * 2)) : 0;
            var pressed = 0f;
            foreach (var footPressure in pressures)
                pressed = MathF.Max(pressed, footPressure.Influence(new(positions[i].X, positions[i].Z), minimum[i]));
            heights[i] = minimum[i] - anchorY + (folds + flutter) * (1 - pressed);
        }

        Tension(heights, size, step);
        for (var i = 0; i < count; i++)
            positions[i].Y = MathF.Max(minimum[i], anchorY + heights[i]);

        var indices = new int[(size - 1) * (size - 1) * 6];
        var at = 0;
        for (var z = 0; z < size - 1; z++)
        for (var x = 0; x < size - 1; x++)
        {
            var a = z * size + x; var b = a + 1; var c = a + size; var d = c + 1;
            // Alternating diagonals keep the connected sheet from favoring one diagonal direction.
            if (((x + z + diagonalParity) & 1) == 0)
            {
                indices[at++] = a; indices[at++] = c; indices[at++] = b;
                indices[at++] = b; indices[at++] = c; indices[at++] = d;
            }
            else
            {
                indices[at++] = a; indices[at++] = d; indices[at++] = b;
                indices[at++] = a; indices[at++] = c; indices[at++] = d;
            }
        }
        var ground = new float[count];
        for (var i = 0; i < count; i++) ground[i] = minimum[i] - Clearance;
        return new(positions, Normals(positions, indices), uv, indices, size, ground);
    }

    /// <summary>Convenience overload for unclamped contacts; heights are absolute world Y values.</summary>
    public static ClothMesh Build(Vector2 center, Vector2 halfSize, float anchorY,
        ReadOnlySpan<float> heights, int size, float time, bool motion, ReadOnlySpan<float> cellCeilings = default,
        ReadOnlySpan<ClothPressure> pressures = default)
    {
        Validate(center, halfSize, anchorY, size, time);
        if (heights.Length != size * size) throw new ArgumentException("Every cloth vertex needs a measured height.", nameof(heights));
        var contacts = new Vector3[heights.Length];
        for (var z = 0; z < size; z++)
        for (var x = 0; x < size; x++)
            contacts[z * size + x] = new(center.X + (2f * x / (size - 1) - 1) * halfSize.X,
                heights[z * size + x], center.Y + (2f * z / (size - 1) - 1) * halfSize.Y);
        return Build(center, halfSize, anchorY, contacts, size, time, motion, cellCeilings, pressures);
    }

    private static float Compression(Vector3 a, Vector3 b, float rest) =>
        Math.Clamp(1 - Vector2.Distance(new(a.X, a.Z), new(b.X, b.Z)) / rest, 0, 1);

    private static void Tension(float[] heights, int size, Vector2 step)
    {
        // The upper Lipschitz envelope raises neighboring cloth around a contact or crease. Starting with all
        // vertices in a max-height queue finds that envelope without lowering a single measured constraint.
        var queue = new PriorityQueue<int, float>(heights.Length);
        for (var i = 0; i < heights.Length; i++) queue.Enqueue(i, -heights[i]);
        while (queue.TryDequeue(out var i, out var negativeHeight))
        {
            var height = -negativeHeight;
            if (height < heights[i]) continue;
            var x = i % size; var z = i / size;
            for (var dz = -1; dz <= 1; dz++)
            for (var dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dz == 0) continue;
                var xx = x + dx; var zz = z + dz;
                if (xx < 0 || zz < 0 || xx >= size || zz >= size) continue;
                var j = zz * size + xx;
                var distance = new Vector2(dx * step.X, dz * step.Y).Length();
                var required = height - MaximumMaterialSlope * distance;
                if (required <= heights[j]) continue;
                heights[j] = required;
                queue.Enqueue(j, -required);
            }
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
        for (var i = 0; i < normals.Length; i++)
            normals[i] = MathEx.Finite(normals[i]) && normals[i].LengthSquared() > 1e-12f ? Vector3.Normalize(normals[i]) : Vector3.UnitY;
        return normals;
    }

    private static bool ValidContact(Vector3 p) => MathEx.Finite(p) && MathF.Max(MathF.Max(MathF.Abs(p.X), MathF.Abs(p.Y)), MathF.Abs(p.Z)) <= 1_000_000;

    private static void Validate(Vector2 center, Vector2 halfSize, float anchorY, int size, float time)
    {
        if (!MathEx.Finite(center) || MathF.Max(MathF.Abs(center.X), MathF.Abs(center.Y)) > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(center));
        if (!MathEx.Finite(halfSize) || halfSize.X < 0.05f || halfSize.Y < 0.05f || halfSize.X > 128 || halfSize.Y > 128)
            throw new ArgumentOutOfRangeException(nameof(halfSize));
        if (!float.IsFinite(anchorY) || MathF.Abs(anchorY) > 1_000_000) throw new ArgumentOutOfRangeException(nameof(anchorY));
        if (size < 2 || size > MaximumSize) throw new ArgumentOutOfRangeException(nameof(size));
        if (!float.IsFinite(time)) throw new ArgumentOutOfRangeException(nameof(time));
    }
}
