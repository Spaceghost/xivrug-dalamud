using System.Numerics;
using XivSurface.RenderedGeometry;

static class Tests
{
    internal const string Path14 = "bg/ffxiv/sea_s1/twn/s1t2/bgplate/0014.mdl";
    internal const string Path23 = "bg/ffxiv/sea_s1/twn/s1t2/bgplate/0023.mdl";
    private static readonly TerrainContentProfile Profile = TerrainContentProfile.OptInKnownVanillaStairs();
    private static readonly TerrainInterest Interest = new(Vector3.Zero, new(5,3,5));
    internal static TerrainInstance Instance(string path = Path14) => new(14, path, 1828818217, Matrix4x4.Identity, new(-32), new(32));
    internal static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    internal static async Task<TerrainStreamState> Drain(RenderedTerrainStream stream)
    {
        for (var i = 0; i < 1000; i++) { var state = stream.Poll(); if (!state.WorkerPending) return state; await Task.Delay(1); }
        throw new Exception("Worker did not settle in bounded test deadline");
    }
    private static void Request(RenderedTerrainStream s, ulong generation=1, TerrainInstance[]? instances=null, bool complete=true, string revision="r1")
        => s.Request(129, generation, instances ?? [Instance()], complete ? TerrainCaptureCompleteness.CompleteTerrainPlates : TerrainCaptureCompleteness.Incomplete, Interest, generation*.02,Profile, revision);
    internal static RenderedMdlGeometry Geometry(string path=Path14, int triangles=2, uint group=29, string? material=null)
    {
        Profile.TryAsset(path, out var asset);
        var indices = new int[triangles*3];
        for(var i=0;i<triangles;i++) { indices[i*3]=0; indices[i*3+1]=i%2==0?1:2; indices[i*3+2]=i%2==0?2:3; }
        return new(path, asset.Sha256, [new(0,0,0),new(1,0,0),new(0,0,1),new(0,1,1)], [1,1,1,1], indices,
            [new(12,0,0,4,0,indices.Length)], [new(12,7,0,indices.Length,group)], [material ?? TerrainContentProfile.StairMaterial], new(0), new(1), true,true,true);
    }
    internal static async Task Run()
    {
        var count=0;
        async Task Test(string name, Func<Task> body) { await body(); count++; Console.WriteLine($"PASS {name}"); }
        await Test("default profile unavailable and no loader IO", async () => {
            var loader=new Loader(); using var s=new RenderedTerrainStream(loader); s.Request(129,1,[Instance()],TerrainCaptureCompleteness.CompleteTerrainPlates,Interest,.02);
            Require((await Drain(s)).Phase=="ProfileUnavailable" && loader.Calls==0,"Default silently opted in"); });
        await Test("explicit profile labels assumption and never grants support", async () => {
            using var s=new RenderedTerrainStream(new Loader()); Request(s); var b=(await Drain(s)).Batch!;
            Require(b.Confidence=="AssumedKnownVanilla"&&!b.LoadedContentVerified&&!b.AuthorizesWorldSupport&&!b.CoversAllSceneMaterials&&b.Lod==0,"False authority"); });
        await Test("terrain culling index 29 retained as geometry", async () => {
            using var s=new RenderedTerrainStream(new Loader()); Request(s); var b=(await Drain(s)).Batch!;
            Require(b.Triangles.Length==2 && b.Triangles[0].CullingGrid==29 && b.Triangles[0].SourceSubmesh==7,"Misread culling as attribute mask"); });
        await Test("vertical triangle and original winding retained", async () => {
            using var s=new RenderedTerrainStream(new Loader()); Request(s); var t=(await Drain(s)).Batch!.Triangles[1];
            Require(Vector3.Cross(t.B-t.A,t.C-t.A)==new Vector3(-1,0,0),"Riser flattened/reversed"); });
        await Test("unsupported model fails before loader", async () => {
            var l=new Loader(); using var s=new RenderedTerrainStream(l); Request(s,instances:[Instance("bg/other.mdl")]);
            Require((await Drain(s)).Phase=="UnsupportedModel"&&l.Calls==0,"Unsupported profile admitted"); });
        await Test("incomplete capture clears previous batch", async () => {
            using var s=new RenderedTerrainStream(new Loader()); Request(s); await Drain(s); Request(s,2,complete:false);
            var state=await Drain(s); Require(state.Phase=="IncompleteTerrainInventory"&&state.Batch is null,"Partial renewed old geometry"); });
        await Test("input instance array is copied", async () => {
            var l=new BlockingLoader(); using var s=new RenderedTerrainStream(l); TerrainInstance[] a=[Instance()]; Request(s,instances:a); await l.Entered.Task;
            a[0]=a[0] with{World=Matrix4x4.CreateTranslation(100,0,0)}; l.Release.SetResult();
            Require((await Drain(s)).Batch!.Triangles[0].A==Vector3.Zero,"Input ownership escaped"); });
        await Test("old completion after zone change cannot publish", async () => {
            var l=new BlockingLoader(); using var s=new RenderedTerrainStream(l); Request(s); await l.Entered.Task;
            s.Request(130,2,[Instance()],TerrainCaptureCompleteness.CompleteTerrainPlates,Interest,.04,Profile,"r2"); l.Release.SetResult();
            var b=(await Drain(s)).Batch!; Require(b.Zone==130&&b.SourceObservationGeneration==2,"Stale zone published"); });
        await Test("abandoned provider single flight with latest request only", async () => {
            var l=new BlockingLoader(); using var s=new RenderedTerrainStream(l); Request(s); await l.Entered.Task;
            for(ulong i=2;i<100;i++) Request(s,i,revision:$"r{i}");
            Require(l.Calls==1&&s.Poll().Batch is null,"Abandoned fanout"); l.Release.SetResult();
            var b=(await Drain(s)).Batch!; Require(b.SourceObservationGeneration==99&&l.Calls==2,"Queued obsolete requests"); });
        await Test("continuous same-content captures do not starve cold loading", async () => {
            var l=new BlockingLoader();using var s=new RenderedTerrainStream(l);Request(s);await l.Entered.Task;
            for(ulong i=2;i<100;i++)Request(s,i);
            Require(!l.Token.IsCancellationRequested,"Cold read canceled on mere capture generation");l.Release.SetResult();
            var state=await Drain(s);Require(state.ObservationGeneration==99&&state.Batch!.SourceObservationGeneration==1&&l.Calls==1,"Cold cache did not survive renewed publication");});
        await Test("identical complete renewal preserves immutable batch identity",async()=>{
            var l=new Loader();using var s=new RenderedTerrainStream(l);Request(s);var first=(await Drain(s)).Batch!;
            Request(s,2);var second=s.Poll();Require(ReferenceEquals(first,second.Batch)&&second.Batch.GeometryGeneration==first.GeometryGeneration
                &&second.ObservationGeneration==2&&second.ObservationCapturedSeconds==.04&&!second.WorkerPending&&l.Calls==1,"Refresh rebuilt or hid stable terrain");});
        await Test("stale capture cannot renew geometry",async()=>{
            using var s=new RenderedTerrainStream(new Loader());Request(s,2);await Drain(s);Request(s,1);
            Require(s.Poll().Batch is null&&s.Poll().Phase=="StaleObservation","Backward observation renewed");});
        await Test("new incomplete observation blocks older complete resurrection",async()=>{
            using var s=new RenderedTerrainStream(new Loader());Request(s,98);await Drain(s);Request(s,100,complete:false);Request(s,99);
            Require(s.Poll().Phase=="StaleObservation"&&s.Poll().Batch is null&&!s.Poll().WorkerPending,"Older geometry revived after incomplete observation");});
        await Test("new unsupported observation blocks older complete resurrection",async()=>{
            using var s=new RenderedTerrainStream(new Loader());Request(s,98);await Drain(s);Request(s,100,[Instance("bg/unreviewed.mdl")]);Request(s,99);
            Require(s.Poll().Phase=="StaleObservation"&&s.Poll().Batch is null,"Older geometry revived after unsupported observation");});
        await Test("new profile-unavailable observation also consumes freshness watermark",async()=>{
            using var s=new RenderedTerrainStream(new Loader());Request(s,98);await Drain(s);
            s.Request(129,100,[Instance()],TerrainCaptureCompleteness.CompleteTerrainPlates,Interest,2);
            Request(s,99);Require(s.Poll().Phase=="StaleObservation"&&s.Poll().Batch is null,"Unavailable profile did not supersede older scene");});
        await Test("malformed clock or zone does not poison later valid observation",async()=>{
            using var s=new RenderedTerrainStream(new Loader());Request(s,98);await Drain(s);
            s.Request(129,1000,[Instance()],TerrainCaptureCompleteness.CompleteTerrainPlates,Interest,double.NaN,Profile);
            Request(s,99);Require((await Drain(s)).Batch is not null,"Invalid clock advanced watermark");
            s.Request(0,2000,[Instance()],TerrainCaptureCompleteness.CompleteTerrainPlates,Interest,1000,Profile);
            Request(s,100);Require((await Drain(s)).Batch is not null,"Invalid zone advanced watermark");});
        await Test("failure is not retried on every identical framework capture",async()=>{
            var l=new Loader(){Fail=true};using var s=new RenderedTerrainStream(l);Request(s);await Drain(s);Request(s,2);
            Require(!s.Poll().WorkerPending&&l.Calls==1,"Unbounded failed-read churn");s.Invalidate();Request(s,3);await Drain(s);Require(l.Calls==2,"Explicit retry unavailable");});
        await Test("replacement revision invalidates warm model cache", async () => {
            var l=new Loader(); using var s=new RenderedTerrainStream(l); Request(s); await Drain(s); Request(s,2); await Drain(s);
            Require(l.Calls==1,"Warm cache missed"); Request(s,3,revision:"replacement2"); await Drain(s); Require(l.Calls==2,"Replacement cache stale"); });
        await Test("changed transform recomputes world triangles", async () => {
            using var s=new RenderedTerrainStream(new Loader()); Request(s); await Drain(s);
            Request(s,2,[Instance() with {World=Matrix4x4.CreateTranslation(2,0,0)}]);
            Require((await Drain(s)).Batch!.Triangles[0].A==new Vector3(2,0,0),"Stale transform"); });
        await Test("changed resource observation reparses candidate", async()=> {var l=new Loader();using var s=new RenderedTerrainStream(l);Request(s);await Drain(s);Request(s,2,[Instance() with{ResourceId=123}]);await Drain(s);Require(l.Calls==2,"Reload reused stale cache");});
        await Test("explicit invalidation rereads without provider revision", async()=> {var l=new Loader();using var s=new RenderedTerrainStream(l);Request(s);await Drain(s);s.Invalidate("ReplacementChanged");Request(s,2);await Drain(s);Require(l.Calls==2,"Explicit invalidation kept cache");});
        await Test("missing loaded instance never retained", async () => {
            using var s=new RenderedTerrainStream(new Loader()); Request(s); await Drain(s); Request(s,2,[]);
            Require((await Drain(s)).Batch is null,"Missing instance retained"); });
        await Test("material subset excludes unreviewed shader", async () => {
            using var s=new RenderedTerrainStream(new Loader((p)=>Geometry(p,material:"bg/waving.mtrl"))); Request(s);
            var batch=(await Drain(s)).Batch!;
            Require(batch.Triangles.Length==0&&batch.UnreviewedMaterialTriangles==2,"Unreviewed material escaped or omission hidden"); });
        await Test("reviewed nearby material has no unreviewed omission", async () => {
            using var s=new RenderedTerrainStream(new Loader()); Request(s);
            Require((await Drain(s)).Batch!.UnreviewedMaterialTriangles==0,"Reviewed faces marked omitted"); });
        await Test("far unreviewed material does not become a local omission", async () => {
            using var s=new RenderedTerrainStream(new Loader(p=>Geometry(p,material:"bg/waving.mtrl")));
            Request(s,instances:[Instance() with {World=Matrix4x4.CreateTranslation(100,0,0)}]);
            var batch=(await Drain(s)).Batch!;
            Require(batch.Triangles.Length==0&&batch.UnreviewedMaterialTriangles==0,"Distant material counted locally"); });
        await Test("changed interest recomputes omissions without mutating prior batch", async () => {
            using var s=new RenderedTerrainStream(new Loader(p=>Geometry(p,material:"bg/waving.mtrl")));Request(s);
            var prior=(await Drain(s)).Batch!;
            s.Request(129,2,[Instance()],TerrainCaptureCompleteness.CompleteTerrainPlates,new(new(100,0,0),Vector3.One),.04,Profile,"r1");
            var next=(await Drain(s)).Batch!;
            Require(prior.UnreviewedMaterialTriangles==2&&next.UnreviewedMaterialTriangles==0&&!ReferenceEquals(prior,next),"Stale or mutable omission metadata"); });
        await Test("out of range culling index rejects whole batch", async () => {
            using var s=new RenderedTerrainStream(new Loader(p=>Geometry(p,group:30))); Request(s);
            Require((await Drain(s)).Batch is null,"Invalid culling index"); });
        await Test("triangle cap fails without partial result", async () => {
            using var s=new RenderedTerrainStream(new Loader(p=>Geometry(p,RenderedTerrainStream.MaximumTriangles+1))); Request(s);
            var st=await Drain(s); Require(st.Batch is null&&st.Phase=="GeometryBudgetOrInvalid","Silent truncation"); });
        await Test("maximum valid triangle batch succeeds", async () => {
            using var s=new RenderedTerrainStream(new Loader(p=>Geometry(p,RenderedTerrainStream.MaximumTriangles))); Request(s);
            Require((await Drain(s)).Batch!.Triangles.Length==RenderedTerrainStream.MaximumTriangles,"Budget boundary rejected"); });
        await Test("resource load failure cannot preserve old candidate", async () => {
            var l=new Loader(); using var s=new RenderedTerrainStream(l); Request(s); await Drain(s); l.Fail=true;
            Request(s,2,revision:"new"); Require((await Drain(s)).Batch is null,"Failed replacement kept stale batch"); });
        await Test("source exception is bounded and sanitized", async () => {
            using var s=new RenderedTerrainStream(new Loader(_=>throw new IOException("private path detail"))); Request(s);
            Require((await Drain(s)).Phase=="SourceUnavailable","Raw error leaked"); });
        foreach(var invalid in new[]{Instance() with {World=Matrix4x4.CreateScale(2)},Instance() with {World=Matrix4x4.CreateTranslation(float.NaN,0,0)},Instance() with{StaticTransformEligible=false}})
            await Test("invalid/dynamic/nontranslation transform rejected", async()=> {using var s=new RenderedTerrainStream(new Loader()); Request(s,instances:[invalid]); Require((await Drain(s)).Batch is null,"Invalid transform admitted");});
        await Test("duplicate identities rejected", async()=> {using var s=new RenderedTerrainStream(new Loader()); Request(s,instances:[Instance(),Instance()]); Require((await Drain(s)).Phase=="InvalidObservation","Duplicate source IDs");});
        await Test("instance budget rejects before IO", async()=> {var l=new Loader();using var s=new RenderedTerrainStream(l); Request(s,instances:Enumerable.Range(0,5).Select(i=>Instance() with{InstanceKey=(ulong)i}).ToArray()); Require((await Drain(s)).Batch is null&&l.Calls==0,"Instance cap");});
        await Test("explicit invalidation rejects late old result", async()=> {var l=new BlockingLoader();using var s=new RenderedTerrainStream(l);Request(s);await l.Entered.Task;s.Invalidate("ReplacementChanged");l.Release.SetResult();var st=await Drain(s);Require(st.Batch is null&&st.Phase=="ReplacementChanged","Invalidation lost");});
        await Test("dispose never waits for blocked IO", async()=> {var l=new BlockingLoader();var s=new RenderedTerrainStream(l);Request(s);await l.Entered.Task;s.Dispose();l.Release.SetResult();try{s.Poll();throw new Exception("Disposed usable");}catch(ObjectDisposedException){} });
        await Test("profiled loader rejects wrong dependency bytes", async()=> {var l=new ProfiledTerrainLoader(new BadReader());Require(await l.LoadAsync(Path14,Profile,"r",default) is null,"Wrong shader hash accepted");});
        await Test("revision cache bounded across repeated replacements", async()=> {var l=new Loader();using var s=new RenderedTerrainStream(l);for(ulong i=1;i<=12;i++){Request(s,i,revision:$"r{i}");await Drain(s);}Require(l.Calls==12,"Revision loads missing");});
        await Test("batch freshness rejects wrong zone future and expired sample",async()=>{
            using var s=new RenderedTerrainStream(new Loader());Request(s);var st=await Drain(s);
            Require(st.CandidateAt(129,.02) is not null&&st.CandidateAt(130,.02) is null&&st.CandidateAt(129,.019) is null
                &&st.CandidateAt(129,.271) is null&&st.CandidateAt(129,double.NaN) is null,"Freshness bypass");});
        await Test("generic profile registration owns pins and hashes full definition",()=>{
            var models=new Dictionary<string,TerrainAssetProfile>{{"bg/other/tile.mdl",new(new string('a',64),2)}};
            var mats=new Dictionary<string,string>{{"bg/other/stone.mtrl",new string('b',64)}};
            var p=TerrainContentProfile.AssumeReviewedStaticBgTerrain("fixture",models,mats);models.Clear();mats.Clear();
            Require(p.TryAsset("bg/other/tile.mdl",out _)&&p.IncludesMaterial("bg/other/stone.mtrl")&&!p.LoadedContentVerified,"Profile ownership");
            var q=TerrainContentProfile.AssumeReviewedStaticBgTerrain("fixture",new Dictionary<string,TerrainAssetProfile>{{"bg/other/tile.mdl",new(new string('c',64),2)}},new Dictionary<string,string>{{"bg/other/stone.mtrl",new string('b',64)}});
            Require(p.Fingerprint!=q.Fingerprint,"Profile ID alone reused old cache");return Task.CompletedTask;});
        await Test("generic profile rejects traversal unpinned and malformed entries",()=>{
            foreach(var path in new[]{"bg/../bad.mdl","other.mdl","bg//bad.mdl"})
            {try{TerrainContentProfile.AssumeReviewedStaticBgTerrain("bad",new Dictionary<string,TerrainAssetProfile>{{path,new(new string('a',64),2)}},new Dictionary<string,string>{{TerrainContentProfile.StairMaterial,TerrainContentProfile.MaterialHash}});throw new Exception("Bad profile accepted");}catch(ArgumentException){}}
            return Task.CompletedTask;});
        Console.WriteLine($"TOTAL {count} PASS (synthetic orchestration; --actual is separate installed-data gate)");
    }
    sealed class Loader(Func<string,RenderedMdlGeometry>? make=null) : ITerrainCandidateLoader
    {
        public int Calls; public bool Fail;
        public ValueTask<RenderedMdlGeometry?> LoadAsync(string path,TerrainContentProfile profile,string revision,CancellationToken ct)
        {Interlocked.Increment(ref Calls);ct.ThrowIfCancellationRequested();return ValueTask.FromResult<RenderedMdlGeometry?>(Fail?null:make?.Invoke(path)??Geometry(path));}
    }
    sealed class BlockingLoader : ITerrainCandidateLoader
    {
        public int Calls; public TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously),Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token;
        public async ValueTask<RenderedMdlGeometry?> LoadAsync(string path,TerrainContentProfile profile,string revision,CancellationToken ct)
        {if(Interlocked.Increment(ref Calls)==1){Token=ct;Entered.SetResult();await Release.Task;}return Geometry(path);}
    }
    sealed class BadReader : ITerrainAssetReader
    {public ValueTask<ReadOnlyMemory<byte>?> ReadAsync(string path,int max,CancellationToken ct)=>ValueTask.FromResult<ReadOnlyMemory<byte>?>(new byte[]{1,2,3});}
}
