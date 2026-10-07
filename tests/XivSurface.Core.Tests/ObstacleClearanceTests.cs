using System.Numerics;
using XivSurface.Core;

namespace XivSurface.Core.Tests;

public class ObstacleClearanceTests
{
    [Fact]
    public void OpenGroundKeepsFullCoverage()
    {
        var values = Grid(9);
        ObstacleClearance.Apply(values, 9, new Vector2(2));
        Assert.All(Enumerable.Range(0, 81), i => Assert.Equal(1, values[i * 2 + 1]));
    }

    [Fact]
    public void SolidPostMakesConservativeSymmetricClearanceWithoutMovingFloor()
    {
        var values = Grid(9);
        values[(4 * 9 + 4) * 2 + 1] = 0;
        ObstacleClearance.Apply(values, 9, new Vector2(2));
        float At(int x, int y) => values[(y * 9 + x) * 2 + 1];
        Assert.Equal(0, At(4, 4));
        Assert.InRange(At(5, 4), 0.09f, 0.10f);
        Assert.Equal(At(5, 4), At(3, 4));
        Assert.Equal(At(5, 4), At(4, 5));
        Assert.True(At(6, 4) > At(5, 4));
        Assert.Equal(1, At(0, 0));
        Assert.All(Enumerable.Range(0, 81), i => Assert.Equal(7, values[i * 2]));
    }

    [Fact]
    public void EntirelyUnknownGroundStaysHidden()
    {
        var values = new float[50];
        ObstacleClearance.Apply(values, 5, new Vector2(2, 1));
        Assert.All(values, v => Assert.Equal(0, v));
    }

    private static float[] Grid(int size) => Enumerable.Range(0, size * size)
        .SelectMany(_ => new float[] { 7, 1 }).ToArray();
}
