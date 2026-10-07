using Lumina;
using Lumina.Data;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Numerics;
using XivSurface.RenderedGeometry;

if (args.Length == 2 && args[0] == "--actual-rock")
{
    await ActualRockProbe.Run(args[1]);
    return;
}

if(args.Length==2 && args[0]=="--material-hashes")
{
    using var game=new GameData(args[1]);
    foreach(var name in new[]{"s1t0_b0_sbwd2_h","s1t0_b0_ston1_h","s1t0_b0_stpv2_h_bgs0","s1t0_b0_stpv2_h"})
    {
        var path=$"bg/ffxiv/sea_s1/twn/common/material/{name}.mtrl";
        var bytes=game.GetFile<FileResource>(path)!.Data;
        Console.WriteLine($"{name} bytes={bytes.Length} SHA256={Convert.ToHexStringLower(SHA256.HashData(bytes))}");
    }
    return;
}

if (args.Length == 2 && args[0] == "--actual")
{
    using var game = new GameData(args[1]);
    using var stream = new RenderedTerrainStream(new ProfiledTerrainLoader(new ArchiveReader(game)));
    var profile = TerrainContentProfile.OptInKnownVanillaStairs();
    var center = new Vector3(-99.38f, 18.53f, -1.23f);
    TerrainInstance[] instances = [ Tests.Instance(Tests.Path14) with { World = Matrix4x4.CreateTranslation(-96,0,-32) },
        Tests.Instance(Tests.Path23) with { InstanceKey = 23, ResourceId = 1505043351, World = Matrix4x4.CreateTranslation(-96,0,32) } ];
    var timer = Stopwatch.StartNew(); var before = GC.GetTotalAllocatedBytes(true);
    stream.Request(129, 1, instances, TerrainCaptureCompleteness.CompleteTerrainPlates, new(center, new(5,3,5)), 1,profile);
    var first = await Tests.Drain(stream); var coldTime = timer.Elapsed.TotalMilliseconds; var coldBytes = GC.GetTotalAllocatedBytes(true) - before;
    Tests.Require(first.Phase == "CandidateReady", first.Phase);
    var tris = first.Batch!.Triangles.ToArray();
    var vertical = tris.Count(t => Math.Abs(Vector3.Normalize(Vector3.Cross(t.B-t.A,t.C-t.A)).Y) < .15f);
    var horizontal = tris.Count(t => Math.Abs(Vector3.Normalize(Vector3.Cross(t.B-t.A,t.C-t.A)).Y) > .99f);
    Console.WriteLine($"Observed scoped actual triangles={tris.Length} vertical={vertical} horizontal={horizontal}");
    Tests.Require(tris.Length == 128, $"Expected 128 reviewed-material triangles (4 unsupported-key triangles excluded), got {tris.Length}");
    Tests.Require(first.Batch.UnreviewedMaterialTriangles == 4, $"Expected 4 explicitly reported unreviewed material faces, got {first.Batch.UnreviewedMaterialTriangles}");
    Tests.Require(vertical == 44 && horizontal == 84, "Actual vertical/horizontal topology changed");
    Tests.Require(tris.Select(t => t.ModelPath).Distinct().Count() == 2, "Lost tile union");
    var heights = tris.Where(t => Math.Abs(t.A.Y-t.B.Y) < .0001f && Math.Abs(t.A.Y-t.C.Y) < .0001f)
        .Select(t => MathF.Round(t.A.Y, 3)).Distinct().Order().ToArray();
    Tests.Require(heights.Zip(heights.Skip(1)).Count(v => Math.Abs(v.Second-v.First-.15f) < .002f) >= 4, "Lost actual 0.15-yalm treads");
    Console.WriteLine($"ACTUAL PASS triangles={tris.Length} horizontal={horizontal} vertical={vertical} omittedMaterial={first.Batch.UnreviewedMaterialTriangles} tiles=2 coldMs={coldTime:F3} allocated={coldBytes} confidence={first.Batch.Confidence} loadedVerified={first.Batch.LoadedContentVerified}");
    var times = new List<double>(); var builds=new List<double>();var allocations = new List<long>();
    for (var frame = 0; frame < 140; frame++)
    {
        timer.Restart(); before = GC.GetTotalAllocatedBytes(true);
        stream.Request(129, (ulong)(frame+2), instances, TerrainCaptureCompleteness.CompleteTerrainPlates, new(center+new Vector3((frame%2)*.0001f,0,0), new(5,3,5)), frame+2,profile);
        var done = await Tests.Drain(stream);
        Tests.Require(done.Batch!.Triangles.Length == 128, "Warm topology changed");
        Tests.Require(done.Batch.UnreviewedMaterialTriangles == 4, "Warm omission evidence changed");
        if (frame >= 20) { times.Add(timer.Elapsed.TotalMilliseconds);builds.Add(done.Batch.BuildMilliseconds); allocations.Add(GC.GetTotalAllocatedBytes(true)-before); }
    }
    times.Sort(); builds.Sort();allocations.Sort();
    Console.WriteLine($"ACTUAL 120 warm request-to-publication (includes scheduler/poll) medianMs={times[60]:F3} p95Ms={times[114]:F3} maxMs={times[^1]:F3} medianAllocated={allocations[60]}");
    Console.WriteLine($"ACTUAL warm worker assembly medianMs={builds[60]:F3} p95Ms={builds[114]:F3} maxMs={builds[^1]:F3}");
    return;
}
MdlBaselineTests.Run();
await Tests.Run();
await RockProfileTests.Run();

sealed class ArchiveReader(GameData game) : ITerrainAssetReader
{
    public ValueTask<ReadOnlyMemory<byte>?> ReadAsync(string path, int maximumBytes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Diagnostic-only installed SqPack source: Lumina reconstructs before returning length.
        // Production reader needs metadata-level preallocation cap; this source is not a hostile-file service.
        var data = game.GetFile<FileResource>(path)?.Data;
        if (data is not null && data.Length > maximumBytes) throw new InvalidDataException("Resource cap");
        return ValueTask.FromResult<ReadOnlyMemory<byte>?>(data is null ? null : data);
    }
}
