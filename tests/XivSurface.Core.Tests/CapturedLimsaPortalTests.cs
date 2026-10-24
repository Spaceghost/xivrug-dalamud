using System.Collections.Immutable;
using System.Numerics;
using System.Text;
using XivSurface.Core;

namespace XivSurface.Core.Tests;

public sealed class CapturedLimsaPortalTests
{
    private static ClothPathReplay Read() => ClothCollisionReportFile.Decode(Encoding.UTF8.GetBytes(Capture)).PathUnknown!;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CapturedPlanterCorridorProducesOrderedMeasuredPoints(bool reverse)
    {
        var query = Read();
        Assert.Equal("portal point does not match its floor", query.Reason);
        Assert.Equal(7, query.Layer.Faces.Length);
        if (reverse) query = query with { From = query.To, To = query.From };
        var result = LocalFloorLayer.Replay(query);
        Assert.Equal(LayerQueryResult.Success, result.Result);
        Assert.Equal(query.From, result.Path[0]); Assert.Equal(query.To, result.Path[^1]);
        var dx = (double)query.To.X - query.From.X; var dz = (double)query.To.Z - query.From.Z;
        var previous = -1d;
        foreach (var point in result.Path)
        {
            Assert.Contains(query.Layer.Faces, face => face.Triangle.Contains(point));
            var t = (((double)point.X-query.From.X)*dx + ((double)point.Z-query.From.Z)*dz)/(dx*dx+dz*dz);
            Assert.True(t >= previous); previous = t;
        }
    }

    [Fact]
    public void UnequalAxisSpacingFindsAnAcceptedPointInsideTheOriginalCorrectionRadius()
    {
        var query = Read();
        var parent = query.Layer.Faces.Single(f => Math.Abs(f.Triangle.A.X + 46.307465f) < .000001f
            && Math.Abs(f.Triangle.C.X + 43.90484f) < .000001f).Triangle;
        var child = query.Layer.Faces.Single(f => Math.Abs(f.Triangle.A.X + 45.00255f) < .000001f
            && Math.Abs(f.Triangle.B.X + 43.848717f) < .000001f).Triangle;
        var rounded = new Vector2(-46.035987854003906f, -117.24408721923828f);
        Assert.True(parent.TryHeight(rounded, out var y));
        Assert.False(parent.Contains(new(rounded.X, y, rounded.Y)));
        var result = LocalFloorLayer.Replay(query);
        Assert.Equal(LayerQueryResult.Success, result.Result);
        var gx = (double)MathF.BitIncrement(rounded.X) - rounded.X;
        var gz = (double)MathF.BitIncrement(rounded.Y) - rounded.Y;
        Assert.Contains(result.Path, p =>
        {
            var dx = (double)p.X-rounded.X; var dz = (double)p.Z-rounded.Y;
            var xz = new Vector2(p.X,p.Z);
            return Math.Abs(dx) > gx && dx*dx+dz*dz <= gx*gx+gz*gz
                && parent.TryHeight(xz,out var py) && parent.Contains(new(p.X,py,p.Z))
                && child.TryHeight(xz,out var cy) && child.Contains(new(p.X,cy,p.Z));
        });
    }

    [Fact]
    public void CapturedPlanterStillRejectsWrongLayersExpiredEvidenceAndDeadlines()
    {
        var query = Read();
        Assert.Equal(LayerQueryResult.Unknown, LocalFloorLayer.Replay(query with { To = query.To + Vector3.UnitY * .1f }).Result);
        Assert.Equal(LayerQueryResult.Unknown, LocalFloorLayer.Replay(query with { Layer = query.Layer with { Now = query.Layer.Now + 2.01 } }).Result);
        Assert.Equal(LayerQueryResult.Pending, LocalFloorLayer.Replay(query, 16).Result);
    }

    [Fact]
    public void ExpandedFloatSpacingStillCannotExceedWorldSpaceCorrectionCap()
    {
        var query = Read(); const float scale = 1024;
        LayerTriangle Scale(LayerTriangle t) => t with { A=t.A*scale, B=t.B*scale, C=t.C*scale };
        var state = query.Layer with { Root=Scale(query.Layer.Root), RootPoint=query.Layer.RootPoint*scale,
            Faces=query.Layer.Faces.Select(f=>f with { Triangle=Scale(f.Triangle) }).ToImmutableArray(),
            Portals=query.Layer.Portals.Select(p=>p with { ExitA=p.ExitA*scale, ExitB=p.ExitB*scale,
                EntryA=p.EntryA*scale, EntryB=p.EntryB*scale }).ToImmutableArray() };
        var result = LocalFloorLayer.Replay(query with { Layer=state, From=query.From*scale, To=query.To*scale });
        Assert.Equal(LayerQueryResult.Unknown, result.Result); Assert.Empty(result.Path);
    }

    // Exact native collision geometry, without player/account identifiers or raw query timing.
    private const string Capture = """
{"Version":2,"Zone":128,"RequestedAt":3.9577801,"CompletedAt":4.4503437,"Completion":"both failures captured","PathUnknown":{"Layer":{"Version":2,"Now":4.4458627,"Root":{"A":{"X":-51.638954,"Y":42,"Z":-124.743866},"B":{"X":-46.665577,"Y":42,"Z":-117.029526},"C":{"X":-48.352978,"Y":42,"Z":-125.31035},"Normal":{"X":0,"Y":1,"Z":0}},"RootPoint":{"X":-47.24528,"Y":42,"Z":-118.725464},"Faces":[{"Triangle":{"A":{"X":-51.638954,"Y":42,"Z":-124.743866},"B":{"X":-46.665577,"Y":42,"Z":-117.029526},"C":{"X":-48.352978,"Y":42,"Z":-125.31035},"Normal":{"X":0,"Y":1,"Z":0}},"ObservedAt":4.4458627,"WalkableGraph":true,"Role":0},{"Triangle":{"A":{"X":-50.712944,"Y":42,"Z":-116.34121},"B":{"X":-46.665577,"Y":42,"Z":-117.029526},"C":{"X":-51.638954,"Y":42,"Z":-124.743866},"Normal":{"X":0,"Y":1,"Z":0}},"ObservedAt":4.4458627,"WalkableGraph":true,"Role":0},{"Triangle":{"A":{"X":-46.665577,"Y":42,"Z":-117.029526},"B":{"X":-46.307465,"Y":41.99811,"Z":-117.16119},"C":{"X":-48.352978,"Y":42,"Z":-125.31035},"Normal":{"X":0.0049053,"Y":0.9999874,"Z":-0.0009995634}},"ObservedAt":4.3661961,"WalkableGraph":true,"Role":0},{"Triangle":{"A":{"X":-48.352978,"Y":42,"Z":-125.31035},"B":{"X":-46.307465,"Y":41.99811,"Z":-117.16119},"C":{"X":-43.90484,"Y":41.998035,"Z":-118.04421},"Normal":{"X":0.00010704045,"Y":1,"Z":0.00020484587}},"ObservedAt":4.4458627,"WalkableGraph":true,"Role":0},{"Triangle":{"A":{"X":-46.307465,"Y":41.99811,"Z":-117.16119},"B":{"X":-43.848717,"Y":41.99669,"Z":-117.91204},"C":{"X":-43.90484,"Y":41.998035,"Z":-118.04421},"Normal":{"X":0.0032662798,"Y":0.9999559,"Z":0.008800825}},"ObservedAt":4.4458627,"WalkableGraph":true,"Role":0},{"Triangle":{"A":{"X":-45.00255,"Y":41.99647,"Z":-117.47026},"B":{"X":-43.848717,"Y":41.99669,"Z":-117.91204},"C":{"X":-46.307465,"Y":41.99811,"Z":-117.16119},"Normal":{"X":0.0036013762,"Y":0.99994457,"Z":0.009898153}},"ObservedAt":4.4458627,"WalkableGraph":true,"Role":0},{"Triangle":{"A":{"X":-45.00255,"Y":41.99647,"Z":-117.47026},"B":{"X":-46.307465,"Y":41.99811,"Z":-117.16119},"C":{"X":-46.236225,"Y":41.99597,"Z":-116.99798},"Normal":{"X":0.003953677,"Y":0.99992734,"Z":0.011385663}},"ObservedAt":4.4458627,"WalkableGraph":true,"Role":0}],"Portals":[{"From":0,"To":1,"Risers":[],"ExitA":{"X":-51.638954,"Y":42,"Z":-124.743866},"ExitB":{"X":-46.665577,"Y":42,"Z":-117.029526},"EntryA":{"X":-51.638954,"Y":42,"Z":-124.743866},"EntryB":{"X":-46.665577,"Y":42,"Z":-117.029526}},{"From":0,"To":2,"Risers":[],"ExitA":{"X":-46.665577,"Y":42,"Z":-117.029526},"ExitB":{"X":-48.352978,"Y":42,"Z":-125.31035},"EntryA":{"X":-46.665577,"Y":42,"Z":-117.029526},"EntryB":{"X":-48.352978,"Y":42,"Z":-125.31035}},{"From":1,"To":0,"Risers":[],"ExitA":{"X":-51.638954,"Y":42,"Z":-124.743866},"ExitB":{"X":-46.665577,"Y":42,"Z":-117.029526},"EntryA":{"X":-51.638954,"Y":42,"Z":-124.743866},"EntryB":{"X":-46.665577,"Y":42,"Z":-117.029526}},{"From":2,"To":0,"Risers":[],"ExitA":{"X":-46.665577,"Y":42,"Z":-117.029526},"ExitB":{"X":-48.352978,"Y":42,"Z":-125.31035},"EntryA":{"X":-46.665577,"Y":42,"Z":-117.029526},"EntryB":{"X":-48.352978,"Y":42,"Z":-125.31035}},{"From":2,"To":3,"Risers":[],"ExitA":{"X":-46.307465,"Y":41.99811,"Z":-117.16119},"ExitB":{"X":-48.352978,"Y":42,"Z":-125.31035},"EntryA":{"X":-46.307465,"Y":41.99811,"Z":-117.16119},"EntryB":{"X":-48.352978,"Y":42,"Z":-125.31035}},{"From":3,"To":2,"Risers":[],"ExitA":{"X":-46.307465,"Y":41.99811,"Z":-117.16119},"ExitB":{"X":-48.352978,"Y":42,"Z":-125.31035},"EntryA":{"X":-46.307465,"Y":41.99811,"Z":-117.16119},"EntryB":{"X":-48.352978,"Y":42,"Z":-125.31035}},{"From":3,"To":4,"Risers":[],"ExitA":{"X":-46.307465,"Y":41.99811,"Z":-117.16119},"ExitB":{"X":-43.90484,"Y":41.998035,"Z":-118.04421},"EntryA":{"X":-46.307465,"Y":41.99811,"Z":-117.16119},"EntryB":{"X":-43.90484,"Y":41.998035,"Z":-118.04421}},{"From":4,"To":3,"Risers":[],"ExitA":{"X":-46.307465,"Y":41.99811,"Z":-117.16119},"ExitB":{"X":-43.90484,"Y":41.998035,"Z":-118.04421},"EntryA":{"X":-46.307465,"Y":41.99811,"Z":-117.16119},"EntryB":{"X":-43.90484,"Y":41.998035,"Z":-118.04421}},{"From":4,"To":5,"Risers":[],"ExitA":{"X":-46.307465,"Y":41.99811,"Z":-117.16119},"ExitB":{"X":-43.848717,"Y":41.99669,"Z":-117.91204},"EntryA":{"X":-46.307465,"Y":41.99811,"Z":-117.16119},"EntryB":{"X":-43.848717,"Y":41.99669,"Z":-117.91204}},{"From":5,"To":4,"Risers":[],"ExitA":{"X":-46.307465,"Y":41.99811,"Z":-117.16119},"ExitB":{"X":-43.848717,"Y":41.99669,"Z":-117.91204},"EntryA":{"X":-46.307465,"Y":41.99811,"Z":-117.16119},"EntryB":{"X":-43.848717,"Y":41.99669,"Z":-117.91204}},{"From":5,"To":6,"Risers":[],"ExitA":{"X":-46.307465,"Y":41.99811,"Z":-117.16119},"ExitB":{"X":-45.00255,"Y":41.99647,"Z":-117.47026},"EntryA":{"X":-46.307465,"Y":41.99811,"Z":-117.16119},"EntryB":{"X":-45.00255,"Y":41.99647,"Z":-117.47026}},{"From":6,"To":5,"Risers":[],"ExitA":{"X":-46.307465,"Y":41.99811,"Z":-117.16119},"ExitB":{"X":-45.00255,"Y":41.99647,"Z":-117.47026},"EntryA":{"X":-46.307465,"Y":41.99811,"Z":-117.16119},"EntryB":{"X":-45.00255,"Y":41.99647,"Z":-117.47026}}],"Scope":1},"From":{"X":-47.24528,"Y":42,"Z":-118.725464},"To":{"X":-46,"Y":41.997337,"Z":-117.200005},"Result":0,"Reason":"portal point does not match its floor","MissingWitness":null,"SupportEpoch":3,"DiscoveryTarget":null,"DiscoveryResult":null,"AttemptResult":0,"SnapshotCopyMilliseconds":0.0319},"CellPending":null,"RawFloorRays":[]}
""";
}
