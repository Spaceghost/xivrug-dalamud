using System.Text.Json;

namespace XivSurface.Core;

/// <summary>Strict optional spectrum extension of XivPiano.AudioFeatures.v1.
/// Equal logarithmic frequency intervals, low to high; values are actual
/// played post-gain PCM linear RMS band amplitudes, not inferred MIDI/levels.</summary>
public sealed class MusicSpectrumConsumer
{
    public const int BandCount = 32;
    public const double MaximumAge = .2;
    public const int MaximumJsonLength = 8192;
    public const double AttackSeconds = .045;
    public const double ReleaseSeconds = .18;
    private readonly float[] target = new float[BandCount], smoothed = new float[BandCount];
    private long stream, sequence;
    private double sampledAt, lastClock = double.NaN, lastSmooth = double.NaN;
    private bool hasCursor;
    public bool Available { get; private set; }
    public float MinimumHz { get; private set; }
    public float MaximumHz { get; private set; }
    public double ExpiresAt => Available ? sampledAt + MaximumAge : 0;
    public string Status { get; private set; } = "Waiting for XivPiano spectrum.";

    public void Reset()
    {
        Clear("Waiting for XivPiano spectrum."); hasCursor = false; stream = sequence = 0;
        lastClock = lastSmooth = double.NaN; sampledAt = 0;
    }

    public bool Update(string? json, double now)
    {
        if (!Clock(now)) return false;
        if (json is null || json.Length is 0 or > MaximumJsonLength)
        { Clear("XivPiano spectrum is unavailable."); return false; }
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.GetProperty("version").GetInt32() != 1) throw new FormatException();
            var nextStream = root.GetProperty("streamId").GetInt64(); var nextSequence = root.GetProperty("sequence").GetInt64();
            var age = root.GetProperty("ageSeconds").GetDouble();
            var position = root.GetProperty("positionSeconds").GetDouble();
            if (nextStream <= 0 || nextSequence <= 0 || !double.IsFinite(age) || age < 0
                || !double.IsFinite(position) || position < 0) throw new FormatException();
            if (!root.GetProperty("playing").GetBoolean() || !root.GetProperty("fresh").GetBoolean() || age > MaximumAge)
            { Clear("XivPiano spectrum is paused or stale."); return false; }
            if (!root.TryGetProperty("spectrum", out var spectrum))
            { Clear("This XivPiano frame has no spectrum."); return false; }
            var minimum = spectrum.GetProperty("minimumHz").GetSingle(); var maximum = spectrum.GetProperty("maximumHz").GetSingle();
            var values = spectrum.GetProperty("bands");
            if (minimum != 40 || !float.IsFinite(maximum) || maximum <= minimum || maximum > 16000
                || values.ValueKind != JsonValueKind.Array || values.GetArrayLength() != BandCount) throw new FormatException();
            Span<float> incoming = stackalloc float[BandCount]; var index = 0; var audible = false;
            foreach (var item in values.EnumerateArray())
            {
                var value = item.GetSingle();
                if (!float.IsFinite(value) || value is < 0 or > 1) throw new FormatException();
                incoming[index++] = value; audible |= value > 0;
            }
            if (hasCursor && nextStream == stream && nextSequence < sequence)
            {
                // A producer/plugin restart can reset its sequence while the
                // audio stream identity remains. Reject this transition once,
                // then let the next frame initialize without any old energy.
                Reset(); lastClock = now; Status = "Waiting for the current XivPiano cursor."; return false;
            }
            if (hasCursor && nextStream == stream && nextSequence == sequence)
                return !Sample(now).IsEmpty; // identical polling cannot rejuvenate an old sample
            var changed = !hasCursor || nextStream != stream;
            if (changed) { smoothed.AsSpan().Clear(); lastSmooth = double.NaN; }
            stream = nextStream; sequence = nextSequence; hasCursor = true;
            sampledAt = now - age; MinimumHz = minimum; MaximumHz = maximum;
            incoming.CopyTo(target);
            if (!audible) { Clear("XivPiano spectrum is silent."); return false; }
            Available = true; Status = "XivPiano · 32-band played PCM spectrum";
            return !Sample(now).IsEmpty;
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        { Clear("XivPiano spectrum format is unavailable."); return false; }
    }

    /// <summary>Call with the current monotonic clock before each submission.
    /// Expiry does not depend on another IPC update arriving. Repeated cursor
    /// reads cannot reset sampledAt; expired cursors require a newer sequence.</summary>
    public ReadOnlySpan<float> Sample(double now)
    {
        if (!Clock(now) || !Available) return [];
        if (now < sampledAt || now - sampledAt > MaximumAge)
        { Clear("XivPiano spectrum is stale."); return []; }
        if (!double.IsFinite(lastSmooth)) target.CopyTo(smoothed, 0);
        else
        {
            var dt = now - lastSmooth;
            for (var i = 0; i < BandCount; i++)
            {
                var time = target[i] > smoothed[i] ? AttackSeconds : ReleaseSeconds;
                smoothed[i] = Math.Clamp(target[i] + (smoothed[i] - target[i]) * (float)Math.Exp(-dt / time), 0, 1);
            }
        }
        lastSmooth = now; return smoothed;
    }

    private bool Clock(double now)
    {
        if (!double.IsFinite(now) || now < 0 || double.IsFinite(lastClock) && now < lastClock)
        { Reset(); Status = "Waiting for a valid music clock."; return false; }
        lastClock = now; return true;
    }
    private void Clear(string status)
    {
        Available = false; target.AsSpan().Clear(); smoothed.AsSpan().Clear();
        MinimumHz = MaximumHz = 0; lastSmooth = double.NaN; Status = status;
    }
}
