using System.Numerics;
using XivSurface.Core;

namespace XivSurface.Core.Tests;

public class WorldMapCoordinatesTests
{
    [Theory]
    [InlineData(100, -1024, 0)]
    [InlineData(100, 0, 0.5f)]
    [InlineData(100, 1024, 1)]
    [InlineData(200, -512, 0)]
    [InlineData(200, 512, 1)]
    [InlineData(50, 2048, 1)]
    public void CanonicalTextureBoundsHonorSizeFactor(ushort sizeFactor, float world, float expected)
    {
        var map = new WorldMapCoordinates(sizeFactor, 0, 0);
        Assert.True(map.TryWorldToUv(new(world, world), out var uv));
        Assert.Equal(expected, uv.X, 5);
        Assert.Equal(expected, uv.Y, 5);
    }

    [Fact]
    public void OffsetsAreAddedBeforeScaleAndWorldZIsTextureY()
    {
        var map = new WorldMapCoordinates(200, 100, -50);
        Assert.True(map.TryWorldToUv(new(-100, 50), out var center));
        Assert.Equal(new Vector2(0.5f), center);
        Assert.True(map.TryWorldToUv(new(412, -462), out var corner));
        Assert.Equal(new Vector2(1, 0), corner);
        Assert.Equal(new Vector4(-612, -462, 412, 562), map.WorldBounds);
    }

    [Fact]
    public void TransformMatchesShaderFormula()
    {
        var map = new WorldMapCoordinates(95, -245, 390);
        var point = new Vector2(318, -211);
        var transform = map.Transform;
        var shaderUv = (point + new Vector2(transform.X, transform.Y)) * new Vector2(transform.Z, transform.W) + new Vector2(0.5f);
        Assert.True(map.TryWorldToUv(point, out var uv));
        Assert.Equal(shaderUv, uv);
    }

    [Fact]
    public void NeighborhoodShows150YalmsAcrossHalfTheRugNotJustPhysicalRugWidth()
    {
        var map = new WorldMapCoordinates(100, 0, 0);
        var player = new Vector2(200, -300);
        Assert.True(map.TryNeighborhoodUv(player, Vector2.Zero, new(150, 150), out var center));
        Assert.True(map.TryWorldToUv(player, out var playerUv));
        Assert.Equal(playerUv, center);
        Assert.True(map.TryNeighborhoodUv(player, new(1, -1), new(150, 150), out var corner));
        Assert.True(map.TryWorldToUv(new(350, -450), out var expected));
        Assert.Equal(expected, corner);
    }

    [Fact]
    public void OutOfMapUvIsNotClampedToARenderedEdgeStreak()
    {
        var map = new WorldMapCoordinates(100, 0, 0);
        Assert.True(map.TryWorldToUv(new(3000, 0), out var uv));
        Assert.True(uv.X > 1); // the shader can reject it instead of stretching the edge texel
    }

    [Fact]
    public void InvalidScaleAndCoordinatesFailClosed()
    {
        Assert.False(default(WorldMapCoordinates).TryWorldToUv(Vector2.Zero, out _));
        var map = new WorldMapCoordinates(100, 0, 0);
        Assert.False(map.TryWorldToUv(new(float.NaN, 0), out _));
        Assert.False(map.TryWorldToUv(new(0, float.PositiveInfinity), out _));
        Assert.False(map.TryNeighborhoodUv(Vector2.Zero, Vector2.Zero, new(0, 150), out _));
        Assert.False(map.TryNeighborhoodUv(Vector2.Zero, new(float.NaN, 0), new(150), out _));
        Assert.False(map.TryNeighborhoodUv(Vector2.Zero, Vector2.Zero, new(float.PositiveInfinity), out _));
    }
}
