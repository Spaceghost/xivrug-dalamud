using System.Numerics;
using System.Text.Json;

namespace XivSurface.Core;

/// <summary>Optional, versioned PCM feature consumer. No plugin assembly,
/// track metadata, playback controls or synthetic beat clock are involved.</summary>
public sealed class PianoClothResponse
{
    public const int MaximumImpulses = 16;
    public const double MaximumFeatureAge = .2;
    private readonly List<ClothImpulse> impulses = [];
    private long stream, sequence;
    private float lastOnset;
    private double previousTime = double.NaN, lastImpulse = double.NegativeInfinity;
    public string Status { get; private set; } = "Waiting for XivPiano audio.";
    public ReadOnlySpan<ClothImpulse> Impulses => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(impulses);

    public void Reset(string status = "XivPiano response is off.")
    {
        impulses.Clear(); stream = sequence = 0; lastOnset = 0;
        previousTime = double.NaN; lastImpulse = double.NegativeInfinity; Status = status;
    }

    public bool Update(string? json, double now, Vector2 center, float strength)
    {
        if (!double.IsFinite(now) || now < 0 || !MathEx.Finite(center) || !float.IsFinite(strength)
            || strength is < 0 or > 1 || double.IsFinite(previousTime) && now < previousTime)
        { Reset("Waiting for a valid cloth/audio clock."); return false; }
        previousTime = now;
        impulses.RemoveAll(value => now < value.Time || now - value.Time > 3);
        if (json is null || json.Length is 0 or > 4096)
        { Reset("XivPiano audio features are unavailable."); return false; }
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.GetProperty("version").GetInt32() != 1)
                throw new FormatException();
            var nextStream = root.GetProperty("streamId").GetInt64();
            var nextSequence = root.GetProperty("sequence").GetInt64();
            var age = root.GetProperty("ageSeconds").GetDouble();
            var position = root.GetProperty("positionSeconds").GetDouble();
            var playing = root.GetProperty("playing").GetBoolean();
            var fresh = root.GetProperty("fresh").GetBoolean();
            var onset = Unit(root, "onset"); var low = Unit(root, "low");
            var mid = Unit(root, "mid"); var high = Unit(root, "high");
            var rms = Unit(root, "rms"); _ = Unit(root, "peak");
            if (!double.IsFinite(age) || age < 0 || !double.IsFinite(position) || position < 0)
                throw new FormatException();
            if (!playing || !fresh || age > MaximumFeatureAge || strength == 0)
            { Reset(playing ? "Waiting for fresh audible XivPiano samples." : "XivPiano is quiet or paused."); return false; }
            if (nextStream <= 0 || nextSequence <= 0) throw new FormatException();
            if (stream != nextStream)
            { impulses.Clear(); stream = nextStream; sequence = 0; lastOnset = 0; lastImpulse = double.NegativeInfinity; }
            if (nextSequence < sequence)
            { Reset("XivPiano audio cursor changed; waiting for current samples."); return false; }
            Status = "XivPiano · cloth follows the audible music";
            if (nextSequence == sequence) return true;
            sequence = nextSequence;
            // Trigger a rising audible attack, not each poll of a held envelope.
            var attack = onset > .035f && onset > lastOnset + .015f;
            lastOnset = onset;
            if (!attack || rms < .0001f || now - lastImpulse < .09) return true;
            lastImpulse = now;
            var energy = Math.Clamp(onset * MathF.Sqrt(rms) * 4 * strength, 0, 1);
            if (energy <= .001f) return true;
            var tone = (high + mid * .45f) / Math.Max(.00001f, low + mid + high);
            if (impulses.Count == MaximumImpulses) impulses.RemoveAt(0);
            impulses.Add(new(center, now, energy, tone));
            return true;
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        { Reset("XivPiano audio feature format is unavailable."); return false; }
    }

    private static float Unit(JsonElement root, string name)
    {
        var value = root.GetProperty(name).GetSingle();
        if (!float.IsFinite(value) || value is < 0 or > 1) throw new FormatException();
        return value;
    }
}
