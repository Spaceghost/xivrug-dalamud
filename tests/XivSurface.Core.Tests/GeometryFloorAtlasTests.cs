using System.Numerics;
using XivSurface.Core;

namespace XivSurface.Core.Tests;

public class GeometryFloorAtlasTests
{
    private static FloorTriangle Flat(float y = 0) => new(new(-4, y, -4), new(4, y, -4), new(-4, y, 4));
    private static GeometryFloorAtlas Build(params FloorTriangle[] triangles)
    {
        Assert.True(GeometryFloorAtlas.TryBuild(triangles, Vector2.Zero, new(4), out var atlas));
        return atlas!;
    }

    [Fact]
    public void HitValidationChecksBothPlaneAndActualTriangleCoverage()
    {
        Assert.True(GeometryFloorAtlas.TryValidateHit(Flat(3), new(-1, 3, -1)));
        Assert.False(GeometryFloorAtlas.TryValidateHit(Flat(3), new(-1, 3.1f, -1)));
        Assert.False(GeometryFloorAtlas.TryValidateHit(Flat(3), new(3, 3, 3)));
        Assert.False(GeometryFloorAtlas.TryValidateHit(Flat(3), new(float.NaN)));
        var localTriangle = new FloorTriangle(new(0, 0, 0), new(2, 0, 0), new(0, 0, 2));
        Assert.False(GeometryFloorAtlas.TryValidateHit(localTriangle, new(500, 0, 500)));
    }

    [Fact]
    public void SlopingTriangleUsesItsExactPlaneRegardlessOfBinResolution()
    {
        var triangle = new FloorTriangle(new(-4, 0, -4), new(4, 4, -4), new(-4, 8, 4));
        Assert.True(GeometryFloorAtlas.TryValidateHit(triangle, new(-1, 4.5f, -1)));
        var atlas = Build(triangle);
        Assert.True(atlas.TryMatch(new(-1, 4.5f, -1), out var y));
        Assert.Equal(4.5f, y);
        Assert.False(atlas.TryMatch(new(-1, 5, -1), out _));
    }

    [Fact]
    public void ReversedWindingStillMatches()
    {
        var t = Flat(2);
        var atlas = Build(new FloorTriangle(t.A, t.C, t.B));
        Assert.True(atlas.TryMatch(new(-1, 2, -1), out var y));
        Assert.Equal(2, y);
    }

    [Fact]
    public void NoPlaneExtrapolationAcrossTriangleEdgeOrDisconnectedGap()
    {
        var left = new FloorTriangle(new(-4, 0, -4), new(-1, 0, -4), new(-1, 0, 4));
        var right = new FloorTriangle(new(1, 0, -4), new(4, 0, -4), new(1, 0, 4));
        var atlas = Build(left, right);
        Assert.True(atlas.TryMatch(new(-2, 0, -3), out _));
        Assert.True(atlas.TryMatch(new(2, 0, -3), out _));
        Assert.False(atlas.TryMatch(Vector3.Zero, out _));
        Assert.False(atlas.TryMatch(new(-3, 0, 3), out _));
    }

    [Fact]
    public void FoldedSurfaceDoesNotCreateBilinearMissingStrip()
    {
        // Two planes meeting along a diagonal: a bilinear four-corner height
        // field invents an intermediate plane here and misses the actual crease.
        var a = new FloorTriangle(new(-4, 0, -4), new(4, 0, -4), new(-4, 0, 4));
        var b = new FloorTriangle(new(4, 0, -4), new(4, 4, 4), new(-4, 0, 4));
        var atlas = Build(a, b);
        Assert.True(atlas.TryMatch(new(0, 0, 0), out var crease));
        Assert.Equal(0, crease);
        Assert.True(atlas.TryMatch(new(1, 1, 1), out var slope));
        Assert.Equal(1, slope);
    }

    [Fact]
    public void MultipleValidatedLayersMatchOnlyTheirOwnHeight()
    {
        var atlas = Build(Flat(0), Flat(5));
        Assert.True(atlas.TryMatch(new(-1, 0, -1), out var lower));
        Assert.Equal(0, lower);
        Assert.True(atlas.TryMatch(new(-1, 5, -1), out var upper));
        Assert.Equal(5, upper);
        Assert.False(atlas.TryMatch(new(-1, 2.5f, -1), out _));
    }

    [Fact]
    public void OverflowInvalidatesAllSlotsAndCannotRevealAnotherLayer()
    {
        var triangles = Enumerable.Repeat(Flat(0), GeometryFloorAtlas.SlotsPerBin).Append(Flat(5)).ToArray();
        var atlas = Build(triangles);
        Assert.True(atlas.OverflowedBins > 0);
        Assert.False(atlas.TryMatch(new(-1, 0, -1), out _));
        Assert.False(atlas.TryMatch(new(-1, 5, -1), out _));
        var bin = 12 * GeometryFloorAtlas.BinSize + 12;
        Assert.All(atlas.IndexTexels.Skip(bin * GeometryFloorAtlas.SlotsPerBin).Take(GeometryFloorAtlas.SlotsPerBin),
            value => Assert.Equal(-1, value));
    }

    [Fact]
    public void FullBinWithoutOverflowStillWorks()
    {
        var atlas = Build(Enumerable.Repeat(Flat(), GeometryFloorAtlas.SlotsPerBin).ToArray());
        Assert.Equal(0, atlas.OverflowedBins);
        Assert.True(atlas.TryMatch(new(-1, 0, -1), out _));
    }

    [Fact]
    public void ExactTriangleBinIntersectionAvoidsBoundingBoxOverflow()
    {
        var irrelevant = new FloorTriangle(new(-4, 0, -4), new(4, 0, -4), new(-4, 0, 4));
        var relevant = new FloorTriangle(new(1, 0, 1), new(4, 0, 1), new(1, 0, 4));
        var atlas = Build(Enumerable.Repeat(irrelevant, 17).Append(relevant).ToArray());
        Assert.True(atlas.OverflowedBins > 0);
        Assert.True(atlas.TryMatch(new(2, 0, 2), out _));
    }

    [Fact]
    public void TriangleAndFootprintBoundarySamplesAreConservativelyIncluded()
    {
        var atlas = Build(Flat());
        Assert.True(atlas.TryMatch(new(-4, 0, -4), out _));
        Assert.True(atlas.TryMatch(new(4, 0, -4), out _));
        Assert.True(atlas.TryMatch(new(-4, 0, 4), out _));
        Assert.True(atlas.TryMatch(Vector3.Zero, out _));
        Assert.False(atlas.TryMatch(new(4.01f, 0, -4), out _));
    }

    [Fact]
    public void PackedTexturesUseDocumentedDimensionsAndIdentifiers()
    {
        var t = Flat(3);
        var atlas = Build(t);
        Assert.Equal(1, atlas.TriangleCount);
        Assert.Equal(GeometryFloorAtlas.TriangleTextureWidth * GeometryFloorAtlas.TriangleTextureHeight * 4, atlas.TriangleTexels.Length);
        Assert.Equal(GeometryFloorAtlas.IndexTextureWidth * GeometryFloorAtlas.IndexTextureHeight * 4, atlas.IndexTexels.Length);
        Assert.Equal(new float[] { -4, 3, -4, 0, 4, 3, -4, 0, -4, 3, 4, 0 }, atlas.TriangleTexels.Take(12));
        var indexAtBin12 = (12 * GeometryFloorAtlas.IndexTextureWidth + 12 * 4) * 4;
        Assert.Equal(1, atlas.IndexTexels[indexAtBin12]);
        Assert.All(atlas.IndexTexels.Skip(indexAtBin12 + 1).Take(15), value => Assert.Equal(0, value));
    }

    [Fact]
    public void InvalidOrOversizedTriangleSetsRejectWholeBuild()
    {
        FloorTriangle[] invalid =
        [
            new(new(float.NaN), Vector3.Zero, Vector3.One),
            new(new(float.MaxValue), Vector3.Zero, Vector3.One),
            new(Vector3.Zero, Vector3.Zero, Vector3.One),
            new(new(0, 0, 0), new(0, 3, 0), new(0, 0, 3)),
            new(new(0, 0, 0), new(1, 10, 0), new(0, 0, 1)),
        ];
        foreach (var t in invalid)
        {
            Assert.False(GeometryFloorAtlas.TryBuild([Flat(), t], Vector2.Zero, new(4), out var atlas));
            Assert.Null(atlas);
            Assert.False(GeometryFloorAtlas.TryValidateHit(t, Vector3.Zero));
        }
        Assert.False(GeometryFloorAtlas.TryBuild(Enumerable.Repeat(Flat(), GeometryFloorAtlas.MaxTriangles + 1).ToArray(),
            Vector2.Zero, new(4), out _));
    }

    [Fact]
    public void EmptyAtlasAndOutsideTrianglesDoNotProduceFloor()
    {
        var empty = Build();
        Assert.False(empty.TryMatch(Vector3.Zero, out _));
        Assert.All(empty.IndexTexels, value => Assert.Equal(0, value));
        var t = new FloorTriangle(new(100, 0, 100), new(101, 0, 100), new(100, 0, 101));
        var outside = Build(t);
        Assert.False(outside.TryMatch(Vector3.Zero, out _));
        Assert.All(outside.IndexTexels, value => Assert.Equal(0, value));
    }

    [Fact]
    public void InputMutationCannotChangeBuiltGeometry()
    {
        var triangles = new[] { Flat(1) };
        var atlas = Build(triangles);
        triangles[0] = Flat(10);
        Assert.True(atlas.TryMatch(new(-1, 1, -1), out var y));
        Assert.Equal(1, y);
    }

    [Fact]
    public void TranslatedNonSquareFootprintPreservesWorldGeometry()
    {
        var triangle = new FloorTriangle(new(4996, 2, -4998), new(5000, 2, -4998), new(4996, 2, -4990));
        Assert.True(GeometryFloorAtlas.TryBuild([triangle], new(4998, -4994), new(2, 4), out var atlas));
        Assert.True(atlas!.TryMatch(new(4997, 2, -4997), out var y));
        Assert.Equal(2, y);
    }

    [Fact]
    public void InvalidFootprintsAndMatchTolerancesFailClosed()
    {
        Assert.False(GeometryFloorAtlas.TryBuild(null, Vector2.Zero, new(4), out _));
        Assert.False(GeometryFloorAtlas.TryBuild([], new(float.NaN), new(4), out _));
        Assert.False(GeometryFloorAtlas.TryBuild([], Vector2.Zero, new(0, 4), out _));
        Assert.False(GeometryFloorAtlas.TryBuild([], Vector2.Zero, new(float.MaxValue), out _));
        var atlas = Build(Flat());
        Assert.False(atlas.TryMatch(new(float.NaN), out _));
        Assert.False(atlas.TryMatch(Vector3.Zero, out _, float.NaN));
        Assert.False(atlas.TryMatch(Vector3.Zero, out _, 0.3f));
        Assert.False(atlas.TryMatch(Vector3.Zero, out _, -1));
    }
}
