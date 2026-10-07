namespace XivCloth.Core;

public sealed partial class XpbdCloth
{
    private PairedPlantPolicy? plantPolicy;
    private PairedPlantObservation? plantObservation;
    private byte[]? compressedState;

    public bool TryCreatePlantPolicy(MeasuredTriangleScene scene,FootProxyPose reference,
        ReadOnlySpan<int> leftTread,ReadOnlySpan<int> rightTread,out PairedPlantPolicy? policy)
    {
        policy=null;
        return !checkingCommit&&PairedPlantPolicy.TryCreate(definition,scene,reference,leftTread,rightTread,out policy);
    }

    /// <summary>Explicit whole-current-pose admission or policy replacement.
    /// No reset, pose projection, clock consumption, velocity change or foot
    /// sequence skipping. Failed replacement leaves every accepted field intact.</summary>
    public XpbdAdvance AdmitPairedScene(PairedPlantObservation observation,double now,Func<bool>? canCommit=null)
    {
        if(checkingCommit||observation is null||!ReferenceEquals(observation.Policy.Definition,definition)
            ||!observation.Actual.FreshAt(now)||acceptedFeet is not null&&(!ReferenceEquals(acceptedFeet,observation.Actual)||now<acceptedFootCheckTime))
            return new(XpbdStatus.RejectedInput,0,0,0,0);
        var policy=observation.Policy;var scene=policy.Scene;
        var dispatch=new PlantContactDispatch(policy,observation.Actual,observation.Actual,
            ReferenceEquals(policy,plantPolicy)?compressedState:null);
        terrainQueries.BeginCall();var work=new CollisionWork(settings.CollisionWorkLimit);
        if(!dispatch.UpdateClosure(positions,positions,settings.Thickness,ref work))return Report(work.Used>=work.Limit?SweepVerdict.BudgetExceeded:SweepVerdict.Unproven);
        var verdict=TriangleContacts.OneSidedClear(positions,definition.Faces,scene,settings.Thickness*.5f,ref work,dispatch);
        if(verdict==SweepVerdict.Clear)verdict=TriangleContacts.Sweep(positions,positions,definition.Faces,scene,settings.Thickness*.5f,ref work,out _,terrainQueries,dispatch);
        if(verdict==SweepVerdict.Clear)verdict=FootCapsuleContacts.Sweep(positions,positions,definition.Faces,
            observation.Actual.Capsules,observation.Actual.Capsules,settings.Thickness*.5f,ref work,dispatch);
        if(verdict!=SweepVerdict.Clear)return Report(verdict);
        if(!CheckCommit(canCommit))return new(XpbdStatus.RejectedInput,0,0,0,0,work.Pairs,work.SweepNodes,work.TreeNodes);
        plantPolicy=policy;plantObservation=observation;compressedState=dispatch.Compressed;
        SceneGeneration=scene.Generation;acceptedFeet=observation.Actual;acceptedFootCheckTime=now;
        return Report(SweepVerdict.Clear);
        XpbdAdvance Report(SweepVerdict v)=>new(v==SweepVerdict.Clear?XpbdStatus.Ready:v==SweepVerdict.BudgetExceeded?XpbdStatus.WorkBudgetExceeded:XpbdStatus.CollisionUnproven,
            0,0,0,0,work.Pairs,work.SweepNodes,work.TreeNodes);
    }

    public XpbdAdvance AdvanceMeasuredFeetWithPlants(PairedPlantInterval interval,double now,
        XpbdStepInputs? inputs=null,Func<bool>? canCommit=null)
    {
        if(checkingCommit||interval is null||!ReferenceEquals(interval.Policy,plantPolicy)
            ||!ReferenceEquals(interval.Before,plantObservation)||!ReferenceEquals(interval.Motion.Before,acceptedFeet)
            ||!interval.Motion.FreshAt(now)||now<acceptedFootCheckTime)
            return new(XpbdStatus.FootTrackingUnavailable,0,0,0,0);
        var dispatch=new PlantContactDispatch(interval.Policy,interval.Motion.Before,interval.Motion.After,compressedState);
        var result=AdvanceCore(interval.Motion.Duration,interval.Policy.Scene,inputs,null,interval.Motion,canCommit,dispatch);
        if(result.Status==XpbdStatus.Ready)
        {compressedState=dispatch.Compressed;plantObservation=interval.After;acceptedFootCheckTime=now;}
        return result;
    }

    public XpbdAdvance RemovePlantPolicy(double now,Func<bool>? canCommit=null)
    {
        if(checkingCommit||plantPolicy is null||acceptedFeet is null||!acceptedFeet.FreshAt(now)||now<acceptedFootCheckTime)
            return new(XpbdStatus.RejectedInput,0,0,0,0);
        var scene=plantPolicy.Scene;terrainQueries.BeginCall();var work=new CollisionWork(settings.CollisionWorkLimit);
        var verdict=TriangleContacts.OneSidedClear(positions,definition.Faces,scene,settings.Thickness*.5f,ref work);
        if(verdict==SweepVerdict.Clear)verdict=TriangleContacts.Sweep(positions,positions,definition.Faces,scene,settings.Thickness*.5f,ref work,out _,terrainQueries);
        if(verdict==SweepVerdict.Clear)verdict=FootCapsuleContacts.Sweep(positions,positions,definition.Faces,acceptedFeet.Capsules,acceptedFeet.Capsules,settings.Thickness*.5f,ref work);
        var status=verdict==SweepVerdict.Clear?XpbdStatus.Ready:verdict==SweepVerdict.BudgetExceeded?XpbdStatus.WorkBudgetExceeded:XpbdStatus.CollisionUnproven;
        if(status==XpbdStatus.Ready&&!CheckCommit(canCommit))status=XpbdStatus.RejectedInput;
        if(status==XpbdStatus.Ready){plantPolicy=null;plantObservation=null;compressedState=null;}
        return new(status,0,0,0,0,work.Pairs,work.SweepNodes,work.TreeNodes);
    }

    public XpbdFootCapture CaptureForPlantedFeet(PairedPlantObservation expectedAccepted,
        PairedPlantObservation inspected,double now,int? maximumWork=null)
    {
        if(checkingCommit||plantPolicy is null||plantObservation is null||acceptedFeet is null
            ||!ReferenceEquals(expectedAccepted,plantObservation)||!ReferenceEquals(inspected?.Policy,plantPolicy)
            ||!acceptedFeet.FreshAt(now)||!inspected.Actual.FreshAt(now)||now<acceptedFootCheckTime
            ||!ReferenceEquals(inspected.Actual,acceptedFeet)&&(inspected.Actual.SampledAt<acceptedFootCheckTime
                ||!FootProxyMotion.TryCreate(acceptedFeet,inspected.Actual,now,out _)))
            return new(XpbdStatus.FootTrackingUnavailable,null,0,0);
        var limit=maximumWork??settings.CollisionWorkLimit;
        if(limit<0||limit>settings.CollisionWorkLimit)return new(XpbdStatus.RejectedInput,null,0,0);
        var work=new CollisionWork(limit);var scene=plantPolicy.Scene;
        var dispatch=new PlantContactDispatch(plantPolicy,inspected.Actual,inspected.Actual,compressedState);
        if(!dispatch.UpdateClosure(positions,positions,settings.Thickness,ref work))return Report(work.Used>=work.Limit?SweepVerdict.BudgetExceeded:SweepVerdict.Unproven);
        // A new late plant may alter which proof applies. Revalidate the whole
        // current surface against terrain as well as both actual observations.
        var verdict=TriangleContacts.OneSidedClear(positions,definition.Faces,scene,settings.Thickness*.5f,ref work,dispatch);
        if(verdict==SweepVerdict.Clear)verdict=TriangleContacts.Sweep(positions,positions,definition.Faces,scene,settings.Thickness*.5f,ref work,out _,null,dispatch);
        if(verdict==SweepVerdict.Clear)verdict=FootCapsuleContacts.Sweep(positions,positions,definition.Faces,inspected.Actual.Capsules,inspected.Actual.Capsules,settings.Thickness*.5f,ref work,dispatch);
        if(verdict!=SweepVerdict.Clear)return Report(verdict);
        var binding=new XpbdFootPublicationBinding(acceptedFeet,inspected.Actual,SceneGeneration,now,settings.Thickness*.5f,
            plantPolicy,plantObservation,inspected);
        return new(XpbdStatus.Ready,new(positions,definition,SceneGeneration,binding),work.Pairs,work.SweepNodes){BroadphaseNodes=work.TreeNodes};
        XpbdFootCapture Report(SweepVerdict v)=>new(v==SweepVerdict.BudgetExceeded?XpbdStatus.WorkBudgetExceeded:XpbdStatus.CollisionUnproven,null,work.Pairs,work.SweepNodes){BroadphaseNodes=work.TreeNodes};
    }
}
