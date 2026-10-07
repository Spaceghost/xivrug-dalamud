using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using XivCloth.Core;

Console.WriteLine($"XPBD finite-face collision cost probe: {DateTime.UtcNow:O}; {RuntimeInformation.FrameworkDescription}; process CPUs={Environment.ProcessorCount}");
Console.WriteLine("Synthetic steady flat cloth; 8 iterations, 2 x 1/120s steps per 60Hz call, including bounded finite-face sweeps. No self-collision, scene acquisition, feet, publication, upload or rendering cost included. Budget refusals are reported, not timed as accepted simulation.");
foreach (var file in new[] { "XpbdGeometry.cs", "XpbdCloth.cs", "XpbdStepInputs.cs", "TriangleContacts.cs", "TriangleSweep.cs", "PrismBounds.cs" })
{
    using var source = typeof(Program).Assembly.GetManifestResourceStream($"XivCloth.CostProbe.Source.{file}")
        ?? throw new InvalidOperationException($"Missing embedded source evidence: {file}");
    Console.WriteLine($"source {file} {Convert.ToHexStringLower(SHA256.HashData(source))}");
}
var corePath = typeof(XpbdCloth).Assembly.Location;
using (var core = File.OpenRead(corePath))
    Console.WriteLine($"loadedCore {Path.GetFileName(corePath)} {Convert.ToHexStringLower(SHA256.HashData(core))}");
foreach (var size in new[] { 9, 15, 21 })
foreach (var tiles in new[] { 1, 8 })
{
    var definition = Sheet(size);
    var scene = Plane(tiles);
    var solver = new XpbdCloth(definition);
    XpbdAdvance warmup = default;
    for (var i = 0; i < 120; i++)
    {
        warmup = solver.Advance(1d / 60, scene);
        if (warmup.Status != XpbdStatus.Ready) break;
        Check(warmup);
    }
    if (warmup.Status != XpbdStatus.Ready)
    {
        Console.WriteLine($"particles={definition.VertexCount}, sceneTriangles={scene.Triangles.Length}, refused={warmup.Status}, collisionWork={warmup.TrianglePairs + warmup.SweepNodes + warmup.BroadphaseNodes}; no accepted-step timing reported");
        continue;
    }
    var samples = new double[120];
    using var process = Process.GetCurrentProcess();
    var cpuStart = process.TotalProcessorTime;
    var allocated = GC.GetAllocatedBytesForCurrentThread();
    XpbdAdvance last = default;
    for (var i = 0; i < samples.Length; i++)
    {
        var start = Stopwatch.GetTimestamp();
        last = solver.Advance(1d / 60, scene);
        samples[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        if (last.Status != XpbdStatus.Ready) break;
        Check(last);
    }
    var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
    if (last.Status != XpbdStatus.Ready)
    {
        Console.WriteLine($"particles={definition.VertexCount}, sceneTriangles={scene.Triangles.Length}, refused={last.Status} during measurement, collisionWork={last.TrianglePairs + last.SweepNodes + last.BroadphaseNodes}; no accepted-step timing reported");
        continue;
    }
    var cpu = (process.TotalProcessorTime - cpuStart).TotalMilliseconds / samples.Length;
    Array.Sort(samples);
    Console.WriteLine($"particles={definition.VertexCount}, sceneTriangles={scene.Triangles.Length}, wallMs mean={samples.Average():F4} p50={samples[60]:F4} p95={samples[114]:F4} max={samples[^1]:F4}, processCpuMsMean={cpu:F4}, allocBytes={allocatedBytes}, constraintSolves={last.ConstraintSolves}, contactChecks={last.ContactChecks}, collisionWork={last.TrianglePairs + last.SweepNodes + last.BroadphaseNodes}");
    var beforeCapture = GC.GetAllocatedBytesForCurrentThread();
    var captureStart = Stopwatch.GetTimestamp();
    for (var i = 0; i < 120; i++) GC.KeepAlive(solver.Capture());
    var captureElapsed = Stopwatch.GetElapsedTime(captureStart).TotalMilliseconds;
    var captureBytes = GC.GetAllocatedBytesForCurrentThread() - beforeCapture;
    Console.WriteLine($"capture separate meanMs={captureElapsed / 120:F4}, bytesPerFrame={captureBytes / 120}; penetration={scene.MaximumVertexPenetration(solver.Capture().Positions):R}");
}

static void Check(XpbdAdvance result)
{
    if (result.Status != XpbdStatus.Ready || result.Substeps != 2)
        throw new InvalidOperationException($"Probe rejected: {result}");
}

static XpbdDefinition Sheet(int size)
{
    var rest = new Vector3[size * size];
    var uv = new Vector2[rest.Length];
    var mass = new float[rest.Length];
    var indices = new List<int>();
    var edges = new List<DistanceEdge>();
    for (var z = 0; z < size; z++) for (var x = 0; x < size; x++)
    {
        var i = z * size + x;
        rest[i] = new(-1 + 2f * x / (size - 1), .008f, -1 + 2f * z / (size - 1));
        uv[i] = new((float)x / (size - 1), (float)z / (size - 1));
        mass[i] = 1;
        if (x + 1 < size) edges.Add(new(i, i + 1, MaterialEdge.Stretch));
        if (z + 1 < size) edges.Add(new(i, i + size, MaterialEdge.Stretch));
        if (x + 1 < size && z + 1 < size)
        {
            indices.AddRange([i, i + size, i + 1, i + 1, i + size, i + size + 1]);
            edges.Add(new(i, i + size + 1, MaterialEdge.Shear));
            edges.Add(new(i + 1, i + size, MaterialEdge.Shear));
        }
    }
    return new(rest, uv, indices.ToArray(), mass, edges.ToArray());
}

static MeasuredTriangleScene Plane(int tiles)
{
    var triangles = new List<MeasuredTriangle>();
    for (var z = 0; z < tiles; z++) for (var x = 0; x < tiles; x++)
    {
        var a = new Vector3(-2 + 4f * x / tiles, 0, -2 + 4f * z / tiles);
        var b = a + new Vector3(0, 0, 4f / tiles);
        var c = a + new Vector3(4f / tiles, 0, 4f / tiles);
        var d = a + new Vector3(4f / tiles, 0, 0);
        triangles.Add(new(a, b, c));
        triangles.Add(new(a, c, d));
    }
    return new(1, triangles.ToArray());
}
