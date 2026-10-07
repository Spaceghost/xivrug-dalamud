using System.Numerics;
using XivSurface.Core;

namespace XivSurface.Core.Tests;

public class TriangleFloorMaskTests
{
    private static FloorTriangle Triangle(float y = 0) => new(new(0, y, 0), new(2, y, 0), new(0, y, 2));

    [Fact]
    public void InteriorAndBoundaryMatchButHoleDoesNot()
    {
        var mask = new TriangleFloorMask([Triangle()], 134, 3, 7);
        Assert.True(mask.TryMatch(new(0.5f, 0, 0.5f), out var match));
        Assert.Equal(134u, match.TerritoryId);
        Assert.Equal(3, match.GeometryEpoch);
        Assert.Equal(7, match.LayerId);
        Assert.True(mask.TryMatch(new(1, 0, 1), out _));
        Assert.False(mask.TryMatch(new(1.01f, 0, 1.01f), out _));
        Assert.False(mask.TryMatch(new(-0.001f, 0, 0), out _));
    }

    [Fact]
    public void SlopesPreserveInterpolatedHeightInEitherWinding()
    {
        var t = new FloorTriangle(new(0, 0, 0), new(2, 2, 0), new(0, 0, 2));
        foreach (var triangle in new[] { t, new FloorTriangle(t.C, t.B, t.A) })
        {
            var mask = new TriangleFloorMask([triangle], 1, 0, 0);
            Assert.True(mask.TryMatch(new(0.5f, 0.6f, 0.5f), out var match));
            Assert.Equal(0.5f, match.Position.Y, 5);
            Assert.False(mask.TryMatch(new(0.5f, 1, 0.5f), out _));
        }
    }

    [Fact]
    public void StackedTrianglesNeverInterpolateTheSpaceBetweenFloors()
    {
        var mask = new TriangleFloorMask([Triangle(), Triangle(4)], 1, 0, 0);
        Assert.True(mask.TryMatch(new(0.5f, 4, 0.5f), out var upper));
        Assert.Equal(4, upper.Position.Y);
        Assert.True(mask.TryMatch(new(0.5f, 0, 0.5f), out var lower));
        Assert.Equal(0, lower.Position.Y);
        Assert.False(mask.TryMatch(new(0.5f, 2, 0.5f), out _));
    }

    [Fact]
    public void CopiesInputAndRejectsInvalidGeometry()
    {
        var triangles = new[] { Triangle() };
        var mask = new TriangleFloorMask(triangles, 1, 0, 0);
        triangles[0] = Triangle(4);
        Assert.True(mask.TryMatch(new(0.5f, 0, 0.5f), out _));
        Assert.False(mask.TryMatch(new(float.NaN, 0, 0), out _));
        Assert.Throws<ArgumentException>(() => new TriangleFloorMask([new(default, default, default)], 1, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TriangleFloorMask([], 1, 0, 0, 1));
    }
}
