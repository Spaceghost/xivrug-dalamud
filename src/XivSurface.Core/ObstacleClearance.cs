using System.Numerics;

namespace XivSurface.Core;

/// <summary>Conservative clearance from unverified ground or solid obstacles.
/// Stored in the existing floor texture's validity channel (0..1).</summary>
public static class ObstacleClearance
{
    public const float Reach = 1.5f;

    public static void Apply(float[] heights, int size, Vector2 halfSize)
    {
        if (size < 2 || heights.Length != size * size * 2
            || !float.IsFinite(halfSize.X) || !float.IsFinite(halfSize.Y)
            || halfSize.X <= 0 || halfSize.Y <= 0)
            throw new ArgumentException("Invalid floor grid.");
        var blocked = new List<Vector2>();
        var step = halfSize * 2 / (size - 1);
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
                if (heights[(y * size + x) * 2 + 1] <= 0)
                    blocked.Add(new Vector2(x, y) * step);
        // Subtract half a cell diagonal: clearance never assumes that a
        // blocked sample describes an infinitely small point.
        var margin = step.Length() * 0.5f;
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var index = (y * size + x) * 2 + 1;
                if (heights[index] <= 0) continue;
                var point = new Vector2(x, y) * step;
                var distance = Reach + margin;
                foreach (var obstacle in blocked)
                    distance = Math.Min(distance, Vector2.Distance(point, obstacle));
                heights[index] = Math.Clamp((distance - margin) / Reach, 0, 1);
            }
    }
}
