using System.Diagnostics;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using XivSurface.Core;
using IDXGISwapChain = TerraFX.Interop.DirectX.IDXGISwapChain;
using NativeFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace XivRug.Plugin;

/// <summary>
/// Reads finalized feet on the verified game framework thread, never from a
/// D3D hook. Brio's SkeletonService identifies this original-first boundary as
/// the final skeleton view in Framework.TaskRenderGraphicsRender. We require
/// a unique installed signature and runtime thread/epoch continuity before
/// admitting it. Ordinary framework contacts remain the fallback.
/// </summary>
internal sealed unsafe class NativeFootFrameCapture : IDisposable
{
    // Brio's finalization signature, narrowed by the matched game's TLS load.
    // Matched executable: a single .text match at RVA 0x2BA1F0. Runtime scan is
    // authoritative; no address or generated offset is hard-coded below.
    private const string FinalizationSignature =
        "40 53 57 41 54 41 55 48 83 EC ?? 65 48 8B 04 25 58 00 00 00 4C";
    private delegate void FinalizeSkeletons(nint manager);
    private readonly Hook<FinalizeSkeletons> hook;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly CharacterFootContacts observer;
    private readonly ClothFootPresentationFrame presentation = new();
    private readonly object lifecycleGate = new();
    private int activeCallbacks, frameworkThread;
    private volatile bool disposed, faulted, enabled;
    private bool loggedThreadProof;
    private long captures, rejected, acquired, delayed, otherThread, maxDelayMicroseconds, nextDiagnostic;
    private long nextUiObservation;

    public NativeFootFrameCapture(IFramework framework, IObjectTable objects, IClientState client,
        ICondition conditions, IPluginLog log, IGameInteropProvider interop, ISigScanner scanner)
    {
        this.framework = framework; this.log = log;
        observer = new(objects, client, conditions, log);
        var matches = scanner.ScanAllText(FinalizationSignature).ToArray();
        if (matches.Length != 1 || matches[0] == 0)
            throw new NotSupportedException($"Native finalized-foot capture requires one exact skeleton boundary; found {matches.Length}.");
        hook = interop.HookFromAddress<FinalizeSkeletons>(matches[0], Detour);
        try { hook.Enable(); }
        catch { Dispose(); throw; }
    }

    /// <summary>Call before ANY Plugin.Update work, including invalidation or
    /// early returns. This path never acquires LiveRug's render-resource lock.</summary>
    public void BeginFramework(bool captureEnabled)
    {
        presentation.Invalidate();
        enabled = false;
        if (disposed || faulted || !framework.IsInFrameworkUpdateThread) return;
        try
        {
            frameworkThread = Environment.CurrentManagedThreadId;
            enabled = captureEnabled;
            presentation.BeginFramework(observer.CaptureIdentity(captureEnabled));
            Report();
        }
        catch (Exception error) { StopCapture(error); }
    }

    /// <summary>Call in Plugin.Update's outer finally, AFTER the actual final
    /// framework reread, with false if any work/capture setup failed.</summary>
    public void CompleteFramework(bool succeeded)
        => presentation.CompleteFramework(succeeded && enabled && !disposed && !faulted);

    /// <summary>Only from the verified native/backbuffer draw, after acquiring
    /// renderLock. Dispose the returned policy lease before releasing renderLock.
    /// Capture callbacks never wait for renderLock while owning the policy lock.</summary>
    public bool TryAcquire(uint zone, double now, Span<ClothFootContact> contacts,
        out ClothFootPresentationFrame.Lease? lease)
    {
        lease = null;
        contacts[..Math.Min(contacts.Length, ClothFootClearance.MaximumContacts)].Clear();
        if (disposed || faulted || !enabled) return false;
        if (!framework.IsInFrameworkUpdateThread || Environment.CurrentManagedThreadId != frameworkThread)
        { Interlocked.Increment(ref otherThread); return false; }
        if (!TryReadEpoch(out var epoch)
            || !presentation.TryAcquire(zone, epoch, now, out lease)) return false;
        if (!lease!.TryCopyContacts(epoch, contacts))
        { lease.Dispose(); lease = null; return false; }
        Interlocked.Increment(ref acquired);
        var age = now - lease!.Frame.SampledAt;
        if (age > ClothFootRenderFrame.MaximumAdaptiveClothAgeSeconds) Interlocked.Increment(ref delayed);
        var micros = (long)Math.Clamp(age * 1_000_000, 0, long.MaxValue);
        var previous = Interlocked.Read(ref maxDelayMicroseconds);
        while (micros > previous)
        {
            var observed = Interlocked.CompareExchange(ref maxDelayMicroseconds, micros, previous);
            if (observed == previous) break;
            previous = observed;
        }
        return true;
    }

    // Pass as part of the existing canSubmit guard; resource preparation may
    // block. Same render epoch and a still-owned lease must hold at submission.
    public bool CanSubmit(ClothFootPresentationFrame.Lease? lease)
        => !disposed && !faulted && enabled && lease is not null
            && TryReadEpoch(out var epoch) && lease.IsCurrent(epoch);

    /// <summary>Call from UiBuilder.Draw even while terrain hides the cloth.
    /// Samples phase/epoch metadata at most once per20seconds; no native bones,
    /// drawing lease, acquisition counter, target binding, or GPU work.</summary>
    public void ObserveUiBoundary()
    {
        if (disposed) return;
        var tick = Environment.TickCount64;
        var previous = Interlocked.Read(ref nextUiObservation);
        if (tick < previous || Interlocked.CompareExchange(ref nextUiObservation, tick + 20000, previous) != previous) return;
        try
        {
            var epochKnown = TryReadEpoch(out var observed);
            var sample = presentation.Inspect(observed, Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
            log.Information("XivRug UI-boundary foot observation (diagnostic only, no draw): enabled={Enabled}, faulted={Faulted}, frameworkThread={FrameworkThread}, observedThread={ObservedThread}, isFrameworkThread={OnFramework}, phase={Phase}, hasCapture={HasCapture}, captureThread={CaptureThread}, epochKnown={EpochKnown}, observedFrame={ObservedFrame}, capturedFrame={CapturedFrame}, observedPresent={ObservedPresent}, capturedPresent={CapturedPresent}, epochMatches={EpochMatches}, threadMatches={ThreadMatches}, policyEligible={Eligible}, sampleAge={Age:F2}ms",
                enabled, faulted, frameworkThread, sample.ObservedThread, framework.IsInFrameworkUpdateThread,
                sample.Phase, sample.HasCapture, sample.CaptureThread, epochKnown,
                observed.FrameworkFrame, sample.CapturedEpoch.FrameworkFrame, observed.PresentCount, sample.CapturedEpoch.PresentCount,
                sample.EpochMatches, sample.ThreadMatches, sample.Eligible, sample.SampleAge * 1000);
        }
        catch (Exception error)
        {
            // Diagnostic failure does not change capture or drawing policy.
            try { log.Warning(error, "XivRug UI-boundary foot observation unavailable; draw policy unchanged"); }
            catch (Exception) { }
        }
    }

    private void Detour(nint manager)
    {
        FinalizeSkeletons original;
        lock (lifecycleGate)
        {
            original = hook.OriginalDisposeSafe;
            activeCallbacks++;
        }
        long ticket = 0;
        try
        {
            try
            {
                if (!disposed && !faulted && enabled)
                {
                    if (!framework.IsInFrameworkUpdateThread || frameworkThread == 0
                        || Environment.CurrentManagedThreadId != frameworkThread)
                        StopCapture(new InvalidOperationException("Native finalization did not run on the observed framework thread."));
                    else if (TryReadEpoch(out var before)) ticket = presentation.BeginFinalization(before, observedFrameworkThread: true);
                    else presentation.Invalidate();
                }
            }
            catch (Exception error) { StopCapture(error); }
            // Never skip, duplicate, or replace the game's own operation.
            original(manager);
            try
            {
                if (ticket == 0 || disposed || faulted || !enabled) return;
                if (!TryReadEpoch(out var beforeRead)) { Reject(); return; }
                observer.Update(true);
                var capture = observer.BoundFrame;
                var completed = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
                if (!TryReadEpoch(out var afterRead) || beforeRead != afterRead
                    || !presentation.CompleteFinalization(ticket, afterRead, capture, completed))
                { Reject(); return; }
                Interlocked.Increment(ref captures);
                if (!loggedThreadProof)
                {
                    loggedThreadProof = true;
                    log.Information("XivRug finalized-foot capture observed after native original on framework thread {Thread}; game frame={Frame}, present={Present}, model generation={Generation}. Native drawing still requires this exact epoch.",
                        frameworkThread, afterRead.FrameworkFrame, afterRead.PresentCount, capture!.CaptureIdentity.ModelGeneration);
                }
            }
            catch (Exception error) { StopCapture(error); }
        }
        finally
        {
            lock (lifecycleGate)
                if (--activeCallbacks == 0) Monitor.PulseAll(lifecycleGate);
        }
    }

    private void Reject() { Interlocked.Increment(ref rejected); presentation.Invalidate(); }

    private void StopCapture(Exception error)
    {
        faulted = true;
        presentation.Invalidate();
        // Even a failed logger must not prevent the native continuation.
        try { log.Warning(error, "XivRug finalized-foot capture stopped; ordinary fresh framework contacts remain required"); }
        catch (Exception) { }
    }

    private static bool TryReadEpoch(out ClothFootPresentationEpoch epoch)
    {
        epoch = default;
        var state = NativeFramework.Instance(); var device = Device.Instance();
        if (state == null || state->IsDestroying || state->IsExiting || state->IsFreed
            || device == null || device->SwapChain == null || device->SwapChain->DXGISwapChain == null) return false;
        var before = state->FrameCounter;
        var swapChain = (IDXGISwapChain*)device->SwapChain->DXGISwapChain;
        uint present = 0; int result;
        swapChain->AddRef();
        try { result = swapChain->GetLastPresentCount(&present); }
        finally { swapChain->Release(); }
        if (result < 0 || state->FrameCounter != before || device->SwapChain == null
            || (nint)device->SwapChain->DXGISwapChain != (nint)swapChain) return false;
        epoch = new((nint)swapChain, present, before);
        return true;
    }

    private void Report()
    {
        var now = Environment.TickCount64;
        if (now < nextDiagnostic) return;
        nextDiagnostic = now + 20000;
        var captured = Interlocked.Exchange(ref captures, 0);
        var refused = Interlocked.Exchange(ref rejected, 0);
        var rendered = Interlocked.Exchange(ref acquired, 0);
        var held = Interlocked.Exchange(ref delayed, 0);
        var asynchronous = Interlocked.Exchange(ref otherThread, 0);
        var age = Interlocked.Exchange(ref maxDelayMicroseconds, 0) / 1000d;
        if (captured + refused + rendered + asynchronous != 0)
            log.Information("XivRug finalized-foot frame gate: captured={Captured}, rejected={Rejected}, acquired={Acquired}, matching delayed frames={Delayed}, other-thread refusals={OtherThread}, max queued age={Age:F2}ms. No wall-clock expiry is renewed; serial native frame and Present must match.",
                captured, refused, rendered, held, asynchronous, age);
    }

    public void Dispose()
    {
        disposed = true; enabled = false;
        // Do not call while holding LiveRug.renderLock. This can wait for a
        // synchronous draw lease, whose holder needs that lock to finish.
        presentation.Dispose();
        lock (lifecycleGate)
        {
            if (hook.IsDisposed) return;
            hook.Disable();
            while (activeCallbacks != 0) Monitor.Wait(lifecycleGate);
            hook.Dispose();
        }
    }
}
