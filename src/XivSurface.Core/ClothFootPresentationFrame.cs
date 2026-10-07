namespace XivSurface.Core;

/// <summary>Observed native frame, not a plugin draw counter. Both the game
/// update and the actual swapchain presentation epoch must still agree.</summary>
public readonly record struct ClothFootPresentationEpoch(nint SwapChain, uint PresentCount, uint FrameworkFrame)
{
    public bool Valid => SwapChain != 0;
}

/// <summary>Read-only diagnostic snapshot. Eligible describes the current
/// policy state; it neither acquires a lease nor reports a GPU submission.</summary>
public readonly record struct ClothFootPresentationInspection(string Phase, uint Zone,
    ClothFootPresentationEpoch CapturedEpoch, bool HasCapture, int CaptureThread,
    int ObservedThread, bool EpochMatches, bool ThreadMatches, bool Eligible, double SampleAge);

/// <summary>
/// Admission for a read made AFTER native skeleton finalization. A matching
/// epoch describes the frozen pose submitted for that scene; elapsed wall time
/// alone does not advance it. This class never grants that provenance to an
/// ordinary framework read. The adapter must observe the actual native phase,
/// framework-thread identity, and epoch on both sides of the native read.
/// </summary>
public sealed class ClothFootPresentationFrame : IDisposable
{
    private enum Phase { Empty, Updating, Ready, Finalizing, Finalized, Consumed }
    private readonly object gate = new();
    private Phase phase;
    private long version;
    private bool disposed;
    private ClothFootCaptureIdentity owner;
    private ClothFootPresentationEpoch epoch;
    private ClothFootRenderFrame? frame;
    private double completedAt;
    private int finalizationThread;

    public void BeginFramework(ClothFootCaptureIdentity identity)
    {
        lock (gate)
        {
            Clear();
            if (disposed || !identity.Bound) return;
            owner = identity;
            phase = Phase.Updating;
        }
    }

    public void CompleteFramework(bool succeeded)
    {
        lock (gate)
        {
            if (disposed || phase != Phase.Updating || !succeeded) { Clear(); return; }
            phase = Phase.Ready;
        }
    }

    /// <summary>Called BEFORE the native finalization original. Invalidates
    /// any preceding native pass, including repeated finalizations in a tick.</summary>
    public long BeginFinalization(ClothFootPresentationEpoch observed, bool observedFrameworkThread)
    {
        lock (gate)
        {
            if (disposed || phase != Phase.Ready || !observed.Valid || !observedFrameworkThread) { Clear(); return 0; }
            frame = null;
            epoch = observed;
            finalizationThread = Environment.CurrentManagedThreadId;
            phase = Phase.Finalizing;
            return ++version;
        }
    }

    public bool CompleteFinalization(long ticket, ClothFootPresentationEpoch observed,
        ClothFootRenderFrame? captured, double readCompletedAt)
    {
        lock (gate)
        {
            // An obsolete callback must not revoke a newer framework cycle.
            if (disposed || ticket == 0 || ticket != version || phase != Phase.Finalizing) return false;
            if (Environment.CurrentManagedThreadId != finalizationThread
                || observed != epoch || captured is null || captured.CaptureIdentity != owner
                || !double.IsFinite(readCompletedAt) || readCompletedAt < captured.SampledAt
                || readCompletedAt > captured.SampledAt + ClothFootRenderFrame.MaximumAgeSeconds)
            { Clear(); return false; }
            Span<ClothFootContact> contacts = stackalloc ClothFootContact[ClothFootClearance.MaximumContacts];
            if (!captured.TryGetClothContacts(owner.Zone, readCompletedAt, contacts, out _))
            { Clear(); return false; }
            frame = captured;
            completedAt = readCompletedAt;
            phase = Phase.Finalized;
            return true;
        }
    }

    /// <summary>
    /// Caller already owns its render-resource lock, then acquires this lease.
    /// Framework/native capture paths take ONLY this policy lock, never the
    /// render-resource lock. Keep the lease until GPU submission has finished;
    /// dispose it on the acquiring thread even when preparation throws.
    /// Each finalized observation may authorize one submission attempt only.
    /// </summary>
    public bool TryAcquire(uint zone, ClothFootPresentationEpoch observed, double now, out Lease? lease)
    {
        lease = null;
        Monitor.Enter(gate);
        // Global counters are insufficient for an asynchronous render queue:
        // the CPU may already have finalized N+1 before N's Present. Until a
        // native command-buffer token is available, require serial execution
        // on the observed producer thread as well as both matching epochs.
        if (disposed || phase != Phase.Finalized || Environment.CurrentManagedThreadId != finalizationThread
            || frame is null || owner.Zone != zone || observed != epoch
            || !double.IsFinite(now) || now < completedAt)
        { Monitor.Exit(gate); return false; }
        phase = Phase.Consumed;
        lease = new(this, frame, epoch, version);
        return true;
    }

    public ClothFootPresentationInspection Inspect(ClothFootPresentationEpoch observed, double now)
    {
        lock (gate)
        {
            var thread = Environment.CurrentManagedThreadId;
            var sameEpoch = observed.Valid && epoch.Valid && observed == epoch;
            var sameThread = finalizationThread != 0 && thread == finalizationThread;
            var validClock = double.IsFinite(now) && frame is not null && now >= completedAt;
            return new(disposed ? "Disposed" : phase.ToString(), owner.Zone, epoch, frame is not null,
                finalizationThread, thread, sameEpoch, sameThread,
                !disposed && phase == Phase.Finalized && frame is not null && sameEpoch && sameThread && validClock,
                validClock ? now - frame!.SampledAt : double.NaN);
        }
    }

    public void Invalidate() { lock (gate) Clear(); }
    public void Dispose() { lock (gate) { disposed = true; Clear(); } }

    private void Clear()
    {
        version++;
        phase = Phase.Empty; owner = default; epoch = default; frame = null; completedAt = 0; finalizationThread = 0;
    }

    public sealed class Lease : IDisposable
    {
        private ClothFootPresentationFrame? source;
        private readonly long version;
        public ClothFootRenderFrame Frame { get; }
        public ClothFootPresentationEpoch Epoch { get; }
        internal Lease(ClothFootPresentationFrame source, ClothFootRenderFrame frame,
            ClothFootPresentationEpoch epoch, long version)
        { this.source = source; Frame = frame; Epoch = epoch; this.version = version; }

        /// <summary>Recheck the actual epoch immediately before submission.
        /// It never samples native skeletons or assigns a newer timestamp.</summary>
        public bool IsCurrent(ClothFootPresentationEpoch observed) => source is { disposed: false } active
            && Monitor.IsEntered(active.gate) && active.version == version && active.phase == Phase.Consumed
            && ReferenceEquals(active.frame, Frame) && Epoch == observed;

        public bool TryCopyContacts(ClothFootPresentationEpoch observed, Span<ClothFootContact> into)
        {
            into[..Math.Min(into.Length, ClothFootClearance.MaximumContacts)].Clear();
            if (into.Length < ClothFootClearance.MaximumContacts || !IsCurrent(observed)) return false;
            Frame.Contacts.CopyTo(into);
            return true;
        }

        public void Dispose()
        {
            if (source is not { } active) return;
            // A wrong-thread release is a programming error, not permission to
            // strand a lock while pretending the presentation lease ended.
            if (!Monitor.IsEntered(active.gate)) throw new SynchronizationLockException();
            source = null;
            Monitor.Exit(active.gate);
        }
    }
}
