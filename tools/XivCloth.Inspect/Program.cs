using System.Globalization;
using System.Net;
using System.Numerics;
using System.Text;
using System.Text.Json;

if (args is ["--self-test"]) return Checks.Run();
if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: ClothView snapshot.json new-output.svg | --self-test");
    return 2;
}
try
{
    var info = new FileInfo(args[0]);
    if (info.Length is <= 0 or > 1_048_576) throw new InvalidDataException("Snapshot exceeds the 1 MiB limit.");
    using var input = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
    var bytes = new byte[1_048_577];
    var count = input.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
    if (count == bytes.Length) throw new InvalidDataException("Snapshot exceeds the 1 MiB limit.");
    var snapshot = JsonSerializer.Deserialize<Snapshot>(bytes.AsSpan(0, count), new JsonSerializerOptions
        { PropertyNameCaseInsensitive = true, MaxDepth = 16 }) ?? throw new InvalidDataException("Missing snapshot.");
    var svg = Inspector.Draw(snapshot);
    using var output = new FileStream(args[1], FileMode.CreateNew, FileAccess.Write, FileShare.None);
    output.Write(Encoding.UTF8.GetBytes(svg));
    Console.WriteLine($"Wrote offline projection: {Path.GetFullPath(args[1])}");
    return 0;
}
catch (Exception error) when (error is IOException or JsonException or ArgumentException)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}

internal sealed record Snapshot
{
    public string Label { get; init; } = "";
    public string Status { get; init; } = "";
    public string SourceManifest { get; init; } = "";
    public long SceneGeneration { get; init; }
    public float[][] Positions { get; init; } = [];
    public float[][] Uv { get; init; } = [];
    public int[] Indices { get; init; } = [];
    public float[][][] Ground { get; init; } = [];
    public Foot[] Feet { get; init; } = [];
    public int[] CompressedVertices { get; init; } = [];
    public int[] TransitionFaces { get; init; } = [];
}

internal sealed record Foot
{
    public float[] A { get; init; } = [];
    public float[] B { get; init; } = [];
    public float Radius { get; init; }
    public string Model { get; init; } = "";
}

internal static class Inspector
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static string N(double value) => value.ToString("0.###", Inv);
    private static string Escape(string value) => WebUtility.HtmlEncode(value);
    private static Vector3 Point(float[]? values)
    {
        if (values is not { Length: 3 } || values.Any(v => !float.IsFinite(v) || Math.Abs(v) > 10000))
            throw new InvalidDataException("Invalid bounded XYZ coordinate.");
        return new(values[0], values[1], values[2]);
    }
    private static bool Text(string? value, int cap) => value is not null && value.Length <= cap;
    private static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool valid, string reason)
    { if (!valid) throw new InvalidDataException(reason); }

    internal static string Draw(Snapshot s)
    {
        Require(s.Status == "Ready", "Only an accepted Ready snapshot can be previewed as accepted cloth.");
        Require(Text(s.Label, 160) && Text(s.SourceManifest, 128) && s.SceneGeneration > 0, "Invalid snapshot metadata.");
        Require(s.Positions is { Length: >= 3 and <= 512 } && s.Indices is { Length: >= 3 and <= 3072 }
            && s.Indices.Length % 3 == 0 && s.Uv is not null && s.Uv.Length == s.Positions.Length,
            "Invalid bounded cloth topology.");
        var positions = s.Positions.Select(Point).ToArray();
        Require(s.Uv.All(uv => uv is { Length: 2 } && uv.All(float.IsFinite)), "Invalid UV coordinates.");
        var faces = new List<(int A, int B, int C)>(); var unique = new HashSet<(int, int, int)>();
        for (var i = 0; i < s.Indices.Length; i += 3)
        {
            var a = s.Indices[i]; var b = s.Indices[i + 1]; var c = s.Indices[i + 2];
            Require((uint)a < positions.Length && (uint)b < positions.Length && (uint)c < positions.Length,
                "Cloth index is outside the position array.");
            Require(a != b && a != c && b != c && Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]).LengthSquared() >= 1e-12f,
                "Degenerate cloth face.");
            var low = Math.Min(a, Math.Min(b, c)); var high = Math.Max(a, Math.Max(b, c));
            Require(unique.Add((low, a + b + c - low - high, high)), "Duplicate cloth face.");
            faces.Add((a, b, c));
        }
        Require(s.Ground is { Length: <= 128 } && s.Feet is { Length: <= 4 }, "Support/proxy cap exceeded.");
        var ground = s.Ground.Select(triangle =>
        {
            Require(triangle is { Length: 3 }, "Invalid support triangle.");
            var t = triangle.Select(Point).ToArray();
            Require(Vector3.Cross(t[1] - t[0], t[2] - t[0]).LengthSquared() >= 1e-12f, "Degenerate support triangle.");
            return t;
        }).ToArray();
        foreach (var foot in s.Feet)
        {
            Require(foot is not null && Text(foot.Model, 80), "Invalid proxy model label.");
            var a = Point(foot.A); var b = Point(foot.B);
            Require(float.IsFinite(foot.Radius) && foot.Radius is >= .02f and <= .34f
                && Vector3.DistanceSquared(a, b) <= .75f * .75f, "Invalid bounded foot capsule.");
        }
        Require(s.CompressedVertices is { Length: <= 512 } && s.CompressedVertices.All(i => (uint)i < positions.Length)
            && s.CompressedVertices.Distinct().Count() == s.CompressedVertices.Length, "Invalid compressed-vertex labels.");
        Require(s.TransitionFaces is { Length: <= 1024 } && s.TransitionFaces.All(i => (uint)i < faces.Count)
            && s.TransitionFaces.Distinct().Count() == s.TransitionFaces.Length, "Invalid transition-face labels.");

        var svg = new StringBuilder();
        svg.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"1440\" height=\"960\" viewBox=\"0 0 1440 960\">");
        svg.Append("<rect width=\"1440\" height=\"960\" fill=\"#101926\"/><g font-family=\"sans-serif\" fill=\"#ecf3fb\">");
        Line(28, 34, s.Label, 21); Line(28, 60, "OFFLINE SOLVER PROJECTION — not an in-game image or collision certificate", 13);
        Line(28, 82, $"{positions.Length} vertices / {faces.Count} full faces · generation {s.SceneGeneration} · caller status Ready", 13);
        Line(28, 103, $"Provenance label (not authenticated): {s.SourceManifest}", 11);
        DrawPanel(24, 124, 682, 490, "Orthographic view · true XYZ proportions", 1, true);
        DrawPanel(730, 124, 682, 490, "Side profile · height differences amplified ×10", 10, false);
        var minimum = positions.Min(p => p.Y); var maximum = positions.Max(p => p.Y);
        Line(28, 644, $"Cloth Y range {minimum:R} … {maximum:R}; span {(maximum - minimum):R} world units", 14);
        Line(28, 670, $"Cyan: material mesh · orange dots: {s.CompressedVertices.Length} labeled compressed vertices · violet: {s.TransitionFaces.Length} transition faces", 13);
        Line(28, 694, "Dashed outlines: declared foot proxies. All overlays are shown for inspection; this view does not simulate depth occlusion.", 12);
        var at = 722;
        foreach (var foot in s.Feet) { Line(28, at, $"Proxy: {foot.Model}; radius {foot.Radius:R}", 12); at += 22; }
        Line(28, 842, "Height exaggeration applies to both geometry and proxy silhouettes in the side profile. No geometry is flattened or dropped.", 12);
        Line(28, 866, "Missing classifications mean unlabeled vertices/faces, not proof of ordinary clearance. No frames or endpoints are interpolated.", 12);
        svg.Append("</g></svg>"); return svg.ToString();

        void Line(double x, double y, string text, int size) => svg.Append($"<text x=\"{N(x)}\" y=\"{N(y)}\" font-size=\"{size}\">{Escape(text)}</text>");
        void DrawPanel(int x, int y, int width, int height, string title, float heightScale, bool iso)
        {
            svg.Append($"<rect x=\"{x}\" y=\"{y}\" width=\"{width}\" height=\"{height}\" rx=\"8\" fill=\"#172638\"/>");
            Line(x + 16, y + 26, title, 15);
            var baseY = positions.Min(p => p.Y);
            Vector2 Project(Vector3 p)
            {
                var py = (p.Y - baseY) * heightScale;
                return iso ? new((p.X - p.Z) * .70710678f, (p.X + p.Z) * .40824829f - py * .81649658f) : new(p.X, -py);
            }
            // Include relevant support heights without zooming out to the
            // arbitrary extent of a huge floor triangle. This clipping only
            // chooses camera bounds: all original support faces are still drawn.
            var all = positions.Select(Project).ToList();
            var worldLo = new Vector2(positions.Min(p => p.X), positions.Min(p => p.Z));
            var worldHi = new Vector2(positions.Max(p => p.X), positions.Max(p => p.Z));
            foreach (var foot in s.Feet)
                foreach (var p in new[] { Point(foot.A), Point(foot.B) })
                {
                    var radius = new Vector2(foot.Radius, foot.Radius * heightScale);
                    all.Add(Project(p) - radius); all.Add(Project(p) + radius);
                    worldLo = Vector2.Min(worldLo, new Vector2(p.X, p.Z) - new Vector2(foot.Radius));
                    worldHi = Vector2.Max(worldHi, new Vector2(p.X, p.Z) + new Vector2(foot.Radius));
                }
            foreach (var triangle in ground)
                all.AddRange(ClipGroundForView(triangle, worldLo, worldHi).Select(Project));
            var lo = new Vector2(all.Min(p => p.X), all.Min(p => p.Y)); var hi = new Vector2(all.Max(p => p.X), all.Max(p => p.Y));
            var scale = Math.Min((width - 40) / Math.Max(.05, hi.X - lo.X), (height - 68) / Math.Max(.05, hi.Y - lo.Y));
            var center = (lo + hi) * .5f;
            Vector2 Screen(Vector3 p) => (Project(p) - center) * (float)scale + new Vector2(x + width * .5f, y + 40 + (height - 48) * .5f);
            string Points(IEnumerable<Vector3> points) => string.Join(" ", points.Select(Screen).Select(p => $"{N(p.X)},{N(p.Y)}"));
            var id = iso ? "iso" : "profile";
            svg.Append($"<defs><clipPath id=\"{id}\"><rect x=\"{x + 8}\" y=\"{y + 36}\" width=\"{width - 16}\" height=\"{height - 44}\"/></clipPath></defs><g clip-path=\"url(#{id})\">");
            foreach (var t in ground) svg.Append($"<polygon points=\"{Points(t)}\" fill=\"#657c56\" fill-opacity=\".14\" stroke=\"#6b875b\" stroke-width=\".7\"/>");
            var ordered = faces.Select((f, i) => (f, i)).OrderBy(pair => iso
                ? positions[pair.f.A].X + positions[pair.f.A].Y + positions[pair.f.A].Z
                    + positions[pair.f.B].X + positions[pair.f.B].Y + positions[pair.f.B].Z
                    + positions[pair.f.C].X + positions[pair.f.C].Y + positions[pair.f.C].Z : 0);
            var transitions = s.TransitionFaces.ToHashSet();
            foreach (var (f, i) in ordered)
                svg.Append($"<polygon points=\"{Points(new[] { positions[f.A], positions[f.B], positions[f.C] })}\" fill=\"{(transitions.Contains(i) ? "#bd9bef" : "#5bd6dd")}\" fill-opacity=\".3\" stroke=\"#6ddfe5\" stroke-width=\".7\"/>");
            foreach (var index in s.CompressedVertices)
            {
                var p = Screen(positions[index]); svg.Append($"<circle cx=\"{N(p.X)}\" cy=\"{N(p.Y)}\" r=\"2.7\" fill=\"#ffb169\"/>");
            }
            // Draw true projected capsule envelopes: orthographic circular
            // cross-section, or an ellipse after the explicit Y exaggeration.
            foreach (var foot in s.Feet)
            {
                var a = Screen(Point(foot.A)); var b = Screen(Point(foot.B)); var radius = foot.Radius * scale;
                svg.Append($"<path d=\"{CapsuleOutline(a, b, radius, heightScale)}\" fill=\"none\" stroke=\"#f2d49a\" stroke-width=\"1.6\" stroke-dasharray=\"5 4\"/>");
            }
            svg.Append("</g>");
        }
    }

    internal static string CapsuleOutline(Vector2 a, Vector2 b, double radius, double verticalStretch)
    {
        var dx = b.X - a.X; var dy = (b.Y - a.Y) / verticalStretch; var length = Math.Sqrt(dx * dx + dy * dy);
        var nx = length > 1e-12 ? -dy / length : 0; var ny = length > 1e-12 ? dx / length : 1;
        var ax = a.X + nx * radius; var ay = a.Y + ny * radius * verticalStretch;
        var bx = b.X + nx * radius; var by = b.Y + ny * radius * verticalStretch;
        var cx = b.X - nx * radius; var cy = b.Y - ny * radius * verticalStretch;
        var dx2 = a.X - nx * radius; var dy2 = a.Y - ny * radius * verticalStretch;
        // For this normal/order, negative sweep makes each cap go OUTWARD.
        return $"M{N(ax)} {N(ay)} L{N(bx)} {N(by)} A{N(radius)} {N(radius * verticalStretch)} 0 0 0 {N(cx)} {N(cy)} L{N(dx2)} {N(dy2)} A{N(radius)} {N(radius * verticalStretch)} 0 0 0 {N(ax)} {N(ay)} Z";
    }

    internal static List<Vector3> ClipGroundForView(Vector3[] triangle, Vector2 minimum, Vector2 maximum)
    {
        var polygon = triangle.ToList();
        Clip(p => p.X - minimum.X); Clip(p => maximum.X - p.X);
        Clip(p => p.Z - minimum.Y); Clip(p => maximum.Y - p.Z);
        return polygon;
        void Clip(Func<Vector3, double> distance)
        {
            if (polygon.Count == 0) return;
            var output = new List<Vector3>(polygon.Count + 1);
            for (var i = 0; i < polygon.Count; i++)
            {
                var a = polygon[i]; var b = polygon[(i + 1) % polygon.Count];
                var da = distance(a); var db = distance(b);
                if (da >= 0) output.Add(a);
                if ((da >= 0) != (db >= 0)) output.Add(Vector3.Lerp(a, b, (float)(da / (da - db))));
            }
            polygon = output;
        }
    }
}

internal static class Checks
{
    internal static int Run()
    {
        var count = 0;
        Snapshot Valid() => new() { Label = "synthetic inspector self-test", Status = "Ready", SceneGeneration = 1,
            Positions = [[0, 0, 0], [1, 0, 0], [0, .04f, 1]], Uv = [[0, 0], [1, 0], [0, 1]], Indices = [0, 1, 2] };
        void Good(Snapshot s) { if (!Inspector.Draw(s).EndsWith("</svg>")) throw new Exception("Invalid SVG output."); count++; }
        void Bad(Snapshot s)
        {
            try { Inspector.Draw(s); } catch (InvalidDataException) { count++; return; }
            throw new Exception("Malformed snapshot unexpectedly accepted.");
        }
        Good(Valid()); Good(Valid() with { Feet = [new() { A = [0, .1f, 0], B = [.1f, .1f, 0], Radius = .1f, Model = "test proxy" }] });
        var escaped = Inspector.Draw(Valid() with { Label = "<script>fake</script>" });
        if (escaped.Contains("<script>")) throw new Exception("Unescaped label."); count++;
        Bad(Valid() with { Status = "CollisionUnproven" }); Bad(Valid() with { SceneGeneration = 0 });
        Bad(Valid() with { Positions = [[float.NaN, 0, 0], [1, 0, 0], [0, 0, 1]] });
        Bad(Valid() with { Indices = [0, 1, 3] }); Bad(Valid() with { Indices = [0, 1, 1] });
        Bad(Valid() with { Indices = [0, 1, 2, 2, 1, 0] }); Bad(Valid() with { Uv = [] });
        Bad(Valid() with { Uv = null! }); Bad(Valid() with { Feet = [null!] });
        Bad(Valid() with { Positions = null! }); Bad(Valid() with { Ground = [null!] });
        Bad(Valid() with { Positions = [[0, 0, 0], [1, 0, 0], [2, 0, 0]] });
        Bad(Valid() with { CompressedVertices = [3] }); Bad(Valid() with { CompressedVertices = [0, 0] });
        Bad(Valid() with { TransitionFaces = [1] }); Bad(Valid() with { Ground = [[[0, 0, 0], [1, 0, 0]]] });
        Bad(Valid() with { Feet = [new() { A = [0, 0, 0], B = [0, 0, 0], Radius = 1 }] });
        CheckCapsule(Vector2.Zero, new(200, 0), 20, 1);
        CheckCapsule(Vector2.Zero, new(60, 800), 20, 10);
        CheckCapsule(new(40, -15), new(-40, 45), 20, 1);
        CheckCapsule(new(25, 30), new(25, 30), 20, 10);
        var floor = Inspector.ClipGroundForView([new(-100, 0, -100), new(0, 0, 100), new(100, 0, -100)], new(-1), new(1));
        if (floor.Count != 4 || floor.Any(p => p.Y != 0 || Math.Abs(p.X) > 1.00001f || Math.Abs(p.Z) > 1.00001f))
            throw new Exception("Local floor heights missing from view bounds."); count++;
        var distant = Inspector.ClipGroundForView([new(10, 0, 10), new(12, 0, 10), new(10, 0, 12)], new(-1), new(1));
        if (distant.Count != 0) throw new Exception("Distant floor changes local camera bounds."); count++;
        Console.WriteLine($"{count} inspector self-checks passed. No solver or native execution."); return 0;

        void CheckCapsule(Vector2 a, Vector2 b, double radius, double stretch)
        {
            var path = Inspector.CapsuleOutline(a, b, radius, stretch);
            var values = System.Text.RegularExpressions.Regex.Matches(path, @"-?\d+(?:\.\d+)?")
                .Select(match => double.Parse(match.Value, CultureInfo.InvariantCulture)).ToArray();
            if (values.Length != 20 || values[8] != 0 || values[17] != 0) throw new Exception("Invalid capsule arc flags.");
            var delta = new Vector2(b.X - a.X, (float)((b.Y - a.Y) / stretch));
            var tangent = delta.LengthSquared() > 1e-12 ? Vector2.Normalize(delta) : Vector2.UnitX;
            CheckArc(b, values[2], values[3], values[9], values[10], b.X + tangent.X * radius, b.Y + tangent.Y * radius * stretch);
            CheckArc(a, values[11], values[12], values[18], values[19], a.X - tangent.X * radius, a.Y - tangent.Y * radius * stretch);
            count++;
            void CheckArc(Vector2 center, double x1, double y1, double x2, double y2, double expectedX, double expectedY)
            {
                // Independently sample the negative-sweep ellipse encoded in
                // the actual path, then check its outward endpoint extreme.
                var start = Math.Atan2((y1 - center.Y) / (radius * stretch), (x1 - center.X) / radius);
                var end = Math.Atan2((y2 - center.Y) / (radius * stretch), (x2 - center.X) / radius);
                var turn = end - start; while (turn >= 0) turn -= 2 * Math.PI;
                var angle = start + turn * .5;
                if (Math.Abs(center.X + radius * Math.Cos(angle) - expectedX) > .003
                    || Math.Abs(center.Y + radius * stretch * Math.Sin(angle) - expectedY) > .003)
                    throw new Exception("Capsule cap bends inward or loses exaggerated scale.");
            }
        }
    }
}
