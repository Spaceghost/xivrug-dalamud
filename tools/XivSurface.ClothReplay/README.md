# Bounded cloth collision replay

The development-only `/rug inspectcloth` command observes existing sampler calls
for at most ten seconds. It issues no extra native queries and changes no saved
settings. It saves the first path `Unknown` and the first non-success pure
cell-ceiling query followed by an overall `Pending` cell attempt independently.
Pure, discovery and final retry results are separate fields; absent
records are explicitly reported, never reconstructed from guesses. At most one
managed snapshot is copied per framework update so that copy cannot induce the
other captured call's deadline failure in that same update.
Snapshot-copy time is recorded: copying can consume the remaining deadline of
the **following** discovery/retry in that update. The recorded pure failure and
geometry precede the copy; follow-up timing must not be mistaken for an
uninstrumented timing measurement.

The framework copies existing triangles, observation times, directed portals,
riser references, root and query parameters into immutable bounded records. One
worker serializes at most 4 MiB into a unique `cloth-replay-*.json` in the plugin
config directory using `CreateNew`. No native pointers, settings, credentials,
or model files are serialized. Worker completion is polled only while loaded;
unload cancels preparation and never waits on disk I/O. A diagnostic already
being written may complete after unload; it cannot call the plugin.

Replay uses the same `LocalFloorLayer` algorithms, without native calls or
runtime surface authorization:

```sh
dotnet run --project tools/XivSurface.ClothReplay -- /path/to/cloth-replay-example.json
dotnet test tests/XivSurface.Core.Tests --filter FullyQualifiedName~ClothCollisionReplayTests
```

Each replay begins with the captured timestamps unchanged. Deterministic check
limits simulate individual deadline slices; they are not native query timing
and do not pretend the present pure algorithms resume across calls. Comparing
these results with unlimited execution separates a deadline from incomplete or
ambiguous geometry. The report contains territory/world geometry and should be
shared intentionally. It is a bounded diagnostic input, not a trusted physics
cache. Original runtime collision, wall, expiry and coverage gates are unchanged.
