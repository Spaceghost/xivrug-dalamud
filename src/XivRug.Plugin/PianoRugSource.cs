using System.Numerics;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using XivSurface.Core;

namespace XivRug.Plugin;

/// <summary>Framework-thread-only polling. XivPiano remains entirely optional;
/// no reflected plugin references, stream access, playback or audio capture.</summary>
internal sealed class PianoRugSource
{
    private readonly ICallGateSubscriber<string> features;
    private readonly PianoClothResponse response = new();
    private readonly MusicSpectrumConsumer spectrum = new();
    private volatile MusicFrame? musicFrame;
    private bool visualEnabled;
    private double nextPoll;
    public string Status => visualEnabled ? spectrum.Status : response.Status;
    public ReadOnlySpan<ClothImpulse> Impulses => response.Impulses;
    public MusicFrame? CurrentMusic => musicFrame;
    // One owned copy is exchanged on the framework thread. The render thread
    // never reads or updates the consumer's mutable smoothing buffers.
    internal sealed class MusicFrame(double capturedAt, double expiresAt, ReadOnlySpan<float> bands)
    {
        private readonly float[] values = bands.ToArray();
        public ReadOnlySpan<float> At(double now) => double.IsFinite(now) && now >= capturedAt && now <= expiresAt ? values : [];
    }
    public PianoRugSource(IDalamudPluginInterface pi) => features = pi.GetIpcSubscriber<string>("XivPiano.AudioFeatures.v1");
    public void Reset() { nextPoll = 0; response.Reset(); spectrum.Reset(); musicFrame = null; }
    public void Update(double now, Vector2 center, bool enabled, float strength, bool visualizationEnabled = false)
    {
        visualEnabled = visualizationEnabled;
        if (!enabled) response.Reset();
        if (!visualizationEnabled) { spectrum.Reset(); musicFrame = null; }
        if (!enabled && !visualizationEnabled) { Reset(); return; }
        if (now >= nextPoll)
        {
            nextPoll = now + .05;
            string? json = null;
            try { json = features.InvokeFunc(); }
            catch { nextPoll = now + 1; }
            if (enabled) response.Update(json, now, center, strength);
            if (visualizationEnabled) spectrum.Update(json, now);
        }
        if (visualizationEnabled)
        {
            var values = spectrum.Sample(now);
            musicFrame = values.IsEmpty ? null : new(now, spectrum.ExpiresAt, values);
        }
    }
}
