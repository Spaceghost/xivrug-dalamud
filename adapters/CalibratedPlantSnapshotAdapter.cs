using XivCloth.Core;
using XivSurface.Core;

namespace XivRug.Prototype;

/// <summary>Explicit binding of the new plane-parametric model to the same
/// unchanged raw/calibrated capsule observations. No joint rotation is zeroed,
/// no read is retimestamped and no negative endpoint displacement is clamped.</summary>
public sealed class CalibratedPlantSnapshotAdapter
{
    private readonly CalibratedFootSnapshotAdapter ordinary;
    private readonly FootPlantCalibration calibration;
    private readonly FootProxyPose reference;
    private CalibratedPlantSnapshotAdapter(CalibratedFootSnapshotAdapter ordinary,FootPlantCalibration calibration,FootProxyPose reference)
    {this.ordinary=ordinary;this.calibration=calibration;this.reference=reference;}
    public static bool TryBind(CalibratedFootSnapshotAdapter ordinary,CalibratedFootPose evaluated,
        PairedPlantPolicy policy,double now,out CalibratedPlantSnapshotAdapter? adapter)
    {
        adapter=null;
        if(ordinary is null||evaluated is null||policy is null
            ||!ReferenceEquals(evaluated.Source,evaluated.Calibration.ReferenceFrame)
            ||!ReferenceEquals(policy.Reference.CaptureBinding,evaluated.Source)
            ||!policy.MatchesReferenceModel(evaluated.Calibration)
            ||evaluated.Support.SceneGeneration!=policy.Scene.Generation
            ||!ordinary.TryCapture(evaluated,evaluated.Source,now,out var actual)
            ||!ReferenceEquals(actual,policy.Reference))return false;
        var candidate=new CalibratedPlantSnapshotAdapter(ordinary,evaluated.Calibration,policy.Reference);
        if(!candidate.TryCapture(policy,evaluated,evaluated.Source,now,out _))return false;
        adapter=candidate;return true;
    }
    public bool TryCapture(PairedPlantPolicy policy,CalibratedFootPose evaluated,RawFootFrame current,double now,
        out PairedPlantObservation? observation)
    {
        observation=null;
        if(policy is null||evaluated is null||current is null||!ReferenceEquals(policy.Reference,reference)
            ||!ReferenceEquals(evaluated.Calibration,calibration)||!ReferenceEquals(evaluated.Source,current)
            ||!ReferenceEquals(evaluated.Support.Frame,current)||evaluated.Support.SceneGeneration!=policy.Scene.Generation)return false;
        for(var i=0;i<4;i++)
        {
            var t=evaluated.Support.Triangles[i];
            if(!policy.NamesSupport(i/2,new(t.A,t.B,t.C)))return false;
        }
        if(!ordinary.TryCapture(evaluated,current,now,out var actual))return false;
        return policy.TryObserve(actual!,now,out observation);
    }
}
