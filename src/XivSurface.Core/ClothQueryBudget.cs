namespace XivSurface.Core;

/// <summary>
/// Bounded accounting for sampling work interrupted by a reported process GC
/// pause. This is not a thread CPU timer: pause counters can include suspension
/// coordination. Credit is limited to 6 ms and elapsed time is always capped at
/// 8 ms. Missing/invalid pause evidence never extends the normal 2 ms window.
/// Once expired, a budget stays expired until its owner starts a new update.
/// </summary>
public sealed class ClothQueryBudget
{
    public const double WorkMilliseconds = 2;
    public const double MaxPauseCreditMilliseconds = 6;
    public const double MaxElapsedMilliseconds = WorkMilliseconds + MaxPauseCreditMilliseconds;
    private double lastElapsed, lastPauseDelta;
    public bool Expired { get; private set; }
    public bool UsedPauseCredit { get; private set; }
    public double PauseCreditMilliseconds { get; private set; }

    public void Reset()
    {
        lastElapsed = lastPauseDelta = PauseCreditMilliseconds = 0;
        Expired = UsedPauseCredit = false;
    }

    public bool CanContinue(double elapsedMilliseconds, double pauseDeltaMilliseconds)
    {
        if (Expired) return false;
        if (!double.IsFinite(elapsedMilliseconds) || elapsedMilliseconds < lastElapsed
            || !double.IsFinite(pauseDeltaMilliseconds) || pauseDeltaMilliseconds < lastPauseDelta
            || elapsedMilliseconds >= MaxElapsedMilliseconds)
            return Expire();
        lastElapsed = elapsedMilliseconds;
        lastPauseDelta = pauseDeltaMilliseconds;
        if (elapsedMilliseconds < WorkMilliseconds) return true;
        // An increment larger than this whole interval cannot establish a
        // contained pause. Refuse credit instead of creating negative work.
        if (pauseDeltaMilliseconds > elapsedMilliseconds) return Expire();
        var credit = Math.Min(pauseDeltaMilliseconds, MaxPauseCreditMilliseconds);
        if (elapsedMilliseconds - credit >= WorkMilliseconds) return Expire();
        UsedPauseCredit = true;
        PauseCreditMilliseconds = Math.Max(PauseCreditMilliseconds, credit);
        return true;
    }

    private bool Expire() { Expired = true; return false; }
}
