using System.Numerics;
using System.Text.Json;

namespace XivSurface.Core;

/// <summary>Optional route visual state; independent from Wayfinder's assembly and cached raster geometry.</summary>
public sealed record NavigationVisual(float Progress, float GuideArc, float LookAheadArc,
    Vector3 GuidePosition, Vector3 LookAheadPosition, Vector4 Color)
{
    // Neutral fallback for old publishers, never a hard-coded orange route.
    public static readonly Vector4 LegacyColor = new(.78f, .88f, 1, 1);
    public Vector4 Arcs => new(Progress, GuideArc, LookAheadArc, 1);

    public static bool TryRead(JsonElement value, ReadOnlySpan<Vector3> path, out NavigationVisual? visual)
    {
        visual = null;
        if (path.Length is < 2 or > 512 || value.ValueKind != JsonValueKind.Object) return false;
        foreach (var point in path)
            if (!MathEx.Finite(point) || MathF.Abs(point.X) > 5000 || MathF.Abs(point.Y) > 5000 || MathF.Abs(point.Z) > 5000) return false;
        try
        {
            var progress = value.GetProperty("progress").GetSingle();
            var guide = value.GetProperty("guideArc").GetSingle();
            var ahead = value.GetProperty("lookAheadArc").GetSingle();
            var color = value.GetProperty("color");
            if (color.GetArrayLength() != 4) return false;
            var tint = new Vector4(color[0].GetSingle(), color[1].GetSingle(), color[2].GetSingle(), color[3].GetSingle());
            if (!FiniteUnit(tint.X) || !FiniteUnit(tint.Y) || !FiniteUnit(tint.Z) || !FiniteUnit(tint.W)) return false;
            var length = 0f;
            for (var i = 1; i < path.Length; i++) length += Flat(path[i - 1], path[i]);
            var slack = Math.Max(.01f, length * .000001f);
            if (!ValidArc(progress, length + slack) || !ValidArc(guide, length + slack) || !ValidArc(ahead, length + slack)
                || MathF.Abs(guide - progress) > 40.01f || MathF.Abs(ahead - guide) > 40.01f) return false;
            var atGuide = At(path, Math.Min(guide, length));
            var atAhead = At(path, Math.Min(ahead, length));
            // Do not trust an unrelated point just because its arc is plausible.
            if (!Matches(value.GetProperty("guide"), atGuide) || !Matches(value.GetProperty("lookAhead"), atAhead)) return false;
            visual = new(Math.Min(progress, length), Math.Min(guide, length), Math.Min(ahead, length), atGuide, atAhead, tint);
            return true;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { return false; }
    }

    private static bool FiniteUnit(float value) => float.IsFinite(value) && value is >= 0 and <= 1;
    private static bool ValidArc(float value, float max) => float.IsFinite(value) && value >= 0 && value <= max;
    private static float Flat(Vector3 a, Vector3 b) => Vector2.Distance(new(a.X, a.Z), new(b.X, b.Z));
    private static Vector3 At(ReadOnlySpan<Vector3> path, float arc)
    {
        if (!(arc > 0)) return path[0];
        for (var i = 1; i < path.Length; i++)
        {
            var length = Flat(path[i - 1], path[i]);
            if (arc <= length) return length < 1e-4f ? path[i] : Vector3.Lerp(path[i - 1], path[i], arc / length);
            arc -= length;
        }
        return path[^1];
    }
    private static bool Matches(JsonElement value, Vector3 expected)
    {
        if (value.GetArrayLength() != 3) return false;
        var actual = new Vector3(value[0].GetSingle(), value[1].GetSingle(), value[2].GetSingle());
        return MathEx.Finite(actual) && Vector3.Distance(actual, expected) <= .05f;
    }
}
