using System.Numerics;
using XivSurface.Core;

namespace XivSurface.Core.Tests;

public class FloorSamplePolicyTests
{
    [Fact]
    public void DerivesMissingNormalFromEitherTriangleWinding()
    {
        var a = Vector3.Zero; var b = Vector3.UnitX; var c = Vector3.UnitZ;
        Assert.Equal(Vector3.UnitY, FloorSamplePolicy.CollisionNormal(Vector3.Zero, a, b, c));
        Assert.Equal(Vector3.UnitY, FloorSamplePolicy.CollisionNormal(Vector3.Zero, c, b, a));
        Assert.Equal(Vector3.Zero, FloorSamplePolicy.CollisionNormal(Vector3.Zero, a, a, a));
        Assert.Equal(-Vector3.UnitY, FloorSamplePolicy.CollisionNormal(-Vector3.UnitY, a, b, c));
    }

    [Fact]
    public void RefinesCoarseMeshWithoutChangingWorldFootprint()
    {
        Assert.True(FloorSamplePolicy.TryRefine(Vector2.Zero, 0, new(0.3f, 0.2f, 0),
            new(0, 0.1f, 0), Vector3.UnitY, out var y));
        Assert.Equal(0.1f, y);
    }

    [Fact]
    public void AGapInBothSurfacesNeverBecomesAFloor()
    {
        Assert.False(FloorSamplePolicy.TryRefine(Vector2.Zero, 0, null, Vector3.Zero, Vector3.UnitY, out _));
        Assert.False(FloorSamplePolicy.TryRefine(Vector2.Zero, 0, Vector3.Zero, null, Vector3.UnitY, out _));
        Assert.False(FloorSamplePolicy.TryRefine(Vector2.Zero, 0, new(0.6f, 0, 0), Vector3.Zero, Vector3.UnitY, out _));
    }

    [Theory]
    [InlineData(2, 0, 1, 0)] // raised prop or another floor
    [InlineData(0, 1, 0, 0)] // wall
    [InlineData(0, 0, -1, 0)] // underside
    [InlineData(0, 0, 0, 0)] // invalid normal
    public void RejectsOtherLayersAndNonFloorNormals(float y, float nx, float ny, float nz)
    {
        Assert.False(FloorSamplePolicy.TryRefine(Vector2.Zero, 0, Vector3.Zero, new(0, y, 0), new(nx, ny, nz), out _));
    }

    [Fact]
    public void RejectsNonfiniteAndWrongHorizontalHits()
    {
        Assert.False(FloorSamplePolicy.TryRefine(Vector2.Zero, 0, Vector3.Zero, new(float.NaN, 0, 0), Vector3.UnitY, out _));
        Assert.False(FloorSamplePolicy.TryRefine(Vector2.Zero, 0, Vector3.Zero, new(0.1f, 0, 0), Vector3.UnitY, out _));
    }
}
