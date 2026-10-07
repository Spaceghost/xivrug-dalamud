using System.Diagnostics;
using System.Numerics;
using Lumina;
using XivSurface.RenderedGeometry;

internal static class ActualRockProbe
{
    internal static async Task Run(string sqpack)
    {
        using var game = new GameData(sqpack);
        using var stream = new RenderedTerrainStream(new ProfiledTerrainLoader(new ArchiveReader(game)));
        var profile = LimsaRockTerrainProfile.OptInKnownVanilla();
        // Frozen observed descriptors, not a query for the current player or loaded resources.
        TerrainInstance Tile(int index, uint resource, Vector3 translation) => new((ulong)index,
            $"bg/ffxiv/sea_s1/twn/s1t2/bgplate/{index:D4}.mdl", resource,
            Matrix4x4.CreateTranslation(translation), new(-32,-5,-32), new(32,80,33));
        TerrainInstance[] rock = [Tile(14,1828818217,new(-96,0,-32)), Tile(15,1348576409,new(-32,0,-32))];
        var point = new Vector2(-65.33346f,-17.446589f);
        var broad = await Check(1, rock, new(new(-65.5f,19,-17.5f),new(3,2,3)), 154, 0);
        var heights = broad.Triangles.ToArray().Select(t => (Triangle:t,Height:Height(t,point)))
            .Where(t => t.Height.HasValue).OrderBy(t => Math.Abs(t.Height!.Value-18.984f)).ToArray();
        Tests.Require(heights.Length > 0, "Actual rock does not cover fixture point");
        var surface = heights[0];
        Tests.Require(Math.Abs(surface.Height!.Value-18.98386f)<.0001f
            && surface.Triangle.SourceMesh==1 && surface.Triangle.SourceTriangle==402
            && surface.Triangle.ModelPath==Tests.Path14
            && surface.Triangle.Material.EndsWith("/s1t0_t1_wall2a.mtrl",StringComparison.Ordinal), "Exact rock profile changed");
        Tests.Require(broad.Triangles.ToArray().Count(t => Math.Abs(Vector3.Normalize(Vector3.Cross(t.B-t.A,t.C-t.A)).Y)<.15f)==13,
            "Vertical rock/stair faces lost");
        var narrow = await Check(2,rock,new(new(point.X,19,point.Y),new(2.5f,2,2.5f)),122,0);
        Tests.Require(narrow.Triangles.ToArray().Select(t=>t.ModelPath).Distinct().Count()==2,"Lost tile boundary");
        await Check(3,[rock[0],Tile(23,1505043351,new(-96,0,32))],new(new(-99.38f,18.53f,-1.23f),new(5,3,5)),128,4);
        Console.WriteLine($"ACTUAL ROCK PASS exactXZ={point} groundY={surface.Height:F5}; three archive fixture cases; no live binding or physics activation");

        async Task<TerrainCandidateBatch> Check(ulong observation,TerrainInstance[] instances,TerrainInterest interest,int count,int omitted)
        {
            var timer=Stopwatch.StartNew();
            stream.Request(129,observation,instances,TerrainCaptureCompleteness.CompleteTerrainPlates,interest,observation,profile,"offline-reviewed-fixture");
            var state=await Tests.Drain(stream);
            Tests.Require(state.Phase=="CandidateReady"&&state.Batch is not null,state.Phase);
            var batch=state.Batch!;
            Tests.Require(batch.Triangles.Length==count&&batch.UnreviewedMaterialTriangles==omitted,"Actual candidate count/omission changed");
            Tests.Require(!batch.LoadedContentVerified&&!batch.AuthorizesWorldSupport&&!batch.CoversAllSceneMaterials,"False authority");
            Console.WriteLine($"ACTUAL ROCK batch={observation} triangles={count} omitted={omitted} workerMs={batch.BuildMilliseconds:F3} elapsedMs={timer.Elapsed.TotalMilliseconds:F3} profile={profile.Fingerprint}");
            return batch;
        }
    }
    private static float? Height(TerrainTriangle t,Vector2 point)
    {
        var a=new Vector2(t.A.X,t.A.Z);var b=new Vector2(t.B.X,t.B.Z);var c=new Vector2(t.C.X,t.C.Z);
        static float Cross(Vector2 a,Vector2 b)=>a.X*b.Y-a.Y*b.X;
        var determinant=Cross(b-a,c-a);if(Math.Abs(determinant)<1e-8f)return null;
        var u=Cross(point-a,c-a)/determinant;var v=Cross(b-a,point-a)/determinant;
        return u< -1e-5f||v< -1e-5f||u+v>1.00001f?null:t.A.Y+(t.B.Y-t.A.Y)*u+(t.C.Y-t.A.Y)*v;
    }
}
