using System.Numerics;
using System.Text.Json;

namespace XivSurface.Core;

/// <summary>Optional Wayfinder JSON contract; no dependency on its assembly or on Ghostty.</summary>
public sealed record NavigationRoute(string Action, string Next, Vector3[] Points)
{
    public NavigationVisual? Visual { get; init; }
    public static bool TryRead(string? json, uint territory, long now, out NavigationRoute? route)
    {
        route = null;
        if (json is null || json.Length > 131072 || territory == 0) return false;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var root = doc.RootElement;
            if (root.GetProperty("version").GetInt32() != 1 || root.GetProperty("territoryId").GetUInt32() != territory) return false;
            var generated = root.GetProperty("generatedAt").GetInt64();
            if (generated < 0 || now < 0 || generated > now || now - generated > 2000) return false;
            var action = root.GetProperty("action").GetString();
            var next = root.GetProperty("next").GetString();
            if (action is null || next is null || action.Length > 512 || next.Length > 512) return false;
            var points = root.GetProperty("points");
            var count = points.GetArrayLength();
            var walkable = root.GetProperty("walkable").GetBoolean();
            if (count > 512 || (walkable ? count < 2 : count != 0)) return false;
            var output = new Vector3[count];
            var i = 0;
            foreach (var p in points.EnumerateArray())
            {
                if (p.GetArrayLength() != 3) return false;
                var v = new Vector3(p[0].GetSingle(), p[1].GetSingle(), p[2].GetSingle());
                if (!MathEx.Finite(v) || MathF.Abs(v.X) > 5000 || MathF.Abs(v.Y) > 5000 || MathF.Abs(v.Z) > 5000) return false;
                output[i++] = v;
            }
            NavigationVisual? visual = null;
            if (root.TryGetProperty("visual", out var metadata) &&
                (!walkable || !NavigationVisual.TryRead(metadata, output, out visual))) return false;
            route = new(action, next, output) { Visual = visual };
            return true;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { return false; }
    }
}
