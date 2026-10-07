namespace XivSurface.Core;

/// <summary>Value-only framework observation; never dereferenced by a renderer.</summary>
public readonly record struct ClothFootCaptureIdentity(uint Zone, ulong PlayerId, nint Address, long ModelGeneration = 0)
{
    public bool Valid => Zone != 0 && PlayerId != 0 && Address != 0 && ModelGeneration >= 0;
    // Legacy three-field publications remain valid for their existing renderer.
    // Physical avatar contact requires an observed model generation as well.
    public bool Bound => Valid && ModelGeneration > 0;
}

/// <summary>Native identity values observed together; no pointer is dereferenced
/// by this type. Does not identify an equipment mesh or GPU animation frame.</summary>
public readonly record struct ClothFootModelObservation(ClothFootCaptureIdentity Actor,
    nint Draw, nint Skeleton, nint HavokSkeleton, int BoneCount)
{
    public bool Valid => Actor.Valid && Draw != 0 && Skeleton != 0 && HavokSkeleton != 0
        && BoneCount is > 0 and <= 1024;
}

/// <summary>Framework observer-owned lifecycle counter. Invalid observations
/// break continuity without recycling generations, even if addresses reappear.
/// Equal observed addresses/counts cannot detect an unobserved in-place rebuild.</summary>
public sealed class ClothFootModelTracker
{
    private ClothFootModelObservation observed;
    private bool hasObservation;
    private long generation;
    public ClothFootCaptureIdentity Current { get; private set; }
    public ClothFootCaptureIdentity Observe(ClothFootModelObservation value)
    {
        if (!value.Valid) { Invalidate(); return default; }
        value = value with { Actor = value.Actor with { ModelGeneration = 0 } };
        if (hasObservation && observed == value) return Current;
        if (generation == long.MaxValue) { Invalidate(); return default; }
        generation++;
        observed = value; hasObservation = true;
        return Current = value.Actor with { ModelGeneration = generation };
    }
    public void Invalidate() { hasObservation = false; observed = default; Current = default; }
}

/// <summary>
/// Framework-thread publication transaction. A late snapshot must come from a
/// new endpoint read after a successful update, not a new timestamp on the old
/// physics contacts. Caller publishes Current atomically to its render thread.
/// This does not attest to the game's GPU skeleton/evaluation frame.
/// </summary>
public sealed class ClothFootRenderPublisher
{
    private ClothFootCaptureIdentity updateIdentity, publishedIdentity;
    private bool updateOpen;
    private readonly double[] recentIntervals = new double[4];
    private int intervalCount, intervalIndex;
    private double previousCycleCaptureAt = double.NaN, previousReadCompletedAt = double.NaN;
    private double updateClothAge = ClothFootRenderFrame.MaximumAgeSeconds;
    private bool capturedThisUpdate;
    public ClothFootRenderFrame? Current { get; private set; }

    public ClothFootRenderFrame? BeginUpdate(ClothFootCaptureIdentity identity)
    {
        // An unclosed previous update cannot retain authorization. Otherwise a
        // still-fresh same-owner frame may render while framework work runs;
        // The immutable publication's expiry remains authoritative.
        if (updateOpen || !identity.Valid || identity != publishedIdentity)
        { Current = null; ResetCadence(); }
        updateIdentity = identity;
        updateOpen = true;
        capturedThisUpdate = false;
        return Current;
    }

    public ClothFootRenderFrame? CompleteUpdate(bool updateSucceeded,
        ClothFootCaptureIdentity beforeRead, ClothFootCaptureIdentity afterRead,
        double readStartedAt, double readCompletedAt, ReadOnlySpan<ClothFootContact> contacts)
    {
        if (!updateSucceeded)
        {
            Invalidate();
            return null;
        }
        var result = PublishEarlyCapture(beforeRead, afterRead, readStartedAt, readCompletedAt, contacts);
        updateOpen = false;
        return result;
    }

    /// <summary>
    /// Publish the actual early physics capture while terrain work runs. Native
    /// rendering may overlap that work, so it must not be forced to use the
    /// previous update's older endpoints. This does not complete the update;
    /// a failed body or late read must still clear even this fresh fallback.
    /// </summary>
    public ClothFootRenderFrame? PublishEarlyCapture(
        ClothFootCaptureIdentity beforeRead, ClothFootCaptureIdentity afterRead,
        double readStartedAt, double readCompletedAt, ReadOnlySpan<ClothFootContact> contacts)
    {
        if (!updateOpen || !updateIdentity.Valid || beforeRead != updateIdentity || afterRead != updateIdentity)
        {
            // A mid-update owner/scene change cannot be repaired by switching
            // back before the final capture: cloth may belong to the other one.
            Invalidate();
            return null;
        }
        Current = null;
        publishedIdentity = default;
        if (!double.IsFinite(readStartedAt) || readStartedAt < 0
            || !double.IsFinite(readCompletedAt) || readCompletedAt < readStartedAt
            || readCompletedAt - readStartedAt > ClothFootRenderFrame.MaximumAgeSeconds
            || double.IsFinite(previousReadCompletedAt) && readStartedAt < previousReadCompletedAt)
        { ResetCadence(); return null; }
        // Timestamp the START of the actual read. Slow captures already older
        // than33.33ms remain rejected. Only a NEW successful read can publish
        // a bounded cloth lifetime reflecting recent actual capture cadence.
        var maximumAge = CaptureAgeLimit(readStartedAt);
        var candidate = new ClothFootRenderFrame(updateIdentity.Zone, readStartedAt, contacts,
            clothMaximumAgeSeconds: maximumAge, captureIdentity: updateIdentity);
        Span<ClothFootContact> checkedContacts = stackalloc ClothFootContact[ClothFootClearance.MaximumContacts];
        if (!candidate.TryGetClothContacts(updateIdentity.Zone, readCompletedAt, checkedContacts, out _))
        { ResetCadence(); return null; }
        previousReadCompletedAt = readCompletedAt;
        publishedIdentity = updateIdentity;
        return Current = candidate;
    }

    public void Invalidate()
    {
        Current = null;
        updateOpen = false;
        updateIdentity = publishedIdentity = default;
        ResetCadence();
    }

    private double CaptureAgeLimit(double readStartedAt)
    {
        // Early and late reads in one framework update are one capture cycle.
        // Measuring their short internal gap would incorrectly restore a
        // 33ms limit on the final publication even at a steady23FPS.
        if (capturedThisUpdate) return updateClothAge;
        capturedThisUpdate = true;
        if (double.IsFinite(previousCycleCaptureAt) && readStartedAt > previousCycleCaptureAt)
        {
            recentIntervals[intervalIndex] = readStartedAt - previousCycleCaptureAt;
            intervalIndex = (intervalIndex + 1) % recentIntervals.Length;
            intervalCount = Math.Min(intervalCount + 1, recentIntervals.Length);
        }
        previousCycleCaptureAt = readStartedAt;
        var cadence = 0d;
        for (var i = 0; i < intervalCount; i++) cadence = Math.Max(cadence, recentIntervals[i]);
        updateClothAge = Math.Clamp(cadence * 1.5 + .005,
            ClothFootRenderFrame.MaximumAgeSeconds, ClothFootRenderFrame.MaximumAdaptiveClothAgeSeconds);
        return updateClothAge;
    }

    private void ResetCadence()
    {
        intervalCount = intervalIndex = 0;
        previousCycleCaptureAt = previousReadCompletedAt = double.NaN;
        capturedThisUpdate = false;
        updateClothAge = ClothFootRenderFrame.MaximumAgeSeconds;
    }
}
