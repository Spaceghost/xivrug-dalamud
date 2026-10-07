using XivSurface.Core;

if (args.Length != 1) { Console.Error.WriteLine("Usage: XivSurface.ClothReplay <cloth-replay-*.json>"); return 2; }
try
{
    var report = ClothCollisionReportFile.Read(args[0]);
    Console.WriteLine($"Version {report.Version}; territory {report.Zone}; {report.Completion}");
    if (report.PathUnknown is { } path)
    {
        Console.WriteLine($"PATH captured {path.Result}: {path.Reason}; faces={path.Layer.Faces.Length}; portals={path.Layer.Portals.Length}; now={path.Layer.Now:R}");
        Console.WriteLine($"  original call follow-up: discovery={path.DiscoveryResult} at {path.DiscoveryTarget}; final attempt={path.AttemptResult}; copy ms={path.SnapshotCopyMilliseconds:R}");
        Run(limit => LocalFloorLayer.Replay(path, limit));
    }
    else Console.WriteLine("PATH Unknown was not observed in this window.");
    if (report.CellPending is { } cell)
    {
        Console.WriteLine($"CELL captured {cell.Result}: {cell.Reason}; faces={cell.Layer.Faces.Length}; portals={cell.Layer.Portals.Length}; now={cell.Layer.Now:R}");
        Console.WriteLine($"  original call follow-up: discovery={cell.DiscoveryResult} at {cell.DiscoveryTarget}; final attempt={cell.AttemptResult}; copy ms={cell.SnapshotCopyMilliseconds:R}");
        Run(limit => LocalFloorLayer.Replay(cell, limit));
    }
    else Console.WriteLine("CELL non-success ceiling followed by a Pending cell attempt was not observed in this window.");
    return 0;
}
catch (Exception ex) { Console.Error.WriteLine($"Replay refused: {ex.GetType().Name}: {ex.Message}"); return 1; }

static void Run(Func<int, ClothReplayResult> replay)
{
    foreach (var limit in new[] { 16, 64, 256, 1024, int.MaxValue })
    {
        var value = replay(limit);
        Console.WriteLine($"  checks={(limit == int.MaxValue ? "unlimited" : limit)}: {value.Result}; {value.Reason}; used={value.DeadlineChecks}; missing={value.MissingWitness}; points={value.Path.Length}; ceiling={value.Ceiling:R}; lift={value.Lift:R}");
    }
    Console.WriteLine("  Each run starts from the exact captured state; deterministic check limits are not resumable native work.");
}
