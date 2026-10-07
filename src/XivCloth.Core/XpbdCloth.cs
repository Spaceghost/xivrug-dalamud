using System.Numerics;

namespace XivCloth.Core;

public sealed record XpbdSettings
{
    public int Iterations { get; init; }=8;
    public Vector3 Gravity { get; init; }=new(0,-9.81f,0);
    public float Drag { get; init; }=2;
    public float StretchCompliance { get; init; }=1e-7f;
    public float ShearCompliance { get; init; }=1e-6f;
    public float BendCompliance { get; init; }=2e-3f;
    public bool Bending { get; init; }=true;
    /// <summary>Response target. The conservative swept finite-face certificate
    /// uses HALF this distance; it does not certify the full response target.</summary>
    public float Thickness { get; init; }=.004f;
    public float MaximumVelocity { get; init; }=8;
    public int CollisionWorkLimit { get; init; }=100_000;
    internal void Validate()
    {
        if(Iterations is <1 or >16||!Geometry.Finite(Gravity)||Gravity.Length()>30
            ||!float.IsFinite(Drag)||Drag is <0 or >30
            ||!Compliance(StretchCompliance)||!Compliance(ShearCompliance)||!Compliance(BendCompliance)
            ||!float.IsFinite(Thickness)||Thickness is <.001f or >.02f
            ||!float.IsFinite(MaximumVelocity)||MaximumVelocity is <=0 or >20
            ||CollisionWorkLimit is <0 or >CollisionWork.Maximum)
            throw new ArgumentException("Invalid bounded XPBD settings.");
        static bool Compliance(float a)=>float.IsFinite(a)&&a>=0&&a<=1;
    }
}

public enum XpbdStatus { Ready, WorkBudgetExceeded, InvalidContactOrState, CollisionUnproven, RejectedInput, FootTrackingUnavailable }
public readonly record struct XpbdAdvance(XpbdStatus Status,int Substeps,int ConstraintSolves,int ContactChecks,double DroppedSeconds,
    int TrianglePairs=0,int SweepNodes=0,int BroadphaseNodes=0,int InputWork=0);

/// <summary>Immutable indexed publication: owns copies, never aliases simulation
/// scratch or caller inputs. Does not pretend this 3-D state has GroundMinimumY.</summary>
public sealed class XpbdFrame
{
    private readonly Vector3[] positions,normals;
    private readonly Vector2[] uv;
    private readonly int[] indices;
    public ReadOnlySpan<Vector3> Positions=>positions;
    public ReadOnlySpan<Vector3> Normals=>normals;
    public ReadOnlySpan<Vector2> UV=>uv;
    public ReadOnlySpan<int> Indices=>indices;
    /// <summary>Only individual accepted substep segments were swept. The
    /// previous published frame→this final endpoint chord is NOT certified.
    /// A render adapter must not interpolate these endpoints. Future smoothing
    /// requires retained proven substep samples and matching render alpha.</summary>
    public bool EndpointInterpolationAllowed=>false;
    /// <summary>Zero before first accepted scene admission; never world-support provenance.</summary>
    public long SceneGeneration { get; }
    /// <summary>Present only on an independently admitted foot-bound capture.
    /// Ordinary Capture remains an unadmitted diagnostic/material snapshot.</summary>
    public XpbdFootPublicationBinding? FootBinding { get; }
    internal XpbdFrame(Vector3[] source,XpbdDefinition definition,long sceneGeneration,
        XpbdFootPublicationBinding? footBinding=null)
    {
        SceneGeneration=sceneGeneration;
        FootBinding=footBinding;
        positions=(Vector3[])source.Clone();uv=(Vector2[])definition.Texture.Clone();indices=(int[])definition.Faces.Clone();
        normals=new Vector3[positions.Length];
        for(var i=0;i<indices.Length;i+=3)
        {
            var a=indices[i];var b=indices[i+1];var c=indices[i+2];
            var n=Vector3.Cross(positions[b]-positions[a],positions[c]-positions[a]);
            normals[a]+=n;normals[b]+=n;normals[c]+=n;
        }
        for(var i=0;i<normals.Length;i++)normals[i]=normals[i].LengthSquared()>1e-12f?Vector3.Normalize(normals[i]):Vector3.UnitY;
    }
}

/// <summary>Bounded indexed 3-D XPBD cloth simulation. Macklin/Müller/Chentanez2016 Algorithm1,
/// equations17–18: deltaLambda=(-C-alpha/dt²*lambda)/(sum(w*|grad|²)+alpha/dt²).
/// Lambdas accumulate across iterations and reset for each substep (strict
/// fixed-clock or explicitly measured interval).
/// External drag is exponential velocity damping, not the paper's Rayleigh term.
/// Conservative finite-face sweeps certify each accepted linear substep at half
/// Thickness. No exact TOI, self-collision, friction, solid-volume containment,
/// or previous-publication→final-endpoint interpolation certificate.</summary>
public sealed partial class XpbdCloth
{
    public const double FixedStep=1d/120;
    public const int MaximumSubsteps=8,MaximumWorkPerCall=2_000_000,MaximumInputWorkPerCall=16_384;
    public const float MaximumPenetration=.05f;
    internal const int MaximumSweepRepairs=4;
    private readonly XpbdDefinition definition;
    private readonly XpbdSettings settings;
    private Vector3[] positions,velocity,previous,checkpoint,checkpointVelocity,pins;
    private readonly float[] edgeLambda,hingeLambda;
    private readonly float[] sweepTravel;
    private readonly FaceQueryCache terrainQueries;
    private readonly Vector3[] targetLambda=new Vector3[XpbdStepInputs.MaximumTargets];
    private readonly float[] targetTravel=new float[XpbdStepInputs.MaximumTargets];
    private double remainder;
    private enum ClockMode { Unstarted, StrictFixed, MeasuredInterval }
    private ClockMode clockMode;
    private FootProxyPose? acceptedFeet;
    private double acceptedFootCheckTime;
    private bool checkingCommit;
    public long SceneGeneration { get; private set; }
    public XpbdCloth(XpbdDefinition definition,XpbdSettings? settings=null)
    {
        this.definition=definition??throw new ArgumentNullException(nameof(definition));
        this.settings=settings??new();this.settings.Validate();
        positions=(Vector3[])definition.Rest.Clone();pins=(Vector3[])positions.Clone();
        velocity=new Vector3[positions.Length];previous=new Vector3[positions.Length];
        checkpoint=new Vector3[positions.Length];checkpointVelocity=new Vector3[positions.Length];
        edgeLambda=new float[definition.Edges.Length];hingeLambda=new float[definition.Hinges.Length];
        sweepTravel=new float[positions.Length];
        terrainQueries=new(definition.Faces.Length/3);
    }
    // Allocation-free diagnostics for tests; does not expose mutable solver storage.
    internal void CopyPositionsTo(Span<Vector3> destination)=>positions.AsSpan().CopyTo(destination);

    public XpbdFrame Capture()
    {
        if(checkingCommit)throw new InvalidOperationException("Commit predicates must not inspect an uncommitted pose.");
        return new(positions,definition,SceneGeneration);
    }
    /// <summary>Read-only whole-surface admission for an actual inspected foot
    /// pose. The caller must name the EXACT last accepted simulation foot
    /// object; a late read cannot relabel an old cloth state. No pose, velocity,
    /// clock, generation or accepted foot sequence is consumed or mutated.
    /// This certifies stationary modeled capsules at half Thickness, not the
    /// path between captures or a same-GPU-frame pose. Framework-owner thread
    /// only; recheck the returned binding with the real draw-time clock.</summary>
    public XpbdFootCapture CaptureForFeet(FootProxyPose? expectedAccepted,FootProxyPose? inspectedFeet,
        double now,int? maximumWork=null)
    {
        if(checkingCommit||plantPolicy is not null)return new(XpbdStatus.RejectedInput,null,0,0);
        if(acceptedFeet==null||expectedAccepted==null||inspectedFeet==null||SceneGeneration==0
            ||!ReferenceEquals(expectedAccepted,acceptedFeet)||!acceptedFeet.FreshAt(now)
            ||!inspectedFeet.FreshAt(now)||now<acceptedFootCheckTime)
            return new(XpbdStatus.FootTrackingUnavailable,null,0,0);
        if(!ReferenceEquals(acceptedFeet,inspectedFeet))
        {
            // Only the next genuinely later observation is eligible. Same-ID
            // clones and skipped/reused sequences do not become a fresh chain.
            if(inspectedFeet.SampledAt<acceptedFootCheckTime
                ||!FootProxyMotion.TryCreate(acceptedFeet,inspectedFeet,now,out _))
                return new(XpbdStatus.FootTrackingUnavailable,null,0,0);
        }
        var limit=maximumWork??settings.CollisionWorkLimit;
        if(limit<0||limit>settings.CollisionWorkLimit)
            return new(XpbdStatus.RejectedInput,null,0,0);
        var work=new CollisionWork(limit);
        var verdict=FootCapsuleContacts.Sweep(positions,positions,definition.Faces,
            inspectedFeet.Capsules,inspectedFeet.Capsules,settings.Thickness*.5f,ref work);
        if(verdict!=SweepVerdict.Clear)
            return new(verdict==SweepVerdict.BudgetExceeded?XpbdStatus.WorkBudgetExceeded:XpbdStatus.CollisionUnproven,
                null,work.Pairs,work.SweepNodes);
        var binding=new XpbdFootPublicationBinding(acceptedFeet,inspectedFeet,SceneGeneration,now,settings.Thickness*.5f);
        return new(XpbdStatus.Ready,new(positions,definition,SceneGeneration,binding),work.Pairs,work.SweepNodes);
    }
    /// <summary>Explicitly admit the unchanged current material into a new
    /// static scene without resetting its velocity or fractional clock. This
    /// does not sweep moving obstacles between old/new scenes, prove source
    /// freshness/completeness, or authorize a changed world/floor layer. The
    /// caller owns those transitions. Unproved/exhausted admission leaves all
    /// state unchanged; Advance still rejects an unbound scene generation.</summary>
    public XpbdAdvance RebindScene(MeasuredTriangleScene scene)=>RebindScene(scene,null);
    public XpbdAdvance RebindScene(MeasuredTriangleScene scene,Func<bool>? canCommit)
    {
        if(checkingCommit||plantPolicy is not null)return new(XpbdStatus.RejectedInput,0,0,0,0);
        ArgumentNullException.ThrowIfNull(scene);
        terrainQueries.BeginCall();
        var work=new CollisionWork(settings.CollisionWorkLimit);
        var verdict=TriangleContacts.OneSidedClear(positions,definition.Faces,scene,settings.Thickness*.5f,ref work);
        if(verdict==SweepVerdict.Clear)
            verdict=TriangleContacts.Sweep(positions,positions,definition.Faces,scene,settings.Thickness*.5f,ref work,out _,terrainQueries);
        var status=verdict==SweepVerdict.Clear?XpbdStatus.Ready
            :verdict==SweepVerdict.BudgetExceeded?XpbdStatus.WorkBudgetExceeded:XpbdStatus.CollisionUnproven;
        if(status==XpbdStatus.Ready&&!CheckCommit(canCommit))status=XpbdStatus.RejectedInput;
        if(status==XpbdStatus.Ready)SceneGeneration=scene.Generation;
        return new(status,0,0,0,0,work.Pairs,work.SweepNodes,work.TreeNodes);
    }
    public void Reset(ReadOnlySpan<Vector3> pose=default)
    {
        if(checkingCommit)throw new InvalidOperationException("Commit predicates must be nonreentrant.");
        if(!pose.IsEmpty)
        {
            if(pose.Length!=positions.Length)throw new ArgumentException("Invalid reset pose.");
            foreach(var p in pose)if(!Geometry.Valid(p))throw new ArgumentException("Invalid reset pose.");
            for(var i=0;i<definition.Faces.Length;i+=3)
            {
                var a=pose[definition.Faces[i]];var b=pose[definition.Faces[i+1]];var c=pose[definition.Faces[i+2]];
                if(Vector3.Cross(b-a,c-a).LengthSquared()<1e-12f)throw new ArgumentException("Degenerate reset pose.");
            }
        }
        (pose.IsEmpty?definition.Rest.AsSpan():pose).CopyTo(positions);
        positions.CopyTo(pins,0);Array.Clear(velocity);Array.Clear(edgeLambda);Array.Clear(hingeLambda);
        Array.Clear(targetLambda);Array.Clear(targetTravel);
        remainder=0;clockMode=ClockMode.Unstarted;SceneGeneration=0;acceptedFeet=null;acceptedFootCheckTime=0;
        plantPolicy=null;plantObservation=null;compressedState=null;
    }
    /// <summary>Optional immutable inputs affect only fixed substeps produced
    /// here; fractional calls never retain their forces/targets for later calls.
    /// A different definition identity is a caller error. A non-pinned target
    /// farther than the admitted pull radius refuses this whole call.</summary>
    public XpbdAdvance Advance(double elapsedSeconds,MeasuredTriangleScene scene,XpbdStepInputs? inputs=null)
        =>AdvanceCore(elapsedSeconds,scene,inputs,null);

    /// <summary>Consume a complete modeled observation interval. Foot motion is
    /// evenly sampled across the fixed substeps actually produced, not a claim
    /// of reconstructed hidden animation. A zero-substep call still sweeps the
    /// body against stationary cloth before consuming its clock/motion. After
    /// the first accepted foot window, omission/discontinuity refuses until an
    /// explicit Reset; missing frames never silently remove avatar contacts.</summary>
    public XpbdAdvance AdvanceWithFeet(double elapsedSeconds,MeasuredTriangleScene scene,FootProxyMotion? feet,
        double now,XpbdStepInputs? inputs=null)
    {
        if(feet==null||!feet.FreshAt(now)||!double.IsFinite(elapsedSeconds)||Math.Abs(elapsedSeconds-feet.Duration)>1e-8
            ||(acceptedFeet!=null&&(!ReferenceEquals(acceptedFeet,feet.Before)||now<acceptedFootCheckTime)))
            return new(XpbdStatus.FootTrackingUnavailable,0,0,0,0);
        var result=AdvanceCore(elapsedSeconds,scene,inputs,feet);
        if(result.Status==XpbdStatus.Ready)acceptedFootCheckTime=now;
        return result;
    }
    /// <summary>Separate clock domain: consume the exact measured interval in
    /// at most6 equal actual-dt substeps, with no fractional remainder or dropped
    /// time. Fresh-after and exact accepted-before identity are required. Never
    /// mix with the strict fixed-clock API without Reset: that would otherwise
    /// discard or reassign its unconsumed time. Failure commits no new clock,
    /// pose, velocity, scene, foot sequence, or accepted check time.
    /// Optional canCommit is a synchronous read-only, nonreentrant owner-lease
    /// check. False or exception rolls back the call. It must not call native
    /// code or inspect/export the solver's not-yet-committed state.</summary>
    public XpbdAdvance AdvanceMeasuredFeet(MeasuredTriangleScene scene,MeasuredFootInterval? interval,
        double now,XpbdStepInputs? inputs=null)=>AdvanceMeasuredFeet(scene,interval,now,inputs,null);
    public XpbdAdvance AdvanceMeasuredFeet(MeasuredTriangleScene scene,MeasuredFootInterval? interval,
        double now,XpbdStepInputs? inputs,Func<bool>? canCommit)
    {
        if(interval==null||!interval.FreshAt(now)
            ||(acceptedFeet!=null&&(!ReferenceEquals(acceptedFeet,interval.Before)||now<acceptedFootCheckTime)))
            return new(XpbdStatus.FootTrackingUnavailable,0,0,0,0);
        var result=AdvanceCore(interval.Duration,scene,inputs,null,interval,canCommit);
        if(result.Status==XpbdStatus.Ready)acceptedFootCheckTime=now;
        return result;
    }
    private XpbdAdvance AdvanceCore(double elapsedSeconds,MeasuredTriangleScene scene,XpbdStepInputs? inputs,
        FootProxyMotion? feet,MeasuredFootInterval? interval=null,Func<bool>? canCommit=null,PlantContactDispatch? plant=null)
    {
        if(checkingCommit)return new(XpbdStatus.RejectedInput,0,0,0,0);
        if(plantPolicy is not null&&plant is null)return new(XpbdStatus.RejectedInput,0,0,0,0);
        ArgumentNullException.ThrowIfNull(scene);
        if(!double.IsFinite(elapsedSeconds)||elapsedSeconds<0||elapsedSeconds>1)throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        var hasFeet=feet!=null||interval!=null;
        if(acceptedFeet!=null&&!hasFeet)return new(XpbdStatus.FootTrackingUnavailable,0,0,0,0);
        if((interval==null&&clockMode==ClockMode.MeasuredInterval)||(interval!=null&&clockMode==ClockMode.StrictFixed))
            return new(XpbdStatus.RejectedInput,0,0,0,0);
        if(inputs!=null&&!ReferenceEquals(inputs.Definition,definition))throw new ArgumentException("Input definition identity does not match.",nameof(inputs));
        var accumulated=remainder+elapsedSeconds;
        var available=interval?.Substeps??(int)Math.Floor((accumulated+1e-10)/FixedStep);
        var steps=Math.Min(available,MaximumSubsteps);
        var dt=interval==null?(float)FixedStep:(float)interval.StepSeconds;
        // Count worst-case work before any mutation; never run partial collision
        // constraints just because a deadline expired halfway through a sheet.
        var accelerationCount=inputs==null?0:inputs.Accelerations.Length;
        var targetCount=inputs==null?0:inputs.Targets.Length;
        var inputWork=(long)steps*(accelerationCount+settings.Iterations*3L*targetCount);
        var footWork=!hasFeet?0:(long)(steps*(settings.Iterations+1)+2)*(definition.Faces.Length/3)*2;
        var work=inputWork+footWork+(long)steps*(settings.Iterations*(definition.Edges.Length+definition.Hinges.Length*4L)
            // Deferred deep contacts can require a second scan; each bounded
            // swept repair repeats point clearance. Neither budget is raised.
            +(settings.Iterations*2+1+MaximumSweepRepairs)*positions.Length*(long)scene.Triangles.Length);
        if(work>MaximumWorkPerCall||inputWork>MaximumInputWorkPerCall)return new(XpbdStatus.WorkBudgetExceeded,0,0,0,0);
        if(SceneGeneration!=0&&SceneGeneration!=scene.Generation)
            return new(XpbdStatus.InvalidContactOrState,0,0,0,0); // caller must explicitly RebindScene or Reset
        if(inputs!=null&&!inputs.AdmittedAt(positions,definition.Weights))return new(XpbdStatus.RejectedInput,0,0,0,0);
        positions.CopyTo(checkpoint,0);velocity.CopyTo(checkpointVelocity,0);
        terrainQueries.BeginCall();
        var solves=0;var checks=0;var appliedInputWork=0;var collisionWork=new CollisionWork(settings.CollisionWorkLimit);
        plant?.BeginStep(0,0);
        if(plant is not null&&!plant.UpdateClosure(positions,positions,settings.Thickness,ref collisionWork))
            return Report(collisionWork.Used>=collisionWork.Limit?XpbdStatus.WorkBudgetExceeded:XpbdStatus.CollisionUnproven,0,0);
        var side=TriangleContacts.OneSidedClear(positions,definition.Faces,scene,settings.Thickness*.5f,ref collisionWork,plant);
        if(side!=SweepVerdict.Clear)return Report(side==SweepVerdict.BudgetExceeded?XpbdStatus.WorkBudgetExceeded:XpbdStatus.CollisionUnproven,0,0);
        var initial=TriangleContacts.Sweep(positions,positions,definition.Faces,scene,settings.Thickness*.5f,ref collisionWork,out _,terrainQueries,plant);
        if(initial!=SweepVerdict.Clear)return Report(initial==SweepVerdict.BudgetExceeded?XpbdStatus.WorkBudgetExceeded:XpbdStatus.CollisionUnproven,0,0);
        Span<FootCapsule> footStart=stackalloc FootCapsule[2];Span<FootCapsule> footEnd=stackalloc FootCapsule[2];
        if(hasFeet)
        {
            SampleFeet(0,footStart);
            var admission=FootCapsuleContacts.Sweep(positions,positions,definition.Faces,footStart,footStart,settings.Thickness*.5f,ref collisionWork,plant);
            if(admission!=SweepVerdict.Clear)return Report(admission==SweepVerdict.BudgetExceeded?XpbdStatus.WorkBudgetExceeded:XpbdStatus.CollisionUnproven,0,0);
            if(steps==0)
            {
                SampleFeet(1,footEnd);
                var stationary=FootCapsuleContacts.Sweep(positions,positions,definition.Faces,footStart,footEnd,settings.Thickness*.5f,ref collisionWork);
                if(stationary!=SweepVerdict.Clear)return Report(stationary==SweepVerdict.BudgetExceeded?XpbdStatus.WorkBudgetExceeded:XpbdStatus.CollisionUnproven,0,0);
            }
        }
        for(var step=0;step<steps;step++)
        {
            if(hasFeet){SampleFeet((float)step/steps,footStart);SampleFeet((float)(step+1)/steps,footEnd);}
            plant?.BeginStep((double)step/steps,(double)(step+1)/steps);
            var status=Substep(scene,inputs,!hasFeet?ReadOnlySpan<FootCapsule>.Empty:footStart,
                !hasFeet?ReadOnlySpan<FootCapsule>.Empty:footEnd,dt,ref solves,ref checks,ref appliedInputWork,ref collisionWork,plant);
            if(status!=XpbdStatus.Ready)
            {
                checkpoint.CopyTo(positions,0);checkpointVelocity.CopyTo(velocity,0);
                return Report(status,0,0);
            }
        }
        plant?.ReleaseRecovered(checkpoint,positions,settings.Thickness);
        if(!CheckCommit(canCommit))
        {
            checkpoint.CopyTo(positions,0);checkpointVelocity.CopyTo(velocity,0);
            return Report(XpbdStatus.RejectedInput,0,0);
        }
        remainder=interval==null?Math.Max(0,accumulated-available*FixedStep):0;
        clockMode=interval==null?ClockMode.StrictFixed:ClockMode.MeasuredInterval;
        SceneGeneration=scene.Generation;
        if(hasFeet)acceptedFeet=interval?.After??feet!.After;
        return Report(XpbdStatus.Ready,steps,interval==null?(available-steps)*FixedStep:0);
        void SampleFeet(float fraction,Span<FootCapsule> destination)
        {if(interval!=null)interval.Sample(fraction,destination);else feet!.Sample(fraction,destination);}
        XpbdAdvance Report(XpbdStatus status,int completed,double dropped)=>new(status,completed,solves,checks,dropped,
            collisionWork.Pairs,collisionWork.SweepNodes,collisionWork.TreeNodes,appliedInputWork);
    }
    // Optional publication-owner lease/time predicate: read-only, no native
    // calls, no reentry. False/throw cannot commit any simulation history.
    private bool CheckCommit(Func<bool>? predicate)
    {
        if(predicate is null)return true;
        checkingCommit=true;
        try{return predicate();}
        catch{return false;}
        finally{checkingCommit=false;}
    }
    private XpbdStatus Substep(MeasuredTriangleScene scene,XpbdStepInputs? inputs,ReadOnlySpan<FootCapsule> feetBefore,
        ReadOnlySpan<FootCapsule> feetAfter,float dt,ref int solves,ref int checks,ref int inputWork,ref CollisionWork collisionWork,
        PlantContactDispatch? plant=null)
    {
        var drag=MathF.Exp(-settings.Drag*dt);
        positions.CopyTo(previous,0);Array.Clear(edgeLambda);Array.Clear(hingeLambda);
        Array.Clear(targetLambda);Array.Clear(targetTravel);
        var accelerations=inputs==null?ReadOnlySpan<Vector3>.Empty:inputs.Accelerations;
        var targets=inputs==null?ReadOnlySpan<XpbdTarget>.Empty:inputs.Targets;
        for(var i=0;i<positions.Length;i++)
        {
            if(!accelerations.IsEmpty)inputWork++;
            if(definition.Weights[i]==0){positions[i]=pins[i];velocity[i]=default;continue;}
            var acceleration=accelerations.IsEmpty?settings.Gravity:settings.Gravity+accelerations[i];
            velocity[i]=Limit((velocity[i]+acceleration*dt)*drag,settings.MaximumVelocity);
            positions[i]+=velocity[i]*dt;
        }
        for(var iteration=0;iteration<settings.Iterations;iteration++)
        {
            for(var i=0;i<definition.Edges.Length;i++){SolveDistance(i,dt);solves++;}
            if(settings.Bending)for(var i=0;i<definition.Hinges.Length;i++){SolveBend(i,dt);solves++;}
            for(var i=0;i<targets.Length;i++)
            {
                inputWork+=3;
                if(!SolveTarget(i,targets[i],dt))return XpbdStatus.InvalidContactOrState;
            }
            if(plant is not null&&(!plant.UpdateClosure(previous,positions,settings.Thickness,ref collisionWork)
                ||!plant.ProjectVertices(positions,definition.Weights,settings.Thickness,ref collisionWork)))
                return collisionWork.Used>=collisionWork.Limit?XpbdStatus.WorkBudgetExceeded:XpbdStatus.CollisionUnproven;
            for(var i=0;i<positions.Length;i++)
            {
                if(definition.Weights[i]==0)
                {
                    var pinned=positions[i];
                    if(!scene.Project(ref pinned,settings.Thickness,MaximumPenetration,ref checks,plant,i)
                        ||Vector3.DistanceSquared(pinned,positions[i])>1e-12f)return XpbdStatus.InvalidContactOrState;
                    continue;
                }
                if(!Geometry.Valid(positions[i])||!scene.Project(ref positions[i],settings.Thickness,MaximumPenetration,ref checks,plant,i))return XpbdStatus.InvalidContactOrState;
            }
            if(!TriangleContacts.Project(positions,previous,definition.Faces,definition.Weights,scene,settings.Thickness,ref collisionWork,terrainQueries,plant))
                return collisionWork.Used>=collisionWork.Limit?XpbdStatus.WorkBudgetExceeded:XpbdStatus.CollisionUnproven;
            if(!FootCapsuleContacts.Project(positions,previous,definition.Faces,definition.Weights,feetAfter,settings.Thickness,ref collisionWork,plant))
                return collisionWork.Used>=collisionWork.Limit?XpbdStatus.WorkBudgetExceeded:XpbdStatus.CollisionUnproven;
        }
        Array.Clear(sweepTravel);
        for(var repair=0;repair<=MaximumSweepRepairs;repair++)
        {
            if(plant is not null&&(!plant.UpdateClosure(previous,positions,settings.Thickness,ref collisionWork)
                ||!plant.ProjectVertices(positions,definition.Weights,settings.Thickness,ref collisionWork)))
                return collisionWork.Used>=collisionWork.Limit?XpbdStatus.WorkBudgetExceeded:XpbdStatus.CollisionUnproven;
            for(var i=0;i<positions.Length;i++)
            {
                if(!Geometry.Valid(positions[i])||!scene.IsClear(positions[i],settings.Thickness,ref checks,plant,i))return XpbdStatus.InvalidContactOrState;
            }
            for(var i=0;i<definition.Faces.Length;i+=3)
            {
                var a=positions[definition.Faces[i]];var b=positions[definition.Faces[i+1]];var c=positions[definition.Faces[i+2]];
                if(Vector3.Cross(b-a,c-a).LengthSquared()<1e-12f)return XpbdStatus.InvalidContactOrState;
            }
            var side=TriangleContacts.OneSidedClear(positions,definition.Faces,scene,settings.Thickness*.5f,ref collisionWork,plant);
            if(side!=SweepVerdict.Clear)return side==SweepVerdict.BudgetExceeded?XpbdStatus.WorkBudgetExceeded:XpbdStatus.CollisionUnproven;
            var sweep=TriangleContacts.Sweep(previous,positions,definition.Faces,scene,settings.Thickness*.5f,ref collisionWork,out var hint,terrainQueries,plant);
            if(sweep==SweepVerdict.Clear)
            {
                // Static response may have moved the cloth after its iterated
                // foot response. Certify the final corrected trajectory against
                // the same complete foot interval before publishing velocity.
                var footSweep=FootCapsuleContacts.Sweep(previous,positions,definition.Faces,feetBefore,feetAfter,settings.Thickness*.5f,ref collisionWork,plant);
                if(footSweep!=SweepVerdict.Clear)return footSweep==SweepVerdict.BudgetExceeded?XpbdStatus.WorkBudgetExceeded:XpbdStatus.CollisionUnproven;
                for(var i=0;i<positions.Length;i++)
                    velocity[i]=definition.Weights[i]==0?default:Limit((positions[i]-previous[i])/dt,settings.MaximumVelocity);
                return XpbdStatus.Ready;
            }
            if(sweep==SweepVerdict.BudgetExceeded)return XpbdStatus.WorkBudgetExceeded;
            if(sweep!=SweepVerdict.Unproven||repair==MaximumSweepRepairs
                ||!TriangleContacts.RespondToSweep(previous,positions,definition.Weights,sweepTravel,hint,settings.Thickness,ref collisionWork))
                return collisionWork.Used>=collisionWork.Limit?XpbdStatus.WorkBudgetExceeded:XpbdStatus.CollisionUnproven;
        }
        return XpbdStatus.CollisionUnproven;
    }
    private bool SolveTarget(int index,XpbdTarget target,float dt)
    {
        var particle=target.Particle;var weight=definition.Weights[particle];
        if(weight==0)return true; // A target never overrides a pin.
        var correctionLimit=XpbdStepInputs.MaximumTargetCorrectionPerSubstep*(dt/(float)FixedStep);
        var remaining=correctionLimit-targetTravel[index];
        if(remaining<=0)return true;
        var alpha=target.Compliance/(dt*dt);
        // Orthogonal XYZ constraints have independent diagonal effective mass.
        // Vector lambda is exactly their three accumulated scalar lambdas.
        var delta=(-(positions[particle]-target.Position)-alpha*targetLambda[index])/(weight+alpha);
        var correction=weight*delta;
        if(!Geometry.Finite(delta)||!Geometry.Finite(correction))return false;
        var length=correction.Length();if(!float.IsFinite(length))return false;
        if(length>remaining){var ratio=remaining/length;delta*=ratio;correction*=ratio;length=remaining;}
        targetLambda[index]+=delta;targetTravel[index]+=length;positions[particle]+=correction;
        return Geometry.Valid(positions[particle])&&Geometry.Finite(targetLambda[index]);
    }
    private void SolveDistance(int index,float dt)
    {
        var edge=definition.Edges[index];var difference=positions[edge.A]-positions[edge.B];var length=difference.Length();
        if(length<1e-8f)return;
        var a=definition.Weights[edge.A];var b=definition.Weights[edge.B];if(a+b<=0)return;
        var alpha=(edge.Kind==MaterialEdge.Stretch?settings.StretchCompliance:settings.ShearCompliance)/(dt*dt);
        var delta=(-(length-edge.Length)-alpha*edgeLambda[index])/(a+b+alpha);
        edgeLambda[index]+=delta;var direction=difference/length;
        positions[edge.A]+=a*delta*direction;positions[edge.B]-=b*delta*direction;
    }
    private void SolveBend(int index,float dt)
    {
        var h=definition.Hinges[index];
        if(!XpbdDihedral.Evaluate(positions[h.OppositeA],positions[h.OppositeB],positions[h.EdgeA],positions[h.EdgeB],
            out var angle,out var a,out var b,out var c,out var d))return;
        var wa=definition.Weights[h.OppositeA];var wb=definition.Weights[h.OppositeB];
        var wc=definition.Weights[h.EdgeA];var wd=definition.Weights[h.EdgeB];
        var sum=wa*a.LengthSquared()+wb*b.LengthSquared()+wc*c.LengthSquared()+wd*d.LengthSquared();
        if(sum<1e-12f)return;
        var alpha=settings.BendCompliance/(dt*dt);
        var delta=(-XpbdDihedral.Wrap(angle-h.RestAngle)-alpha*hingeLambda[index])/(sum+alpha);
        hingeLambda[index]+=delta;
        positions[h.OppositeA]+=wa*delta*a;positions[h.OppositeB]+=wb*delta*b;
        positions[h.EdgeA]+=wc*delta*c;positions[h.EdgeB]+=wd*delta*d;
    }
    private static Vector3 Limit(Vector3 v,float maximum)
    {var length=v.Length();return length>maximum?v*(maximum/length):v;}
}
