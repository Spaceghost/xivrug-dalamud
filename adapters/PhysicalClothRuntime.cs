using System.Numerics;
using XivCloth.Core;

namespace XivRug.Rendering;

internal enum PhysicalOwnerStatus { Ready, AwaitingHistory, SceneUnavailable, FootUnavailable, SimulationRefused, PublicationRefused }
internal readonly record struct PhysicalOwnerResult(PhysicalOwnerStatus Status, XpbdAdvance Step);

/// <summary>Single-owner A-stage fixture controller. No native source, plugin
/// switch, music/follow mapping or renderer activation. The complete rest
/// material remains fixed; only accepted solver positions may move. Caller
/// supplies EVERY genuine early/late observation in order. All calls and the
/// final CanSubmit-to-draw section require the same owner serialization. The
/// injected clock is read-only and must not reenter this object. A missing or
/// refused interval retains history: recovery requires a new runtime and full
/// admission, not AdmitScene alone or silently skipped foot observations.</summary>
internal sealed class PhysicalClothRuntime
{
    private readonly XpbdCloth solver;
    private readonly PhysicalClothPresentation presentation = new();
    private readonly PhysicalClothChart chart;
    private readonly Func<double> clock;
    private readonly Func<bool> commitAllowed;
    private FixtureSceneLease? lease, committingLease;
    private FootProxyPose? previous, accepted, committingFeet;
    private PairedPlantObservation? acceptedPlant;
    private double checkedAt = double.NegativeInfinity;
    private bool readingClock;
    public PhysicalClothRuntime(ClothRestPattern pattern, Vector3 initialOffset,
        PhysicalClothChart chart, Func<double> clock, XpbdSettings? settings = null)
        :this(pattern,initialOffset,chart,clock,settings,ReadOnlySpan<Vector3>.Empty) { }
    public PhysicalClothRuntime(ClothRestPattern pattern, Vector3 initialOffset,
        PhysicalClothChart chart, Func<double> clock, XpbdSettings? settings,ReadOnlySpan<Vector3> initialMaterial)
    {
        if (!chart.Valid || chart.HalfSize != new Vector2(pattern.Width, pattern.Depth) * .5f
            || chart.Circle != (pattern.Shape == ClothPatternShape.Circle)
            || chart.Corner != (chart.Circle ? 0 : pattern.CornerRadius)
            || chart.Center != new Vector2(initialOffset.X, initialOffset.Z))
            throw new ArgumentException("The chart must describe the complete material pattern.");
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock)); this.chart = chart;
        solver = new(pattern.ToDefinition(), settings);
        var positions = initialMaterial.IsEmpty?pattern.Positions.ToArray():initialMaterial.ToArray();
        for (var i = 0; i < positions.Length; i++) positions[i] += initialOffset;
        solver.Reset(positions); commitAllowed = CanCommit;
    }
    public XpbdFrame Inspect() { GuardClockReentry(); return solver.Capture(); }
    public FootProxyPose? AcceptedObservation => accepted;
    public void Invalidate() { GuardClockReentry(); presentation.Invalidate(); lease = null; }
    public XpbdAdvance AdmitScene(FixtureSceneLease candidate)
    {
        GuardClockReentry();
        presentation.Invalidate();
        lease = null;
        var now = ReadClock();
        if (!candidate.CurrentAt(now) || previous is not null && previous.Identity.Zone != candidate.Zone)
            return new(XpbdStatus.RejectedInput, 0, 0, 0, 0);
        committingLease = candidate; committingFeet = null;
        try
        {
            var result = solver.RebindScene(candidate.Scene, commitAllowed);
            if (result.Status == XpbdStatus.Ready) lease = candidate;
            return result;
        }
        finally { committingLease = null; }
    }
    public PhysicalOwnerResult Observe(FixtureSceneLease current, FootProxyPose? actual)
    {
        GuardClockReentry();
        presentation.Invalidate();
        if(acceptedPlant is not null)return new(PhysicalOwnerStatus.FootUnavailable,default);
        var now = ReadClock();
        if (!ReferenceEquals(lease, current) || !current.CurrentAt(now)) return new(PhysicalOwnerStatus.SceneUnavailable, default);
        if (actual is null || actual.Identity.Zone != current.Zone || !actual.FreshAt(now))
            return new(PhysicalOwnerStatus.FootUnavailable, default);
        if (previous is null) { previous = actual; return new(PhysicalOwnerStatus.AwaitingHistory, default); }
        if (!MeasuredFootInterval.TryCreate(previous, actual, now, out var interval))
            return new(PhysicalOwnerStatus.FootUnavailable, default);
        committingLease = current; committingFeet = actual;
        XpbdAdvance result;
        try { result = solver.AdvanceMeasuredFeet(current.Scene, interval, now, inputs: null, canCommit: commitAllowed); }
        finally { committingLease = null; committingFeet = null; }
        if (result.Status != XpbdStatus.Ready) return new(PhysicalOwnerStatus.SimulationRefused, result);
        previous = accepted = actual;
        now = ReadClock();
        if (!current.CurrentAt(now)) return new(PhysicalOwnerStatus.SceneUnavailable, result);
        var observation = presentation.BeginObservation(actual, actual, current.Scene.Generation, now);
        var capture = solver.CaptureForFeet(actual, actual, now);
        now = ReadClock();
        if (!current.CurrentAt(now) || !presentation.TryPublish(observation, capture, chart, now))
        { presentation.Invalidate(); return new(PhysicalOwnerStatus.PublicationRefused, result); }
        return new(PhysicalOwnerStatus.Ready, result);
    }
    public bool TryCreatePlantPolicy(FixtureSceneLease current,FootProxyPose reference,
        ReadOnlySpan<int> leftTread,ReadOnlySpan<int> rightTread,out PairedPlantPolicy? policy)
    {
        GuardClockReentry();policy=null;var now=ReadClock();
        return current.CurrentAt(now)&&reference.Identity.Zone==current.Zone
            &&solver.TryCreatePlantPolicy(current.Scene,reference,leftTread,rightTread,out policy);
    }
    public XpbdAdvance AdmitPairedScene(FixtureSceneLease candidate,PairedPlantObservation actual)
    {
        GuardClockReentry();presentation.Invalidate();var now=ReadClock();
        if(!candidate.CurrentAt(now)||actual is null||actual.Actual.Identity.Zone!=candidate.Zone
            ||!ReferenceEquals(actual.Policy.Scene,candidate.Scene))return new(XpbdStatus.RejectedInput,0,0,0,0);
        committingLease=candidate;committingFeet=actual.Actual;
        try
        {
            var result=solver.AdmitPairedScene(actual,now,commitAllowed);
            if(result.Status==XpbdStatus.Ready)
            {lease=candidate;previous=accepted=actual.Actual;acceptedPlant=actual;}
            return result;
        }
        finally{committingLease=null;committingFeet=null;}
    }
    public PhysicalOwnerResult ObservePaired(FixtureSceneLease current,PairedPlantObservation actual)
    {
        GuardClockReentry();presentation.Invalidate();var now=ReadClock();
        if(!ReferenceEquals(current,lease)||!current.CurrentAt(now))return new(PhysicalOwnerStatus.SceneUnavailable,default);
        if(acceptedPlant is null||actual is null||!ReferenceEquals(actual.Policy.Scene,current.Scene)
            ||!PairedPlantInterval.TryCreate(acceptedPlant,actual,now,out var interval))return new(PhysicalOwnerStatus.FootUnavailable,default);
        committingLease=current;committingFeet=actual.Actual;
        XpbdAdvance result;
        try{result=solver.AdvanceMeasuredFeetWithPlants(interval!,now,canCommit:commitAllowed);}
        finally{committingLease=null;committingFeet=null;}
        if(result.Status!=XpbdStatus.Ready)return new(PhysicalOwnerStatus.SimulationRefused,result);
        previous=accepted=actual.Actual;acceptedPlant=actual;now=ReadClock();
        if(!current.CurrentAt(now))return new(PhysicalOwnerStatus.SceneUnavailable,result);
        var observation=presentation.BeginPairedObservation(actual,actual,current.Scene,now);
        var capture=solver.CaptureForPlantedFeet(actual,actual,now);now=ReadClock();
        if(!current.CurrentAt(now)||!presentation.TryPublish(observation,capture,chart,now))
        {presentation.Invalidate();return new(PhysicalOwnerStatus.PublicationRefused,result);}
        return new(PhysicalOwnerStatus.Ready,result);
    }
    public XpbdAdvance RemovePlantPolicy()
    {
        GuardClockReentry();presentation.Invalidate();var now=ReadClock();
        if(lease is null||!lease.CurrentAt(now))return new(XpbdStatus.RejectedInput,0,0,0,0);
        committingLease=lease;committingFeet=accepted;
        try
        {
            var result=solver.RemovePlantPolicy(now,commitAllowed);
            if(result.Status==XpbdStatus.Ready)acceptedPlant=null;
            return result;
        }
        finally{committingLease=null;committingFeet=null;}
    }
    public bool TryGet(uint zone, out PhysicalClothDrawFrame? packet)
    {
        GuardClockReentry();
        var now = ReadClock();
        if (lease is null || lease.Zone != zone || !lease.CurrentAt(now))
        { presentation.Invalidate(); packet = null; return false; }
        return presentation.TryGet(zone, now, out packet);
    }
    /// <summary>Required after GPU preparation, immediately before submission.
    /// Checks source revocation/expiry as well as exact publication identity.
    /// This is a CPU submission gate, not proof of GPU/display-time freshness.</summary>
    public bool CanSubmit(PhysicalClothDrawFrame? packet, uint zone)
    {
        GuardClockReentry();
        var now = ReadClock();
        if (lease is null || lease.Zone != zone || !lease.CurrentAt(now))
        { presentation.Invalidate(); return false; }
        return presentation.CanSubmit(packet, zone, now);
    }
    private bool CanCommit()
    {
        var now = ReadClock();
        return committingLease is { } current && current.CurrentAt(now)
            && (committingFeet is null || committingFeet.FreshAt(now));
    }
    private double ReadClock()
    {
        double now;
        readingClock = true;
        try { now = clock(); }
        catch { return double.NaN; }
        finally { readingClock = false; }
        if (!double.IsFinite(now) || now < 0 || now < checkedAt) return double.NaN;
        checkedAt = now; return now;
    }
    private void GuardClockReentry()
    {
        if (readingClock) throw new InvalidOperationException("Owner clocks must be read-only and nonreentrant.");
    }
}
