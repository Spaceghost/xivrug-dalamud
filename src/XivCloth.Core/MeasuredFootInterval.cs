using System.Numerics;

namespace XivCloth.Core;

/// <summary>Explicit modeled interval for genuinely consecutive observations at
/// up to50ms cadence. Only AFTER must be currently fresh; BEFORE is bounded
/// history, never retimestamped. Linear capsule motion is a model, not proof of
/// hidden animation. This does not alter FootProxyMotion's strict1/30 policy.</summary>
public sealed class MeasuredFootInterval
{
    public const double MaximumDuration=.05, MinimumDuration=.000001;
    public const double MaximumClockQuantum=.000001;
    public const int MaximumSubsteps=6;
    public FootProxyPose Before { get; }
    public FootProxyPose After { get; }
    public double Duration=>After.SampledAt-Before.SampledAt;
    public int Substeps { get; }
    public double StepSeconds=>Duration/Substeps;
    private MeasuredFootInterval(FootProxyPose before,FootProxyPose after,int substeps)
    {Before=before;After=after;Substeps=substeps;}
    public bool FreshAt(double now)=>After.FreshAt(now);
    public static bool TryCreate(FootProxyPose? before,FootProxyPose? after,double now,out MeasuredFootInterval? interval)
    {
        interval=null;
        if(before==null||after==null||before.Identity!=after.Identity||before.Sequence==long.MaxValue
            ||after.Sequence!=before.Sequence+1||!after.FreshAt(now)
            // Bounds are compared in the original timestamp domain. Refuse
            // clocks whose ULP could turn50ms into a materially longer interval;
            // never add an arbitrary freshness/step-count tolerance.
            ||Math.BitIncrement(before.SampledAt)-before.SampledAt>MaximumClockQuantum
            ||Math.BitIncrement(after.SampledAt)-after.SampledAt>MaximumClockQuantum
            ||after.SampledAt<before.SampledAt+MinimumDuration
            ||after.SampledAt>before.SampledAt+MaximumDuration)return false;
        var duration=after.SampledAt-before.SampledAt;
        if(!double.IsFinite(duration)||duration<=0)return false;
        for(var i=0;i<2;i++)
        {
            var a=before.Capsules[i];var b=after.Capsules[i];var limit=FootProxyMotion.MaximumEndpointSpeed*duration;
            if(Vector3.Distance(a.A,b.A)>limit||Vector3.Distance(a.B,b.B)>limit)return false;
        }
        // Ceiling in the original clock domain avoids spurious seventh steps
        // from subtraction roundoff at exactly50ms after a long-running clock.
        var steps=1;
        while(steps<MaximumSubsteps&&after.SampledAt>before.SampledAt+steps*XpbdCloth.FixedStep)steps++;
        interval=new(before,after,steps);return true;
    }
    internal void Sample(float fraction,Span<FootCapsule> destination)
    {
        for(var i=0;i<2;i++)
        {
            var a=Before.Capsules[i];var b=After.Capsules[i];
            destination[i]=new(Vector3.Lerp(a.A,b.A,fraction),Vector3.Lerp(a.B,b.B,fraction),Math.Max(a.Radius,b.Radius));
        }
    }
}
