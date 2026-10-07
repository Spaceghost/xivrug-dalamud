using XivCloth.Core;
using XivSurface.Core;

namespace XivRug.Prototype;

/// <summary>Owner-thread adapter for actual immutable raw captures evaluated
/// by one immutable calibration. Missing/stale/relabelled observations break
/// continuity; later success starts a different model token, not an old chain.
/// This copies proxy geometry only. Plant/conflict diagnostics do not authorize
/// cloth contact, and no same-render-frame or exact footwear claim is made.</summary>
public sealed class CalibratedFootSnapshotAdapter
{
    private FootPlantCalibration? calibration;
    private FootProxyModelBinding? model;
    private RawFootFrame? lastRaw;
    private FootProxyPose? lastPose;
    private double lastCheckedAt;

    public void Invalidate()
    {calibration=null;model=null;lastRaw=null;lastPose=null;lastCheckedAt=0;}

    /// <param name="current">Actual latest producer RawFrame, not a later
    /// caller-supplied identity used to relabel an earlier capture.</param>
    public bool TryCapture(CalibratedFootPose? evaluated,RawFootFrame? current,double now,out FootProxyPose? pose)
    {
        pose=null;
        if(evaluated==null||current==null||!ReferenceEquals(evaluated.Source,current)
            ||!ReferenceEquals(evaluated.Support.Frame,current)||!current.FreshAt(now)
            ||evaluated.Calibration.Identity!=current.Identity||evaluated.Capsules.Length!=2
            ||lastRaw!=null&&now<lastCheckedAt)
            return Refuse();

        var selected=evaluated.Calibration;
        if(!ReferenceEquals(calibration,selected))
        {
            // An explicit change of calibration breaks the accepted model,
            // even if every numeric field and capsule dimension is identical.
            Invalidate();
            var actor=current.Identity.Actor;
            if(!FootProxyModelBinding.TryCreate(new(actor.Zone,actor.PlayerId,
                unchecked((ulong)(nuint)actor.Address),actor.ModelGeneration),selected,out model))return Refuse();
            calibration=selected;
        }
        else if(lastRaw!=null)
        {
            if(ReferenceEquals(lastRaw,current)){pose=lastPose;lastCheckedAt=now;return pose!=null;}
            // Preserve genuine producer sequence/time. No filling gaps,
            // timestamp cloning or silently relabelled same-sequence objects.
            if(lastRaw.Sequence==long.MaxValue||current.Sequence!=lastRaw.Sequence+1
                ||current.SampledAt<=lastRaw.ReadCompletedAt)return Refuse();
        }
        Span<FootCapsule> capsules=stackalloc FootCapsule[2];
        for(var i=0;i<2;i++)
        {
            var c=evaluated.Capsules[i];capsules[i]=new(c.A,c.B,c.Radius);
        }
        if(!FootProxyPose.TryCreateCapsules(model,selected,current,current.Sequence,current.SampledAt,capsules,out pose))return Refuse();
        lastRaw=current;lastPose=pose;lastCheckedAt=now;return true;
    }
    private bool Refuse(){Invalidate();return false;}
}
