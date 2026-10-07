using System.Numerics;
using XivSurface.Core;

namespace XivSurface.Core.Tests;

public sealed class RollingClothGenerationFairnessTests(ITestOutputHelper output)
{
    private static readonly SupportQueryIdentity Active = new(128, 9, 0, new(0, .18f, 0), .35f, 1.35f);
    private static readonly SupportQueryIdentity Requested = Active with { CompressionOrigin = new(.1f, .18f, 0) };

    // A deterministic adapter-cost fixture, NOT a claim to run native collision
    // or the full sampler. The large planar triangle gives exact finite contact
    // positions. Each scheduled operation costs2.05ms of the2ms frame; no sleep.
    // Three-op pending work models bounded resumable floor/riser/clearance work.
    // Actual LayerFloorDiscovery phase correctness has separate real-mesh tests.
    private sealed class Scene : IClothSupportQueries, IIncrementalClothSupportQueries
    {
        private static readonly LayerTriangle Plane = new(new(-20,0,-20), new(-20,0,40), new(40,0,-20), Vector3.UnitY);
        private sealed class Progress { public int Count; public double Last = double.NegativeInfinity; }
        private readonly Dictionary<(bool Cell, Vector2 Key), Progress> progress = [];
        public readonly List<(double At, bool Old, bool Cell, Vector2 Key, LayerQueryResult Result)> Calls = [];
        public bool Warm = true, PermanentPending, ZeroPendingNative;
        public int Raycasts { get; private set; }
        public LayerQueryResult LastResult { get; private set; }
        public int CandidateResets;
        private int allowance, clockMicroseconds, deadlineMicroseconds;
        private double now;
        public int CostMicroseconds = 2050;
        public void Begin(double time)
        { now=time; Raycasts=0; allowance=10000; clockMicroseconds=0; deadlineMicroseconds=Warm?100000000:2000; }
        public bool Deadline() => clockMicroseconds < deadlineMicroseconds;
        public void PrepareQuery(int count) => allowance=Raycasts+count;
        public bool TryVertex(SupportQueryIdentity identity, Vector2 at, out Vector3 contact)
        {
            contact=new(at.X,0,at.Y); Assert.True(Plane.Contains(contact));
            return Query(identity,false,at);
        }
        public bool TryCell(SupportQueryIdentity identity, Vector3 a,Vector3 b,Vector3 c,Vector3 d,out float ceiling)
        {
            ceiling=0;
            // A single convex finite planar face covers the entire cell hull.
            Assert.All(new[]{a,b,c,d}, p=>Assert.True(Plane.Contains(p)));
            var center=(a+b+c+d)/4;
            return Query(identity,true,new(center.X,center.Z));
        }
        private bool Query(SupportQueryIdentity identity,bool cell,Vector2 key)
        {
            Assert.True(Deadline()); Assert.True(Raycasts<allowance);
            var old=identity==Active;
            if(old || !ZeroPendingNative)Raycasts++;
            clockMicroseconds+=CostMicroseconds;
            if(Warm || old) LastResult=LayerQueryResult.Success;
            else
            {
                if(!progress.TryGetValue((cell,key),out var p))progress[(cell,key)]=p=new();
                if(p.Count>0 && now-p.Last>.1){p.Count=0;CandidateResets++;}
                p.Count++;p.Last=now;
                LastResult=!PermanentPending && p.Count>=3 ? LayerQueryResult.Success : LayerQueryResult.Pending;
                if(LastResult==LayerQueryResult.Success)progress.Remove((cell,key));
            }
            Calls.Add((now,old,cell,key,LastResult));
            return LastResult==LayerQueryResult.Success;
        }
    }

    private static (RollingClothSupport Cache,Scene Scene) Warm(bool permanent=false,bool halo=false)
    {
        // A complete configured2y rug, not the partial/supported snapshot API.
        // Coarse2y spacing deliberately makes ongoing refresh affordable at
        // one op/frame, so capacity exhaustion cannot explain these red tests.
        var cache=new RollingClothSupport(spacing:2,half:2,halo:halo?2:0,ttl:2);
        var scene=new Scene { PermanentPending=permanent };scene.Begin(0);
        cache.Update(Active,Vector2.Zero,0,10000,scene,scene.Deadline);
        Assert.True(cache.TrySnapshot(Vector2.Zero,0,out var snapshot,Active));
        Assert.Equal(new Vector2(2),snapshot!.Half);
        scene.Warm=false;scene.Calls.Clear();return(cache,scene);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void EligibleCurrentOriginGetsServiceWhilePendingConsumesOneFrame(bool permanent)
    {
        var(cache,scene)=Warm(permanent);
        for(var frame=36;frame<=61;frame++)
        {
            var now=frame/30d;scene.Begin(now);
            var status=cache.Update(Requested,Vector2.Zero,now,100,scene,scene.Deadline,
                originReserve:45,preferRequestedOrigin:false,allowPendingPromotion:false);
            Assert.InRange(status.Rays,0,1);
            Assert.Equal(Active,cache.ActiveIdentity);Assert.Equal(Requested,cache.PendingIdentity);
        }
        Describe(scene,cache,61/30d);
        Assert.True(scene.Calls.Any(c=>c.Old),
            "A currently eligible full-size origin received no renewal opportunity before its ordinary TTL expired.");
    }

    [Fact]
    public void ActiveOnlyHasEnoughCapacityToRemainContinuouslyFullSize()
    {
        var(cache,scene)=Warm();
        for(var frame=1;frame<=150;frame++)
        {
            var now=frame/30d;scene.Begin(now);
            cache.Update(Active,Vector2.Zero,now,100,scene,scene.Deadline);
            Assert.True(cache.TrySnapshot(Vector2.Zero,now,out var snapshot,Active));
            Assert.Equal(new Vector2(2),snapshot!.Half);
        }
        Describe(scene,cache,5);
        Assert.True(scene.Calls.Count(c=>c.Old)>26);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ContinuousFrameworkBeforeReanchorDoesNotRemoveTheStarvation(bool permanent)
    {
        var(cache,scene)=Warm(permanent);
        for(var frame=1;frame<36;frame++)
        {
            var now=frame/30d;scene.Begin(now);
            cache.Update(Active,Vector2.Zero,now,100,scene,scene.Deadline);
            Assert.True(cache.TrySnapshot(Vector2.Zero,now,out _,Active));
        }
        var earlierRenewals=scene.Calls.Count;
        Assert.True(earlierRenewals>0);scene.Calls.Clear();
        for(var frame=36;frame<=61;frame++)
        {
            var now=frame/30d;scene.Begin(now);
            cache.Update(Requested,Vector2.Zero,now,100,scene,scene.Deadline,
                preferRequestedOrigin:false,allowPendingPromotion:false);
        }
        output.WriteLine($"successfulPreHandoffRenewals={earlierRenewals}");Describe(scene,cache,61/30d);
        Assert.Contains(scene.Calls,c=>c.Old);
        Assert.True(cache.TrySnapshot(Vector2.Zero,61/30d,out _,Active));
    }

    [Fact]
    public void FiniteReplacementEventuallyCompletesWithoutInventingPromotion()
    {
        var(cache,scene)=Warm();double? lost=null,promoted=null;
        for(var frame=36;frame<=120;frame++)
        {
            var now=frame/30d;scene.Begin(now);
            cache.Update(Requested,Vector2.Zero,now,100,scene,scene.Deadline,preferRequestedOrigin:false);
            if(!cache.TrySnapshot(Vector2.Zero,now,out _))lost??=now;
            if(cache.ActiveIdentity==Requested){promoted=now;break;}
        }
        output.WriteLine($"firstInvisible={lost:R}; fullyPromoted={promoted:R}; oldCalls={scene.Calls.Count(c=>c.Old)}; resets={scene.CandidateResets}");
        Assert.NotNull(promoted);
        Assert.Contains(scene.Calls,c=>c.Old);
        Assert.True(cache.TrySnapshot(Vector2.Zero,promoted!.Value,out var replacement,Requested));
        Assert.Equal(9,replacement!.Contacts.Length);Assert.Equal(4,replacement.CellCeilings.Length);
    }

    [Fact]
    public void SlightMotionWithinWarmedHaloStillGetsActiveGenerationService()
    {
        var(cache,scene)=Warm(permanent:true,halo:true);var desired=new Vector2(.1f,0);
        Assert.True(cache.TrySnapshot(desired,0,out var initial,Active));
        Assert.True(initial!.Half.X-Math.Abs(initial.Center.X-desired.X)>=2);
        for(var frame=36;frame<=61;frame++)
        {
            var now=frame/30d;scene.Begin(now);
            cache.Update(Requested,desired,now,100,scene,scene.Deadline,
                preferRequestedOrigin:false,allowPendingPromotion:false);
        }
        Describe(scene,cache,61/30d);
        Assert.Contains(scene.Calls,c=>c.Old);
        // Larger moved windows may exceed the chosen one-op budget. No claim
        // of continuous publication unless every ordinary sample remains fresh.
        Assert.Equal(Active,cache.ActiveIdentity);
    }

    [Fact]
    public void IneligibleActivePreferenceStillSkipsOldWorkIntentionally()
    {
        var(cache,scene)=Warm(permanent:true);
        for(var frame=36;frame<=61;frame++)
        {
            var now=frame/30d;scene.Begin(now);
            cache.Update(Requested,Vector2.Zero,now,100,scene,scene.Deadline,preferRequestedOrigin:true);
        }
        Assert.All(scene.Calls,c=>Assert.False(c.Old));
        Assert.False(cache.TrySnapshot(Vector2.Zero,61/30d,out _,Active));
    }

    [Fact]
    public void FreshVisibleWindowDoesNotReserveFramesForUnneededMaintenance()
    {
        var(cache,scene)=Warm(permanent:true);
        for(var frame=1;frame<=10;frame++)
        {
            var now=frame/30d;scene.Begin(now);
            cache.Update(Requested,Vector2.Zero,now,100,scene,scene.Deadline,preferRequestedOrigin:false);
        }
        Assert.Equal(10,scene.Calls.Count);
        Assert.All(scene.Calls,c=>Assert.False(c.Old));
        Assert.True(cache.TrySnapshot(Vector2.Zero,10/30d,out _,Active));
    }

    [Fact]
    public void ExpensivePurePendingCountsAsAnOpportunityWithoutInventingRayUse()
    {
        var(cache,scene)=Warm(permanent:true);scene.ZeroPendingNative=true;
        for(var frame=36;frame<=61;frame++)
        {
            var now=frame/30d;scene.Begin(now);
            var status=cache.Update(Requested,Vector2.Zero,now,100,scene,scene.Deadline,preferRequestedOrigin:false);
            Assert.Equal(scene.Raycasts,status.Rays);
        }
        Assert.Contains(scene.Calls,c=>c.Old);Assert.Contains(scene.Calls,c=>!c.Old);
        Assert.True(cache.TrySnapshot(Vector2.Zero,61/30d,out _,Active));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(45)] [InlineData(1000)]
    public void BulkReservationCannotRemoveEitherChosenGenerationsBoundedOpportunity(int reserve)
    {
        var(cache,scene)=Warm(permanent:true);
        for(var frame=36;frame<=61;frame++)
        {
            var now=frame/30d;scene.Begin(now);
            var status=cache.Update(Requested,Vector2.Zero,now,100,scene,scene.Deadline,
                originReserve:reserve,preferRequestedOrigin:false);
            Assert.Equal(scene.Raycasts,status.Rays);Assert.InRange(status.Rays,0,100);
        }
        Assert.Contains(scene.Calls,c=>c.Old);Assert.Contains(scene.Calls,c=>!c.Old);
    }

    [Fact]
    public void VariableNativeCapacityPreservesCurrentCoverageAndStillRequiresExplicitPromotion()
    {
        var(cache,scene)=Warm();var prepared=false;
        int[] costs=[2050,250,650,1000];
        for(var frame=36;frame<=95;frame++)
        {
            var now=frame/30d;scene.Begin(now);scene.CostMicroseconds=costs[frame%costs.Length];
            var status=cache.Update(Requested,Vector2.Zero,now,100,scene,scene.Deadline,
                preferRequestedOrigin:false,allowPendingPromotion:false);
            Assert.Equal(scene.Raycasts,status.Rays);Assert.InRange(status.Rays,0,100);
            Assert.Equal(Active,cache.ActiveIdentity);
            Assert.True(cache.TrySnapshot(Vector2.Zero,now,out _,Active));
            Assert.False(cache.TrySnapshot(Vector2.Zero,now,out _,Requested));
            prepared|=cache.TrySupportedSnapshot(Vector2.Zero,now,out var candidate,Requested)
                &&candidate!.Half==new Vector2(2);
        }
        Assert.True(prepared);
        var time=95/30d;scene.Begin(time);
        cache.Update(Requested,Vector2.Zero,time,0,scene,scene.Deadline,allowPendingPromotion:true);
        Assert.Equal(0,scene.Raycasts);
        Assert.True(cache.TrySnapshot(Vector2.Zero,time,out _,Requested));
        Assert.False(cache.TrySnapshot(Vector2.Zero,time,out _,Active));
    }

    private static LayerTriangle Face(Vector3 a,Vector3 b,Vector3 c)
    {
        var n=Vector3.Normalize(Vector3.Cross(b-a,c-a));if(n.Y<0)n=-n;
        return new(a,b,c,n);
    }
    private static readonly LayerTriangle Lower=Face(new(-1,0,-2),new(0,0,-2),new(0,0,2));
    private static readonly LayerTriangle Upper=Face(new(0,.2f,-2),new(1,.2f,2),new(0,.2f,2));
    private static readonly LayerTriangle RiserLow=Face(new(0,0,-2),new(0,0,2),new(0,.2f,2));
    private static readonly LayerTriangle RiserHigh=Face(new(0,0,-2),new(0,.2f,2),new(0,.2f,-2));

    // Both origins share the SAME production discovery machine. The synthetic
    // old adapter maps gathered contacts to the measured lower face, while the
    // requested origin needs the actual raised face. This mapping does not claim
    // native wall-clearance authorization; the floor/riser evidence is real.
    private sealed class SharedTerrain : IClothSupportQueries,IIncrementalClothSupportQueries,
        ILayerFloorScene,ILayerFloorQueryAttempts
    {
        public readonly LayerFloorDiscovery Discovery=new();
        public int Raycasts{get;private set;}
        public LayerQueryResult LastResult{get;private set;}
        public int FrameBudget=1000,OldCalls,RequestedCalls,WallCalls;
        public double? CandidateAt,AdmittedAt;
        private int allowance;private double now;
        public bool CanQuery=>Raycasts<FrameBudget&&Raycasts<allowance;
        public bool Deadline()=>Raycasts<FrameBudget;
        public void Begin(double time)
        {
            now=time;Raycasts=0;allowance=FrameBudget;
            Discovery.BeginFrame(new(new(-.1f,0,0),Lower),now,new(.3f,0),5);
        }
        public void PrepareQuery(int count)=>allowance=Raycasts+count;
        public bool TryVertex(SupportQueryIdentity identity,Vector2 nominal,out Vector3 contact)
        {
            if(identity==Active){OldCalls++;nominal.X-=.5f;}else RequestedCalls++;
            LastResult=Discovery.Query(nominal,this,out var hit);contact=hit.Position;
            if(identity!=Active&&LastResult==LayerQueryResult.Success)AdmittedAt??=now;
            return LastResult==LayerQueryResult.Success;
        }
        public bool TryCell(SupportQueryIdentity identity,Vector3 a,Vector3 b,Vector3 c,Vector3 d,out float ceiling)
        {
            if(identity==Active)OldCalls++;else RequestedCalls++;
            var center=(a+b+c+d)/4;
            LastResult=Discovery.Query(new(center.X,center.Z),this,out var hit);
            ceiling=0;if(LastResult!=LayerQueryResult.Success)return false;
            // Complete actual planar geometry proof, not midpoint-only admission.
            // Native-op capacity is modeled; no claim of actual CPU proof cost.
            LastResult=Discovery.Layer.TryCellCeiling(hit,a,b,c,d,out ceiling);
            return LastResult==LayerQueryResult.Success;
        }
        public bool TryFloor(ClothFloorProbe probe,out LayerFloorHit hit)
        {
            hit=default;if(!CanQuery)return false;Raycasts++;
            var face=probe.Position.X>0?Upper:Lower;
            var at=new Vector3(probe.Position.X,face.A.Y,probe.Position.Y);
            if(!face.Contains(at)||!ClothFloorQueryPolicy.Accept(probe,at,face.Normal,out _))return false;
            if(face==Upper)CandidateAt??=now;
            hit=new(at,face);return true;
        }
        public bool TryWall(Vector3 from,Vector3 to,out LayerTriangle triangle)
        {
            triangle=default;if(!CanQuery)return false;Raycasts++;WallCalls++;
            if(from.X>=0||to.X<=0||from.Y is <=0 or >=.2f||Math.Abs(from.Z)>=2)return false;
            var diagonal=.2f*(from.Z+2)/4;
            triangle=from.Y<=diagonal?RiserLow:RiserHigh;return true;
        }
    }

    [Theory]
    [InlineData(25)] [InlineData(30)]
    public void RealSharedDiscoveryKeepsItsThreePhaseLeaseAcrossGenerationFairness(int fps)
    {
        var center=new Vector2(.3f,0);var cache=new RollingClothSupport(.1f,.1f,0);
        var scene=new SharedTerrain();scene.Begin(0);
        cache.Update(Active,center,0,100,scene,scene.Deadline);
        Assert.True(cache.TrySnapshot(center,0,out _,Active));
        scene.OldCalls=0;scene.FrameBudget=1;
        for(var frame=0;frame<50;frame++)
        {
            var now=1.2+frame/(double)fps;scene.Begin(now);
            cache.Update(Requested,center,now,100,scene,scene.Deadline,preferRequestedOrigin:false);
            Assert.InRange(scene.Raycasts,0,1);
            if(cache.TrySnapshot(center,now,out _,Requested))break;
        }
        Assert.True(scene.OldCalls>0);Assert.True(scene.RequestedCalls>0);
        Assert.NotNull(scene.CandidateAt);Assert.NotNull(scene.AdmittedAt);
        Assert.InRange(scene.AdmittedAt!.Value-scene.CandidateAt!.Value,0,.1);
        Assert.Equal(2,scene.WallCalls);
        Assert.Equal(Requested,cache.ActiveIdentity);
    }

    private void Describe(Scene scene,RollingClothSupport cache,double now)
        =>output.WriteLine($"oldCalls={scene.Calls.Count(c=>c.Old)}; pendingCalls={scene.Calls.Count(c=>!c.Old)}; pendingSuccess={scene.Calls.Count(c=>!c.Old&&c.Result==LayerQueryResult.Success)}; candidateResets={scene.CandidateResets}; oldFull={cache.TrySnapshot(Vector2.Zero,now,out _,Active)}; active={cache.ActiveIdentity}");
}
