using System.Numerics;
using XivSurface.Core;

namespace XivSurface.Core.Tests;

public sealed class RugMotionProbeTests
{
    private static readonly Vector3 Player = new(10, 20, 30);

    [Fact]
    public void OffByDefaultAndExplicitlyStops()
    {
        var probe = new RugMotionProbe();
        Assert.False(probe.TryTarget(1, Player, 0, true, out _));
        Assert.True(probe.Start(1, Player, 0, 2));
        probe.Stop();
        Assert.False(probe.Active); Assert.False(probe.TryTarget(1, Player, .1, true, out _));
    }

    [Theory]
    [InlineData(1f)] [InlineData(1.5f)] [InlineData(2f)] [InlineData(30f)]
    public void BoundedDiagonalTargetReturnsUnderThePlayerAndExpires(float half)
    {
        var probe = new RugMotionProbe(); Assert.True(probe.Start(1, Player, 100, half));
        var root = new Vector2(Player.X, Player.Z); var previous = root; var moved = false;
        for (var i = 0; i < 900; i++)
        {
            Assert.True(probe.TryTarget(1, Player, 100 + i / 60d, true, out var target));
            Assert.InRange(Vector2.Distance(root, target), 0, Math.Min(1.2f, half - .55f) + .00001f);
            Assert.InRange(Math.Abs((target.X - root.X) - (target.Y - root.Y)), 0, .00001f);
            Assert.InRange(Vector2.Distance(previous, target) * 60, 0, 6.8f);
            moved |= Vector2.Distance(root, target) > .1f;
            if (i == 0) Assert.Equal(root, target);
            previous = target;
        }
        Assert.True(moved); Assert.InRange(Vector2.Distance(root, previous), 0, .002f);
        Assert.False(probe.TryTarget(1, Player, 115, true, out _)); Assert.False(probe.Active);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void SceneMovementZoneClockAndLongFrameChangesCancelInsteadOfMovingThePlayer(int reason)
    {
        var probe = new RugMotionProbe(); Assert.True(probe.Start(1, Player, 1, 2));
        var actual = reason == 0 ? Player + Vector3.UnitY * .06f : Player;
        if (reason == 5) actual = new(float.NaN);
        var time = reason == 2 ? .9 : reason == 3 ? 2.01 : 1.1;
        Assert.False(probe.TryTarget(reason == 1 ? 2u : 1u, actual, time, reason != 4, out _));
        Assert.False(probe.Active);
    }

    [Fact]
    public void InvalidStartFailsClosedAndCancelsAnyEarlierProbe()
    {
        var probe = new RugMotionProbe();
        Assert.False(probe.Start(0, Player, 1, 2));
        Assert.False(probe.Start(1, new(float.NaN), 1, 2));
        Assert.False(probe.Start(1, Player, double.NaN, 2));
        Assert.False(probe.Start(1, Player, -1, 2));
        Assert.False(probe.Start(1, Player, 1, .9f));
        Assert.True(probe.Start(1, Player, 1, 2));
        Assert.False(probe.Start(1, Player, 1, float.NaN)); Assert.False(probe.Active);
    }
}
