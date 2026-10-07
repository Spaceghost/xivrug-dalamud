using System.Numerics;

namespace XivCloth.Core;

/// <summary>Equality tokens only; Instance is never dereferenced. ModelGeneration
/// must change when the observer replaces its skeleton/model. Not GPU-pose attestation.</summary>
public readonly record struct FootProxyIdentity(uint Zone,ulong Actor,ulong Instance,long ModelGeneration)
{
    /// <summary>Optional reference-only identity of an immutable calibrated
    /// model. Set only by the validated capsule factory; capture identity is
    /// separate so genuine next observations retain the same model.</summary>
    public FootProxyModelBinding? ModelBinding { get; internal init; }
    public bool Valid=>Zone!=0&&Actor!=0&&Instance!=0&&ModelGeneration>0;
}
public readonly record struct FootMarkerEnvelope(Vector2 Center,float SoleY,float Radius);

/// <summary>Estimated avatar collision volume, NOT rendered footwear geometry.
/// The legacy marker factory rounds soleY+radius downward; the calibrated
/// factory preserves supplied XYZ axes exactly. Simulation distance units.</summary>
public readonly record struct FootCapsule(Vector3 A,Vector3 B,float Radius)
{
    public bool Valid=>Geometry.Valid(A)&&Geometry.Valid(B)&&float.IsFinite(Radius)
        &&Radius is >=.02f and <=.34f&&Vector3.DistanceSquared(A,B)<=.75f*.75f;
}

/// <summary>One complete, owned and genuinely timestamped two-foot observation.
/// No missing-pose fallback, extrapolation, hidden-pose or shoe-mesh inference.</summary>
public sealed class FootProxyPose
{
    public const double MaximumAgeSeconds=1d/30;
    private readonly FootCapsule[] capsules;
    public FootProxyIdentity Identity { get; }
    public long Sequence { get; }
    public double SampledAt { get; }
    /// <summary>Exact immutable producer capture, when supplied by the
    /// calibrated adapter. Metadata only, not part of model equality or an
    /// independent native/GPU attestation.</summary>
    public object? CaptureBinding { get; }
    public ReadOnlySpan<FootCapsule> Capsules=>capsules;
    private FootProxyPose(FootProxyIdentity identity,long sequence,double sampledAt,FootCapsule[] capsules,object? captureBinding=null)
    {Identity=identity;Sequence=sequence;SampledAt=sampledAt;this.capsules=capsules;CaptureBinding=captureBinding;}
    public bool FreshAt(double now)=>double.IsFinite(now)&&now>=SampledAt&&now<=SampledAt+MaximumAgeSeconds;
    public static bool TryCreate(FootProxyIdentity identity,long sequence,double sampledAt,
        ReadOnlySpan<FootMarkerEnvelope> markers,out FootProxyPose? pose)
    {
        pose=null;
        if(!identity.Valid||identity.ModelBinding!=null||sequence<=0||!double.IsFinite(sampledAt)||sampledAt<0||markers.Length!=4)return false;
        var capsules=new FootCapsule[2];
        for(var i=0;i<2;i++)
        {
            var heel=markers[i*2];var toe=markers[i*2+1];
            if(!Valid(heel)||!Valid(toe)||Math.Abs(heel.SoleY-toe.SoleY)>.0001f||Math.Abs(heel.Radius-toe.Radius)>.0001f)return false;
            // Tiny accepted equality tolerance is conservatively combined,
            // never averaged upward or clamped to a smaller boot envelope.
            var sole=Math.Min(heel.SoleY,toe.SoleY);var radius=Math.Max(heel.Radius,toe.Radius);
            var centerY=MathF.BitDecrement(sole+radius);
            capsules[i]=new(new(heel.Center.X,centerY,heel.Center.Y),new(toe.Center.X,centerY,toe.Center.Y),radius);
            if(!capsules[i].Valid)return false;
        }
        pose=new(identity,sequence,sampledAt,capsules);return true;
        static bool Valid(FootMarkerEnvelope marker)=>float.IsFinite(marker.Center.X)&&float.IsFinite(marker.Center.Y)
            &&float.IsFinite(marker.SoleY)&&float.IsFinite(marker.Radius)&&marker.Radius is >=.02f and <=.34f;
    }

    /// <summary>Copy exactly two validated 3-D proxy capsules. No sole-Y
    /// flattening, rounding, fitting, clamp or collision authorization. The
    /// caller must supply the same immutable model source used for the token;
    /// value equality cannot substitute another calibration. Managed callers
    /// remain responsible for genuine capture provenance and time.</summary>
    public static bool TryCreateCapsules(FootProxyModelBinding? model,object? modelSource,
        object? captureBinding,long sequence,double sampledAt,ReadOnlySpan<FootCapsule> source,out FootProxyPose? pose)
    {
        pose=null;
        if(model==null||!model.Matches(modelSource)||captureBinding==null||sequence<=0
            ||!double.IsFinite(sampledAt)||sampledAt<0||source.Length!=2)return false;
        foreach(var capsule in source)if(!capsule.Valid)return false;
        pose=new(model.Identity with {ModelBinding=model},sequence,sampledAt,source.ToArray(),captureBinding);
        return true;
    }
}

/// <summary>Matched consecutive snapshots, modeled by linear axis motion and
/// conservatively maximum endpoint radius. Endpoints do NOT prove arbitrary
/// unsampled animation/rotation or same-render-frame GPU pose. The20-unit/s
/// endpoint limit is an explicit engineering bound, not a measured game limit.</summary>
public sealed class FootProxyMotion
{
    public const float MaximumEndpointSpeed=20;
    public FootProxyPose Before { get; }
    public FootProxyPose After { get; }
    public double Duration=>After.SampledAt-Before.SampledAt;
    private FootProxyMotion(FootProxyPose before,FootProxyPose after){Before=before;After=after;}
    public bool FreshAt(double now)=>Before.FreshAt(now)&&After.FreshAt(now);
    public static bool TryCreate(FootProxyPose? before,FootProxyPose? after,double now,out FootProxyMotion? motion)
    {
        motion=null;
        if(before==null||after==null||before.Identity!=after.Identity||before.Sequence==long.MaxValue||after.Sequence!=before.Sequence+1
            ||!before.FreshAt(now)||!after.FreshAt(now))return false;
        var dt=after.SampledAt-before.SampledAt;
        if(dt<=0||after.SampledAt>before.SampledAt+FootProxyPose.MaximumAgeSeconds)return false;
        for(var i=0;i<2;i++)
        {
            var a=before.Capsules[i];var b=after.Capsules[i];var limit=MaximumEndpointSpeed*dt;
            if(Vector3.Distance(a.A,b.A)>limit||Vector3.Distance(a.B,b.B)>limit)return false;
        }
        motion=new(before,after);return true;
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
