using System.Numerics;

namespace XivSurface.Core;

public enum MusicVisualizationMode
{
    SpectrumCrown = 0, RadialRibbons = 1, SpiralFountain = 2, OrbitHalo = 3,
    HelixCanopy = 4, PrismBloom = 5, WaveDome = 6, StarFountain = 7,
    AuroraVeil = 8, ResonanceArches = 9,
}
public enum MusicRadialDirection { CenterOut, RimIn }
public readonly record struct MusicVertex(Vector3 Position, Vector4 Color);

/// <summary>Allocation-free local material-space triangle geometry. XZ is a
/// circular chart, Y is positive lift above the cloth, NOT world altitude.
/// The caller must sample the actual indexed cloth surface before rendering.
/// No billboards, floor guesses, map changes or rug-size changes occur here.</summary>
public static class MusicVisualization
{
    public const int BandCount = 32;
    public const int MaximumVertices = 2304;
    public const float MaximumRadius = 128;
    public const float MaximumHeight = 32;
    private const float Tau = 2 * MathF.PI;

    public static int RequiredCapacity(MusicVisualizationMode mode) => mode switch
    {
        MusicVisualizationMode.SpectrumCrown => 32 * 3 * 6,
        MusicVisualizationMode.RadialRibbons => 32 * 12 * 6,
        MusicVisualizationMode.SpiralFountain => 4 * 32 * 6,
        MusicVisualizationMode.OrbitHalo => 3 * 32 * 6,
        MusicVisualizationMode.HelixCanopy => 4 * 32 * 6,
        MusicVisualizationMode.PrismBloom => 32 * 4 * 3,
        MusicVisualizationMode.WaveDome => 8 * 32 * 6,
        MusicVisualizationMode.StarFountain => 3 * 32 * 8 * 3,
        MusicVisualizationMode.AuroraVeil => 2 * 32 * 4 * 6,
        MusicVisualizationMode.ResonanceArches => 16 * 16 * 6,
        _ => 0,
    };

    /// <summary>Phase is radians. A full 2pi turn is continuous in every mode.
    /// Exact real band amplitudes get the explicit perceptual compression
    /// log(1+15a)/log(16); no bands are synthesized from aggregate levels.
    /// Invalid input or insufficient destination capacity returns zero WITHOUT
    /// writing a partial frame. The destination beyond the returned count is
    /// untouched and must not be submitted.</summary>
    public static int Build(Span<MusicVertex> destination, ReadOnlySpan<float> bands,
        MusicVisualizationMode mode, MusicRadialDirection direction,
        float radius, float height, double phase, float strength)
    {
        var required = RequiredCapacity(mode);
        if (required == 0 || destination.Length < required || bands.Length != BandCount
            || direction is not (MusicRadialDirection.CenterOut or MusicRadialDirection.RimIn)
            || !float.IsFinite(radius) || radius <= 0 || radius > MaximumRadius
            || !float.IsFinite(height) || height <= 0 || height > MaximumHeight
            || !double.IsFinite(phase) || !float.IsFinite(strength) || strength is <= 0 or > 1) return 0;
        Span<float> energy = stackalloc float[BandCount];
        var audible = false;
        for (var i = 0; i < BandCount; i++)
        {
            var amplitude = bands[i];
            if (!float.IsFinite(amplitude) || amplitude is < 0 or > 1) return 0;
            energy[i] = MathF.Log(1 + 15 * amplitude) / MathF.Log(16) * strength;
            audible |= amplitude > 0;
        }
        if (!audible) return 0;
        var angle = (float)(phase % Math.Tau);
        if (angle < 0) angle += Tau;
        var writer = new Writer(destination, height);
        switch (mode)
        {
            case MusicVisualizationMode.SpectrumCrown: Crown(ref writer, energy, direction, radius, height, angle); break;
            case MusicVisualizationMode.RadialRibbons: Ribbons(ref writer, energy, direction, radius, height, angle); break;
            case MusicVisualizationMode.SpiralFountain: Spiral(ref writer, energy, direction, radius, height, angle); break;
            case MusicVisualizationMode.OrbitHalo: Halo(ref writer, energy, direction, radius, height, angle); break;
            case MusicVisualizationMode.HelixCanopy: Helix(ref writer, energy, direction, radius, height, angle); break;
            case MusicVisualizationMode.PrismBloom: Bloom(ref writer, energy, direction, radius, height, angle); break;
            case MusicVisualizationMode.WaveDome: Dome(ref writer, energy, direction, radius, height, angle); break;
            case MusicVisualizationMode.StarFountain: Stars(ref writer, energy, direction, radius, height, angle); break;
            case MusicVisualizationMode.AuroraVeil: Veil(ref writer, energy, direction, radius, height, angle); break;
            case MusicVisualizationMode.ResonanceArches: Arches(ref writer, energy, direction, radius, height, angle); break;
        }
        return writer.Count;
    }

    private static void Crown(ref Writer w, scoped ReadOnlySpan<float> e, MusicRadialDirection direction, float r, float h, float phase)
    {
        for (var ring = 0; ring < 3; ring++)
        {
            var travel = Fraction(phase / Tau + ring / 3f);
            var fade = MathF.Pow(MathF.Sin(MathF.PI * travel), 2);
            var radius = r * (.16f + .78f * Directed(travel, direction));
            for (var i = 0; i < BandCount; i++)
            {
                if (e[i] == 0) continue;
                var a = Tau * i / BandCount;
                var y = h * e[i] * fade;
                var color = Color(i / 32f, e[i], fade);
                w.Quad(Polar(radius, a - .055f, 0), Polar(radius, a + .055f, 0),
                    Polar(radius, a + .027f, y), Polar(radius, a - .027f, y), color, color);
            }
        }
    }

    private static void Ribbons(ref Writer w, scoped ReadOnlySpan<float> e, MusicRadialDirection direction, float r, float h, float phase)
    {
        for (var band = 0; band < BandCount; band++)
        {
            if (e[band] == 0) continue;
            var angle = Tau * band / BandCount;
            for (var segment = 0; segment < 12; segment++)
            {
                var u = segment / 12f; var v = (segment + 1) / 12f;
                var first = RibbonHeight(u, e[band], direction, h, phase);
                var second = RibbonHeight(v, e[band], direction, h, phase);
                var color = Color(band / 32f, e[band], .8f);
                w.Quad(Polar(r * (.08f + .9f * u), angle - .025f, first),
                    Polar(r * (.08f + .9f * v), angle - .025f, second),
                    Polar(r * (.08f + .9f * v), angle + .025f, second),
                    Polar(r * (.08f + .9f * u), angle + .025f, first), color, color);
            }
        }
    }

    private static float RibbonHeight(float u, float energy, MusicRadialDirection direction, float height, float phase)
    {
        var radialPhase = direction == MusicRadialDirection.CenterOut ? Tau * u - phase : Tau * u + phase;
        return height * energy * MathF.Sin(MathF.PI * u) * (.15f + .85f * (.5f + .5f * MathF.Cos(radialPhase)));
    }

    private static void Spiral(ref Writer w, scoped ReadOnlySpan<float> e, MusicRadialDirection direction, float r, float h, float phase)
    {
        for (var arm = 0; arm < 4; arm++)
        for (var segment = 0; segment < 32; segment++)
        {
            var u = segment / 32f; var v = (segment + 1) / 32f;
            var firstEnergy = e[(segment + arm * 8) % 32];
            var secondEnergy = e[(segment + arm * 8 + 1) % 32];
            if (firstEnergy == 0 && secondEnergy == 0) continue;
            var a = arm * Tau / 4 + u * Tau * 1.25f + phase;
            var b = arm * Tau / 4 + v * Tau * 1.25f + phase;
            var first = RibbonHeight(u, firstEnergy, direction, h, phase);
            var second = RibbonHeight(v, secondEnergy, direction, h, phase);
            w.Quad(Polar(r * (.06f + .9f * u), a - .035f, first),
                Polar(r * (.06f + .9f * v), b - .035f, second),
                Polar(r * (.06f + .9f * v), b + .035f, second),
                Polar(r * (.06f + .9f * u), a + .035f, first),
                Color((segment + arm * 8) / 32f, firstEnergy, .8f), Color((segment + arm * 8 + 1) / 32f, secondEnergy, .8f));
        }
    }

    private static void Halo(ref Writer w, scoped ReadOnlySpan<float> e, MusicRadialDirection direction, float r, float h, float phase)
    {
        for (var ring = 0; ring < 3; ring++)
        {
            var travel = Fraction(phase / Tau + ring / 3f);
            var fade = MathF.Pow(MathF.Sin(MathF.PI * travel), 2);
            var radius = r * (.18f + .76f * Directed(travel, direction));
            for (var segment = 0; segment < 32; segment++)
            {
                var next = (segment + 1) % 32;
                if (e[segment] == 0 && e[next] == 0) continue;
                var a = Tau * segment / 32; var b = Tau * (segment + 1) / 32;
                var y1 = h * e[segment] * (.65f + .25f * MathF.Sin(a + phase)) * fade;
                var y2 = h * e[next] * (.65f + .25f * MathF.Sin(b + phase)) * fade;
                w.Quad(Polar(radius - r * .012f, a, y1), Polar(radius - r * .012f, b, y2),
                    Polar(radius + r * .012f, b, y2), Polar(radius + r * .012f, a, y1),
                    Color(segment / 32f, e[segment], fade), Color((segment + 1) / 32f, e[next], fade));
            }
        }
    }

    // Four ascending, flared helices. Frequency follows each strip rather than
    // inventing bins from an aggregate level; traveling crests follow the
    // selected radial direction while the material footprint stays fixed.
    private static void Helix(ref Writer w, scoped ReadOnlySpan<float> e, MusicRadialDirection direction, float r, float h, float phase)
    {
        for (var arm = 0; arm < 4; arm++)
        for (var segment = 0; segment < 32; segment++)
        {
            var band = (segment + arm * 8) % 32;
            var next = (band + 1) % 32;
            if (e[band] == 0 && e[next] == 0) continue;
            var u = segment / 32f; var v = (segment + 1) / 32f;
            var a = arm * Tau / 4 + u * Tau + phase;
            var b = arm * Tau / 4 + v * Tau + phase;
            var r1 = r * (.18f + .75f * MathF.Sin(MathF.PI * .8f * u));
            var r2 = r * (.18f + .75f * MathF.Sin(MathF.PI * .8f * v));
            var y1 = h * e[band] * (.15f + .8f * u) * (.4f + .6f * TravelingCrest(u, direction, phase));
            var y2 = h * e[next] * (.15f + .8f * v) * (.4f + .6f * TravelingCrest(v, direction, phase));
            w.Quad(Polar(r1, a - .035f, y1), Polar(r2, b - .035f, y2),
                Polar(r2, b + .035f, y2), Polar(r1, a + .035f, y1),
                Color(band / 32f, e[band], .85f), Color(next / 32f, e[next], .85f));
        }
    }

    // Individually faceted four-sided petals: a low diamond perimeter and a
    // frequency-driven ridge, not a camera-facing sprite or another ring.
    private static void Bloom(ref Writer w, scoped ReadOnlySpan<float> e, MusicRadialDirection direction, float r, float h, float phase)
    {
        for (var band = 0; band < 32; band++)
        {
            if (e[band] == 0) continue;
            var angle = Tau * band / 32 + phase;
            var lift = h * e[band];
            var ridge = Polar(r * .52f, angle, lift * (.3f + .65f * TravelingCrest(.52f, direction, phase)));
            var inner = Polar(r * .12f, angle, lift * .08f * TravelingCrest(.12f, direction, phase));
            var left = Polar(r * .5f, angle - .075f, lift * .16f * TravelingCrest(.5f, direction, phase));
            var outer = Polar(r * .94f, angle, lift * .05f * TravelingCrest(.94f, direction, phase));
            var right = Polar(r * .5f, angle + .075f, left.Y);
            var color = Color(band / 32f, e[band], .85f);
            w.Triangle(inner, left, ridge, color);
            w.Triangle(left, outer, ridge, color);
            w.Triangle(outer, right, ridge, color);
            w.Triangle(right, inner, ridge, color);
        }
    }

    // A continuous eight-ring faceted dome. Each angular endpoint retains its
    // own real band amplitude; direction reverses the traveling radial swell.
    private static void Dome(ref Writer w, scoped ReadOnlySpan<float> e, MusicRadialDirection direction, float r, float h, float phase)
    {
        for (var ring = 0; ring < 8; ring++)
        for (var band = 0; band < 32; band++)
        {
            var next = (band + 1) % 32;
            if (e[band] == 0 && e[next] == 0) continue;
            var u = ring / 8f; var v = (ring + 1) / 8f;
            var a = Tau * band / 32; var b = Tau * (band + 1) / 32;
            var y1 = h * MathF.Sqrt(MathF.Max(0, 1 - u * u)) * (.2f + .8f * TravelingCrest(u, direction, phase));
            var y2 = h * MathF.Sqrt(MathF.Max(0, 1 - v * v)) * (.2f + .8f * TravelingCrest(v, direction, phase));
            w.Quad(Polar(r * (.025f + .935f * u), a, y1 * e[band]),
                Polar(r * (.025f + .935f * u), b, y1 * e[next]),
                Polar(r * (.025f + .935f * v), b, y2 * e[next]),
                Polar(r * (.025f + .935f * v), a, y2 * e[band]),
                Color(band / 32f, e[band], .65f), Color(next / 32f, e[next], .65f));
        }
    }

    // Three staggered streams of genuinely volumetric octahedral stars. Each
    // individual stream vanishes smoothly at its radial reset, including Y;
    // reversing direction returns the stars toward the center of the cloth.
    private static void Stars(ref Writer w, scoped ReadOnlySpan<float> e, MusicRadialDirection direction, float r, float h, float phase)
    {
        for (var stream = 0; stream < 3; stream++)
        for (var band = 0; band < 32; band++)
        {
            if (e[band] == 0) continue;
            var travel = Fraction(phase / Tau + stream / 3f + band / 32f);
            var fade = MathF.Pow(MathF.Sin(MathF.PI * travel), 2);
            var angle = Tau * band / 32 + phase;
            var center = Polar(r * (.12f + .78f * Directed(travel, direction)), angle,
                h * e[band] * (.08f + .82f * 4 * travel * (1 - travel)) * fade);
            var width = r * .018f * fade;
            var tall = h * e[band] * .06f * fade;
            var top = center + new Vector3(0, tall, 0);
            var bottom = center - new Vector3(0, tall, 0);
            var a = center + new Vector3(width, 0, 0);
            var b = center + new Vector3(0, 0, width);
            var c = center - new Vector3(width, 0, 0);
            var d = center - new Vector3(0, 0, width);
            var color = Color(band / 32f, e[band], fade);
            w.Triangle(top, a, b, color); w.Triangle(top, b, c, color);
            w.Triangle(top, c, d, color); w.Triangle(top, d, a, color);
            w.Triangle(bottom, b, a, color); w.Triangle(bottom, c, b, color);
            w.Triangle(bottom, d, c, color); w.Triangle(bottom, a, d, color);
        }
    }

    // Two perpendicular serpentine sheets, not camera-facing sprites. Every
    // endpoint uses its own real band; adjacent energies only interpolate along
    // that material span. The four vertical strips gently pleat while the top
    // edge carries a genuinely radial crest. Gain never changes local XZ.
    private static void Veil(ref Writer w, scoped ReadOnlySpan<float> e, MusicRadialDirection direction, float r, float h, float phase)
    {
        for (var curtain = 0; curtain < 2; curtain++)
        for (var band = 0; band < BandCount; band++)
        {
            var next = (band + 1) % BandCount;
            if (e[band] == 0 && e[next] == 0) continue;
            var u = band / 32f; var v = (band + 1) / 32f;
            for (var strip = 0; strip < 4; strip++)
            {
                var lower = strip / 4f; var upper = (strip + 1) / 4f;
                w.Quad(VeilPoint(u, lower, e[band], curtain, direction, r, h, phase),
                    VeilPoint(v, lower, e[next], curtain, direction, r, h, phase),
                    VeilPoint(v, upper, e[next], curtain, direction, r, h, phase),
                    VeilPoint(u, upper, e[band], curtain, direction, r, h, phase),
                    Color(band / 32f, e[band], .65f), Color(next / 32f, e[next], .65f));
            }
        }
    }

    private static Vector3 VeilPoint(float u, float vertical, float energy, int curtain,
        MusicRadialDirection direction, float r, float h, float phase)
    {
        var x = .86f * (2 * u - 1);
        var z = .14f * MathF.Sin(Tau * u);
        var radial = MathF.Sqrt(x * x + z * z);
        var top = h * energy * (.25f + .75f * TravelingCrest(radial, direction, phase));
        // The top and bottom stay on the original serpentine path, so its
        // radial coordinate and crest do not depend on the pleating phase.
        var pleat = vertical is 0 or 1 ? 0 : .035f * MathF.Sin(MathF.PI * vertical)
            * MathF.Sin(MathF.PI * u) * MathF.Sin(Tau * vertical + Tau * u + phase);
        z += pleat;
        return curtain == 0 ? new(r * x, top * vertical, r * z) : new(-r * z, top * vertical, r * x);
    }

    // Sixteen open bridge ribs span the material circle. Opposite real bands
    // own each rib's endpoints; their explicit linear interpolation is not an
    // invented aggregate spectrum. Radial crests travel on both halves of each
    // arch, independently of its fixed width/footprint and raised arch shape.
    private static void Arches(ref Writer w, scoped ReadOnlySpan<float> e, MusicRadialDirection direction, float r, float h, float phase)
    {
        for (var arch = 0; arch < 16; arch++)
        {
            var left = e[arch]; var right = e[arch + 16];
            if (left == 0 && right == 0) continue;
            var angle = MathF.PI * arch / 16;
            var axis = new Vector3(MathF.Cos(angle), 0, MathF.Sin(angle));
            var side = new Vector3(-axis.Z, 0, axis.X) * (r * .014f);
            for (var segment = 0; segment < 16; segment++)
            {
                var u = segment / 16f; var v = (segment + 1) / 16f;
                var firstEnergy = left * (1 - u) + right * u;
                var secondEnergy = left * (1 - v) + right * v;
                var first = axis * (r * .94f * (2 * u - 1));
                var second = axis * (r * .94f * (2 * v - 1));
                first.Y = ArchHeight(u, firstEnergy, direction, h, phase);
                second.Y = ArchHeight(v, secondEnergy, direction, h, phase);
                w.Quad(first - side, second - side, second + side, first + side,
                    Color((arch + 16 * u) / 32f, firstEnergy, .8f),
                    Color((arch + 16 * v) / 32f, secondEnergy, .8f));
            }
        }
    }

    private static float ArchHeight(float u, float energy, MusicRadialDirection direction, float height, float phase)
        => height * energy * Math.Max(0, MathF.Sin(MathF.PI * u))
            * (.3f + .7f * TravelingCrest(Math.Abs(2 * u - 1), direction, phase));

    private static float TravelingCrest(float radius, MusicRadialDirection direction, float phase)
        => .5f + .5f * MathF.Cos(Tau * radius + (direction == MusicRadialDirection.CenterOut ? -phase : phase));
    private static float Fraction(float x) => x - MathF.Floor(x);
    private static float Directed(float t, MusicRadialDirection direction) => direction == MusicRadialDirection.CenterOut ? t : 1 - t;
    private static Vector3 Polar(float radius, float angle, float height) => new(radius * MathF.Cos(angle), height, radius * MathF.Sin(angle));
    private static Vector4 Color(float band, float energy, float fade)
    {
        var angle = Tau * band;
        return new(.5f + .5f * MathF.Cos(angle), .5f + .5f * MathF.Cos(angle - Tau / 3),
            .5f + .5f * MathF.Cos(angle + Tau / 3), Math.Clamp(energy * fade, 0, 1));
    }

    private ref struct Writer(Span<MusicVertex> destination, float height)
    {
        private Span<MusicVertex> output = destination;
        private readonly float maximumHeight = height;
        public int Count;
        public void Triangle(Vector3 a, Vector3 b, Vector3 c, Vector4 color)
        { Add(a, color); Add(b, color); Add(c, color); }
        public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector4 first, Vector4 second)
        {
            Add(a, first); Add(b, second); Add(c, second);
            Add(a, first); Add(c, second); Add(d, first);
        }
        private void Add(Vector3 position, Vector4 color) => output[Count++] = new(position with { Y = Math.Clamp(position.Y, 0, maximumHeight) }, color);
    }
}
