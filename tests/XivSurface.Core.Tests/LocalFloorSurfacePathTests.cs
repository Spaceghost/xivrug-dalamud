using System.Numerics;
using XivSurface.Core;

namespace XivSurface.Core.Tests;

public sealed class LocalFloorSurfacePathTests
{
    [Theory]
    [InlineData(0f)] [InlineData(.3f)]
    public void NewlyObservedAdjacentPlayerFacePreservesTheSameFreshLayer(float rise)
    {
        var previous = Triangle(new(-2, 0, -1), new(0, 0, -1), new(0, 0, 1));
        var next = Triangle(new(0, 0, -1), new(2, rise, 0), new(0, 0, 1));
        var layer = new LocalFloorLayer();
        Assert.True(layer.Seed(new(new(-.2f, 0, 0), previous)));
        Assert.True(layer.BeginFrame(new(new(.2f, rise * .1f, 0), next), .1));
        Assert.Equal(2, layer.Count);
        Assert.True(layer.Contains(previous));
    }

    [Fact]
    public void AdjacentRootWithoutALocalPortalApproachStillInvalidates()
    {
        var previous = Triangle(new(0, 0, 0), new(2, 0, 0), new(0, 0, 2));
        var next = Triangle(new(0, 0, 0), new(3, 0, -.2f), new(2, 0, 0));
        var layer = new LocalFloorLayer();
        Assert.True(layer.Seed(new(new(.1f, 0, 1.8f), previous)));
        Assert.True(LocalFloorLayer.SharesBoundary(previous, next));
        Assert.False(layer.BeginFrame(new(new(2.9f, 0, -.19f), next), .1));
    }

    [Fact]
    public void ExpiredEvidenceStillInvalidatesWhileThePlayerEntersAnAdjacentFace()
    {
        var faces = Strip(-1, 1, _ => 0);
        var layer = new LocalFloorLayer();
        Assert.True(layer.Seed(new(new(-.5f, 0, -.5f), faces[0])));
        Assert.False(layer.BeginFrame(new(new(.5f, 0, .5f), faces[1]), 3));
        Assert.False(layer.Contains(faces[0]));
    }

    [Fact]
    public void ConnectedRidgeProducesSurfaceSegmentsInsteadOfTheBuriedEndpointChord()
    {
        var faces = Strip(-1.2f, 0, x => .6f + .5f * x)
            .Concat(Strip(0, 1.2f, x => .6f - .5f * x)).ToArray();
        var layer = Load(faces);
        var from = new Vector3(-.4f, .4f, 0); var to = new Vector3(.8f, .2f, 0);
        Assert.True(FirstIntersection(from + Vector3.UnitY * .18f, to + Vector3.UnitY * .18f, faces, out var hit));
        Assert.Equal(-.13f, hit.X, 4); Assert.Equal(.535f, hit.Y, 4);
        Assert.Equal(LayerQueryResult.Success, layer.TrySurfacePath(from, to, out var path));
        Assert.Equal(from, path[0]); Assert.Equal(to, path[^1]);
        Assert.Contains(path, p => Math.Abs(p.X) < .0001f && Math.Abs(p.Y - .6f) < .0001f);
        AssertOnMeasuredFaces(path, faces);
        for (var i = 1; i < path.Length; i++)
            Assert.False(FirstIntersection(path[i - 1] + Vector3.UnitY * .18f,
                path[i] + Vector3.UnitY * .18f, faces, out _));
    }

    [Fact]
    public void ConvexRockFanWorksFromPeakAndFromAnOlderCompressionOrigin()
    {
        var faces = Rock(); var layer = Load(faces);
        foreach (var from in new[] { new Vector3(0, 1, 0), new Vector3(-1.5f, .45f, 0) })
        {
            var to = new Vector3(2, 0, 0);
            Assert.Equal(LayerQueryResult.Success, layer.TrySurfacePath(from, to, out var path));
            Assert.Equal(from, path[0]); Assert.Equal(to, path[^1]);
            AssertOnMeasuredFaces(path, faces);
        }
    }

    [Fact]
    public void WorldCoordinateRockKeepsPortalCrossingsOnMeasuredFaces()
    {
        var offset = new Vector3(-255.5487f, 16, 45.755f);
        var faces = Rock().Select(face => new LayerTriangle(face.A + offset,
            face.B + offset, face.C + offset, face.Normal)).ToArray();
        var layer = Load(faces);
        var from = new Vector3(-1.5f, .45f, .25f) + offset;
        var to = new Vector3(2, 0, .5f) + offset;
        Assert.Equal(LayerQueryResult.Success, layer.TrySurfacePath(from, to, out var path));
        Assert.Equal(from, path[0]); Assert.Equal(to, path[^1]);
        AssertOnMeasuredFaces(path, faces);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void VerifiedCurbEmitsBothMeasuredHeightsInEitherDirection(bool reverse)
    {
        var lower = Triangle(new(-2, 0, -1), new(0, 0, -1), new(0, 0, 1));
        var upper = Triangle(new(0, .2f, -1), new(2, .2f, 1), new(0, .2f, 1));
        var risers = new[] {
            Triangle(new(0, 0, -1), new(0, 0, 1), new(0, .2f, 1)),
            Triangle(new(0, 0, -1), new(0, .2f, 1), new(0, .2f, -1)) };
        var from = new Vector3(-.2f, 0, 0); var to = new Vector3(.2f, .2f, 0);
        var layer = new LocalFloorLayer(); Assert.True(layer.Seed(new(from, lower)));
        Assert.True(layer.TryProbe(new(to.X, to.Z), out var probe));
        Assert.True(layer.Accept(probe, new(to, upper), risers));
        if (reverse) (from, to) = (to, from);
        Assert.Equal(LayerQueryResult.Success, layer.TrySurfacePath(from, to, out var path));
        Assert.Equal(from, path[0]); Assert.Equal(to, path[^1]);
        Assert.Contains(new Vector3(0, 0, 0), path);
        Assert.Contains(new Vector3(0, .2f, 0), path);
        Assert.Equal(4, path.Length);
    }

    [Fact]
    public void EndpointMustBelongToTheMeasuredHeightRatherThanTheSameXZ()
    {
        var faces = Strip(-2, 2, _ => 0); var layer = Load(faces);
        Assert.Equal(LayerQueryResult.Unknown, layer.TrySurfacePath(new(-1, .2f, 0), new(1, 0, 0), out var path));
        Assert.Empty(path);
        Assert.Equal(LayerQueryResult.Unknown, layer.TrySurfacePath(new(-1, 0, 0), new(1, .2f, 0), out path));
        Assert.Empty(path);
    }

    [Fact]
    public void KnownOppositeCornerFaceCanConfirmTheSameMeasuredEndpointWithoutAddingAnEdge()
    {
        var origin = Vector3.Zero;
        var corners = new[] { new Vector3(1, 0, 0), new Vector3(0, 0, 1), new Vector3(-1, 0, 0), new Vector3(0, 0, -1) };
        var faces = Enumerable.Range(0, 4).Select(i => Triangle(origin, corners[i], corners[(i + 1) % 4])).ToArray();
        var layer = Load(faces);
        // The current root is the last quadrant. A ray at the common vertex
        // can return its opposite quadrant, reached by two real edges.
        Assert.False(LocalFloorLayer.SharesBoundary(faces[3], faces[1]));
        Assert.True(layer.TryProbe(Vector2.Zero, out var probe));
        Assert.True(layer.Accept(probe, new(origin, faces[1])));
        Assert.Equal(4, layer.Count);
        var unknown = Triangle(origin, new(2, 0, 1), new(1, 0, 2));
        Assert.False(layer.Accept(probe, new(origin, unknown)));
        Assert.Equal(4, layer.Count);
    }

    [Fact]
    public void CellCeilingIncludesMeasuredPeakBetweenCornerAndCenterSamples()
    {
        var shift = new Vector3(.17f, 0, .11f);
        var faces = Rock().Select(face => face with { A = face.A + shift, B = face.B + shift, C = face.C + shift }).ToArray();
        var layer = Load(faces);
        var center = At(Vector2.Zero);
        layer.BeginFrame(center, 0);
        var a = At(new(-.5f, -.5f)).Position; var b = At(new(.5f, -.5f)).Position;
        var c = At(new(-.5f, .5f)).Position; var d = At(new(.5f, .5f)).Position;
        Assert.True(new[] { center.Position.Y, a.Y, b.Y, c.Y, d.Y }.Max() < .999f);
        Assert.Equal(LayerQueryResult.Success, layer.TryCellCeiling(center, a, b, c, d, out var ceiling));
        Assert.Equal(1, ceiling, 5);
        Assert.Equal(LayerQueryResult.Pending, layer.TryCellCeiling(center, a, b, c, d, out _, () => false));

        LayerFloorHit At(Vector2 xz)
        {
            foreach (var face in faces)
                if (face.TryHeight(xz, out var height) && face.Contains(new(xz.X, height, xz.Y)))
                    return new(new(xz.X, height, xz.Y), face);
            throw new InvalidOperationException("Fixture is missing a real floor.");
        }
    }

    [Fact]
    public void CellCeilingCannotImportAnUpperDeckConnectedOutsideTheCell()
    {
        var faces = Strip(-2, 2, _ => 0)
            .Concat(Strip(2, 4, x => (x - 2) * .1f))
            .Concat(Strip(-2, 4, _ => .2f)).ToArray();
        var layer = Load(faces);
        var center = new LayerFloorHit(Vector3.Zero, faces[0]);
        layer.BeginFrame(center, 0);
        Assert.Equal(LayerQueryResult.Success, layer.TryCellCeiling(center,
            new(-.5f, 0, -.5f), new(.5f, 0, -.5f), new(-.5f, 0, .5f), new(.5f, 0, .5f), out var ceiling));
        Assert.Equal(0, ceiling);
    }

    [Fact]
    public void ConnectionByARemoteRampCannotSwitchToAnOverlappingDeck()
    {
        var faces = Strip(-2, 2, _ => 0)
            .Concat(Strip(2, 4, x => (x - 2) * .1f))
            .Concat(Strip(-2, 4, _ => .2f)).ToArray();
        var layer = Load(faces);
        Assert.Equal(LayerQueryResult.Success, layer.TrySurfacePath(new(-1, 0, 0), new(1, 0, 0), out var below));
        Assert.All(below, p => Assert.Equal(0, p.Y));
        Assert.Equal(LayerQueryResult.Success, layer.TrySurfacePath(new(-1, .2f, 0), new(1, .2f, 0), out var above));
        Assert.All(above, p => Assert.Equal(.2f, p.Y));
        Assert.Equal(LayerQueryResult.Unknown, layer.TrySurfacePath(new(-1, 0, 0), new(1, .2f, 0), out var wrong));
        Assert.Empty(wrong);
    }

    [Fact]
    public void ALocallyForkedLayerDoesNotChooseOneOverlappingHeight()
    {
        var baseFace = Triangle(new(-2, 0, -1), new(0, 0, -1), new(0, 0, 1));
        var flat = Triangle(new(0, 0, -1), new(2, 0, 0), new(0, 0, 1));
        var ramp = Triangle(new(0, 0, -1), new(2, .4f, 0), new(0, 0, 1));
        var layer = Load([baseFace, flat, ramp]);
        Assert.Equal(LayerQueryResult.Unknown, layer.TrySurfacePath(new(-.2f, 0, 0), new(.5f, 0, 0), out var path));
        Assert.Empty(path);
    }

    [Fact]
    public void PositiveGapAndPointOnlyContactCannotBecomeASurfacePath()
    {
        var a = Triangle(new(0, 0, 0), new(1, 0, 0), new(0, 0, 1));
        var layer = new LocalFloorLayer(); Assert.True(layer.Seed(new(new(.1f, 0, .1f), a)));
        foreach (var to in new[] { new Vector3(1.02f, 0, .1f), new Vector3(-.1f, 0, -.1f) })
        {
            Assert.Equal(LayerQueryResult.Unknown, layer.TrySurfacePath(new(.1f, 0, .1f), to, out var path));
            Assert.Empty(path);
        }
    }

    [Fact]
    public void MissingCorridorReportsOnlyTheNextActualMeasuredBoundaryForDiscovery()
    {
        var first = Triangle(new(-2, 0, -1), new(0, 0, -1), new(0, 0, 1));
        var next = Triangle(new(0, 0, -1), new(2, .2f, 0), new(0, 0, 1));
        var layer = new LocalFloorLayer();
        var from = new Vector3(-.2f, 0, 0); var to = new Vector3(.2f, .02f, 0);
        Assert.True(layer.Seed(new(from, first)));
        Assert.Equal(LayerQueryResult.Unknown, layer.TrySurfacePath(from, to, out var path));
        Assert.Empty(path);
        Assert.True(layer.SurfacePathMissingWitness is { X: > 0 and < .006f, Y: 0 });
        var witness = layer.SurfacePathMissingWitness!.Value;
        Assert.True(layer.TryProbe(witness, out var probe));
        Assert.True(next.TryHeight(witness, out var y));
        // Only a real collision hit may fill this gap. Supplying its actual
        // triangle establishes the shared portal and the next call succeeds.
        Assert.True(layer.Accept(probe, new(new(witness.X, y, witness.Y), next)));
        Assert.Equal(LayerQueryResult.Success, layer.TrySurfacePath(from, to, out path));
        Assert.Null(layer.SurfacePathMissingWitness);
        Assert.Equal(LayerQueryResult.Unknown, layer.TrySurfacePath(from, to + Vector3.UnitY * .2f, out _));
        Assert.Null(layer.SurfacePathMissingWitness);
        Assert.Equal(LayerQueryResult.Pending, layer.TrySurfacePath(from, to, out _, () => false));
        Assert.Null(layer.SurfacePathMissingWitness);
    }

    [Fact]
    public void CallerDeadlineNeverPublishesAPartialPathAndRetryCanSucceed()
    {
        var faces = Rock(); var layer = Load(faces); var calls = 0;
        Assert.Equal(LayerQueryResult.Pending, layer.TrySurfacePath(new(0, 1, 0), new(2, 0, 0), out var path,
            () => ++calls < 15));
        Assert.Empty(path);
        Assert.Equal(LayerQueryResult.Success, layer.TrySurfacePath(new(0, 1, 0), new(2, 0, 0), out path));
        AssertOnMeasuredFaces(path, faces);
    }

    [Fact]
    public void ExpiredFacesAndMalformedEndpointsAreNotSurfaceEvidence()
    {
        var faces = Strip(-2, 2, _ => 0); var layer = Load(faces);
        var player = new LayerFloorHit(new(-1, 0, -.5f), faces[0]);
        layer.BeginFrame(player, 3);
        Assert.Equal(LayerQueryResult.Unknown, layer.TrySurfacePath(player.Position, new(1, 0, .5f), out var path));
        Assert.Empty(path);
        Assert.Equal(LayerQueryResult.Unknown, layer.TrySurfacePath(new(float.NaN), player.Position, out path));
        Assert.Empty(path);
    }

    [Fact]
    public void SurfaceProofDoesNotAuthorizeIgnoringAWallAboveTheRidge()
    {
        var faces = Rock(); var layer = Load(faces);
        Assert.Equal(LayerQueryResult.Success, layer.TrySurfacePath(new(0, 1, 0), new(2, 0, 0), out var path));
        var wall = new[] {
            Triangle(new(1.5f, .45f, -1), new(1.5f, 1.5f, -1), new(1.5f, .45f, 1)),
            Triangle(new(1.5f, 1.5f, -1), new(1.5f, 1.5f, 1), new(1.5f, .45f, 1)) };
        Assert.Contains(Enumerable.Range(1, path.Length - 1), i => FirstIntersection(
            path[i - 1] + Vector3.UnitY * .18f, path[i] + Vector3.UnitY * .18f, wall, out _));
    }

    private static LayerTriangle Triangle(Vector3 a, Vector3 b, Vector3 c)
    {
        var normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
        if (normal.Y < 0) normal = -normal;
        return new(a, b, c, normal);
    }
    private static LayerTriangle[] Strip(float x0, float x1, Func<float, float> height)
    {
        var a = new Vector3(x0, height(x0), -1); var b = new Vector3(x1, height(x1), -1);
        var c = new Vector3(x0, height(x0), 1); var d = new Vector3(x1, height(x1), 1);
        return [Triangle(a, b, c), Triangle(b, d, c)];
    }
    private static LayerTriangle[] Rock()
    {
        var inner = new[] { new Vector3(-1, .9f, -1), new Vector3(1, .9f, -1), new Vector3(1, .9f, 1), new Vector3(-1, .9f, 1) };
        var outer = inner.Select(p => new Vector3(p.X * 2, 0, p.Z * 2)).ToArray();
        var faces = new List<LayerTriangle>();
        for (var i = 0; i < 4; i++)
        {
            var next = (i + 1) % 4;
            faces.Add(Triangle(new(0, 1, 0), inner[i], inner[next]));
            faces.Add(Triangle(inner[i], outer[i], outer[next]));
            faces.Add(Triangle(inner[i], outer[next], inner[next]));
        }
        return faces.ToArray();
    }
    private static LocalFloorLayer Load(LayerTriangle[] faces)
    {
        var layer = new LocalFloorLayer(); var pending = faces.ToList();
        var first = pending[0]; pending.RemoveAt(0);
        Assert.True(layer.Seed(new((first.A + first.B + first.C) / 3, first)));
        var loaded = new List<LayerTriangle> { first };
        while (pending.Count > 0)
        {
            var index = pending.FindIndex(candidate => loaded.Any(face => LocalFloorLayer.SharesBoundary(face, candidate)));
            Assert.True(index >= 0, "Fixture must contain actual shared triangle edges.");
            var next = pending[index]; pending.RemoveAt(index);
            layer.BeginFrame(new((next.A + next.B + next.C) / 3, next), 0);
            loaded.Add(next); Assert.Equal(loaded.Count, layer.Count);
        }
        return layer;
    }
    private static void AssertOnMeasuredFaces(Vector3[] path, LayerTriangle[] faces)
    {
        Assert.InRange(path.Length, 2, LocalFloorLayer.MaximumSurfacePathPoints);
        for (var segment = 1; segment < path.Length; segment++)
        for (var sample = 0; sample <= 20; sample++)
        {
            var at = Vector3.Lerp(path[segment - 1], path[segment], sample / 20f);
            Assert.Contains(faces, face => face.Contains(at));
        }
    }
    private static bool FirstIntersection(Vector3 from, Vector3 to, IEnumerable<LayerTriangle> faces, out Vector3 hit)
    {
        hit = default; var earliest = float.PositiveInfinity; var direction = to - from;
        foreach (var face in faces)
        {
            var ab = face.B - face.A; var ac = face.C - face.A;
            var p = Vector3.Cross(direction, ac); var determinant = Vector3.Dot(ab, p);
            if (Math.Abs(determinant) < 1e-7f) continue;
            var inverse = 1 / determinant; var delta = from - face.A;
            var u = Vector3.Dot(delta, p) * inverse; if (u < 0 || u > 1) continue;
            var q = Vector3.Cross(delta, ab); var v = Vector3.Dot(direction, q) * inverse;
            if (v < 0 || u + v > 1) continue;
            var t = Vector3.Dot(ac, q) * inverse;
            if (t < 0 || t > 1 || t >= earliest) continue;
            earliest = t; hit = from + direction * t;
        }
        return float.IsFinite(earliest);
    }
}
