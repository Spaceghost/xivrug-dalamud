using System.Numerics;
using XivSurface.RenderedGeometry;

internal static class RockProfileTests
{
    private const string Rock = "bg/ffxiv/sea_s1/twn/common/material/s1t0_t1_wall2a.mtrl";
    private static readonly TerrainContentProfile Profile = LimsaRockTerrainProfile.OptInKnownVanilla();
    internal static async Task Run()
    {
        var count = 0;
        async Task Test(string name, Func<Task> body) { await body(); count++; Console.WriteLine($"PASS rock profile: {name}"); }
        await Test("legacy opt-in fixture is unchanged", () =>
        {
            var old = TerrainContentProfile.OptInKnownVanillaStairs();
            Tests.Require(!old.TryAsset(LimsaRockTerrainProfile.Model15, out _) && !old.IncludesMaterial(Rock)
                && old.MaterialPins.Count() == 3, "Existing default fixture broadened");
            return Task.CompletedTask;
        });
        await Test("explicit exact pins and stable fingerprint", () =>
        {
            Tests.Require(Profile.TryAsset(LimsaRockTerrainProfile.Model15, out var pin)
                && pin.Sha256 == LimsaRockTerrainProfile.Model15Hash && pin.CullingGridCount == 5
                && Profile.MaterialPins.Count() == 10
                && Profile.Fingerprint == "eece11c1f513e36abb91e9503ef327e99d49181fe8fc13c8d44499c2963c259f", "Profile drift without review");
            foreach (var path in new[] { Tests.Path14, Tests.Path23 })
            {
                TerrainContentProfile.OptInKnownVanillaStairs().TryAsset(path, out var old);
                Tests.Require(Profile.TryAsset(path, out var next) && next == old, "Old model pin changed");
            }
            Tests.Require(!Profile.IncludesMaterial("bg/ffxiv/sea_s1/twn/common/material/s1t0_b0_stpv2_h.mtrl"), "Unreviewed material silently added");
            return Task.CompletedTask;
        });
        await Test("new fixture still requires explicit selection before IO", async () =>
        {
            var loader = new Loader(); using var stream = new RenderedTerrainStream(loader);
            Request(stream, 1, null); var state = await Tests.Drain(stream);
            Tests.Require(state.Phase == "ProfileUnavailable" && loader.Calls == 0, "Profile opted in automatically");
        });
        await Test("synthetic tile15 retains indexed vertical face and grid4", async () =>
        {
            using var stream = new RenderedTerrainStream(new Loader()); Request(stream);
            var batch = (await Tests.Drain(stream)).Batch!;
            Tests.Require(batch.Triangles.Length == 2 && batch.Triangles[1].CullingGrid == 4
                && Vector3.Cross(batch.Triangles[1].B - batch.Triangles[1].A, batch.Triangles[1].C - batch.Triangles[1].A) == new Vector3(-1, 0, 0), "Geometry changed");
            Tests.Require(!batch.AuthorizesWorldSupport && !batch.LoadedContentVerified && !batch.CoversAllSceneMaterials
                && batch.UnreviewedMaterialTriangles == 0 && batch.Confidence == "AssumedKnownVanilla", "False authority");
        });
        await Test("tile15 grid5 is outside its five-grid bound", async () =>
        {
            using var stream = new RenderedTerrainStream(new Loader(group: 5)); Request(stream);
            Tests.Require((await Tests.Drain(stream)).Batch is null, "Out-of-bounds draw group accepted");
        });
        await Test("unreviewed local material stays an explicit omission", async () =>
        {
            using var stream = new RenderedTerrainStream(new Loader(material: "bg/unreviewed.mtrl")); Request(stream);
            var batch = (await Tests.Drain(stream)).Batch!;
            Tests.Require(batch.Triangles.Length == 0 && batch.UnreviewedMaterialTriangles == 2, "Partial profile claimed complete");
        });
        await Test("unreviewed tile refuses before loader", async () =>
        {
            var loader = new Loader(); using var stream = new RenderedTerrainStream(loader);
            Request(stream, path: "bg/ffxiv/sea_s1/twn/s1t2/bgplate/0016.mdl");
            Tests.Require((await Tests.Drain(stream)).Phase == "UnsupportedModel" && loader.Calls == 0, "Wildcard model admission");
        });
        await Test("wrong model digest never becomes a candidate", async () =>
        {
            using var stream = new RenderedTerrainStream(new Loader(wrongHash: true)); Request(stream);
            Tests.Require((await Tests.Drain(stream)).Phase == "ContentProfileMismatch", "Model hash ignored");
        });
        Console.WriteLine($"ROCK PROFILE TOTAL {count} PASS (synthetic only; --actual-rock separately reads installed assets)");
    }
    private static void Request(RenderedTerrainStream stream, ulong sequence, TerrainContentProfile? profile,
        string path = LimsaRockTerrainProfile.Model15)
    {
        stream.Request(129, sequence, [Tests.Instance(path) with { InstanceKey = 15, ResourceId = 1348576409 }],
            TerrainCaptureCompleteness.CompleteTerrainPlates, new(Vector3.Zero, new(3)), sequence, profile);
    }
    private static void Request(RenderedTerrainStream stream, string path = LimsaRockTerrainProfile.Model15)
        => Request(stream, 1, Profile, path);
    private sealed class Loader(uint group = 4, string material = Rock, bool wrongHash = false) : ITerrainCandidateLoader
    {
        public int Calls;
        public ValueTask<RenderedMdlGeometry?> LoadAsync(string path, TerrainContentProfile profile, string revision, CancellationToken ct)
        {
            Calls++; ct.ThrowIfCancellationRequested(); profile.TryAsset(path, out var pin);
            // Independently constructed test topology; actual asset SHA is an expected
            // profile fixture label, not a claim these synthetic vertices came from it.
            RenderedMdlGeometry geometry = new(path, wrongHash ? new string('0', 64) : pin.Sha256,
                [new(0,0,0),new(1,0,0),new(0,0,1),new(0,1,1)], [1,1,1,1], [0,1,2,0,2,3],
                [new(1,0,0,4,0,6)], [new(1,0,0,6,group)], [material], new(0), new(1), true,true,true);
            return ValueTask.FromResult<RenderedMdlGeometry?>(geometry);
        }
    }
}
