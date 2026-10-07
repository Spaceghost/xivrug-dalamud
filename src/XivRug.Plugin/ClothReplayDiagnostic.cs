using XivSurface.Core;

namespace XivRug.Plugin;

/// <summary>One explicit request, one capture and one serializer. Only Request
/// is callable off framework. No worker holds plugin/native objects.</summary>
internal sealed class ClothReplayDiagnostic : IDisposable
{
    private readonly string directory;
    private readonly CancellationTokenSource cancellation = new();
    private Task<string>? worker;
    private int state; // 0 idle, 1 requested, 2 capturing, 3 writing, 4 disposed
    public ClothReplayDiagnostic(string directory) => this.directory = directory;
    public bool Request() => Interlocked.CompareExchange(ref state, 1, 0) == 0;
    public bool TakeRequest() => Interlocked.CompareExchange(ref state, 2, 1) == 1;
    public void Refuse() => Interlocked.CompareExchange(ref state, 0, 2);
    public void Submit(ClothCollisionReport report)
    {
        if (Interlocked.CompareExchange(ref state, 3, 2) != 2) return;
        var destination = directory;
        var token = cancellation.Token;
        worker = Task.Run(() =>
        {
            try { return "saved " + ClothCollisionReportFile.WriteNew(destination, report, token)
                + "; " + report.Completion + "; path=" + (report.PathUnknown?.Reason ?? "not observed")
                + "; cell=" + (report.CellPending?.Reason ?? "not observed")
                + "; raw floor receipts=" + report.RawFloorRays.Length; }
            catch (OperationCanceledException) { return "cancelled"; }
            catch (Exception ex) { return "write failed: " + ex.GetType().Name; }
        });
    }
    public bool Poll(out string message)
    {
        message = "";
        if (Volatile.Read(ref state) != 3 || worker is not { IsCompletedSuccessfully: true }) return false;
        message = worker.Result; worker = null;
        Interlocked.CompareExchange(ref state, 0, 3);
        return true;
    }
    public void Dispose()
    {
        Interlocked.Exchange(ref state, 4);
        cancellation.Cancel();
        // No join on the game thread. The worker owns immutable data only;
        // its completion cannot log, mutate geometry or call an unloaded plugin.
    }
}
