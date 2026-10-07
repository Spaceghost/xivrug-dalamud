using System.Numerics;
using XivSurface.Core;

namespace XivSurface.Core.Tests;

public class RugFollowControllerTests
{
    private const float Radius = .2f;
    private const float Response = .55f;

    [Fact]
    public void TinyCircularWakeAreaStaysPlantedWithoutBuildingMotion()
    {
        var follow = new RugFollowController();
        Assert.False(follow.HasCenter);
        var origin = new Vector2(10, 20);
        Assert.True(follow.Step(origin, 0, Radius, Response));
        for (var i = 1; i <= 100; ++i)
        {
            var offset = i % 2 == 0 ? new Vector2(.12f, .12f) : new Vector2(.19f, 0);
            Assert.True(follow.Step(origin + offset, i * .1, Radius, Response));
            Assert.Equal(origin, follow.Anchor);
            Assert.Equal(origin, follow.Center);
            Assert.Equal(Vector2.Zero, follow.Velocity);
            Assert.Equal(0, follow.Travel.Z);
        }
    }

    [Fact]
    public void WakeAreaIsCircularRatherThanAnAxisAlignedBox()
    {
        var follow = new RugFollowController();
        follow.Step(Vector2.Zero, 0, Radius, Response, false);
        follow.Step(new(.15f, .15f), .1, Radius, Response, false);
        Assert.InRange(follow.Anchor.X, .00001f, .14999f);
        Assert.Equal(follow.Anchor.X, follow.Anchor.Y);
        Assert.Equal(follow.Anchor, follow.Center);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(144)]
    public void CircleCrossingAndLinearTrackingMatchAnalyticMotionAcrossFrameRates(int fps)
    {
        var follow = new RugFollowController();
        follow.Step(Vector2.Zero, 0, Radius, Response, false);
        const double speed = 6;
        for (var i = 1; i <= fps; ++i)
        {
            var time = (double)i / fps;
            var player = new Vector2((float)(speed * time), 0);
            Assert.True(follow.Step(player, time, Radius, Response, false));
            if (player.X <= Radius)
                Assert.Equal(Vector2.Zero, follow.Anchor);
            else
            {
                var activeTime = time - Radius / speed;
                var omega = 4.75 / Response;
                var expected = player.X + (-Radius + (-speed - omega * Radius) * activeTime)
                    * Math.Exp(-omega * activeTime);
                Assert.InRange(Math.Abs(follow.Anchor.X - expected), 0, .000003);
            }
            Assert.Equal(0, follow.Anchor.Y);
        }
        Assert.InRange(6 - follow.Anchor.X, 0, .003f);
        Assert.InRange(follow.Velocity.X, 5.99f, 6.02f);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(144)]
    public void GentleAccelerationCatchesUnderfootInsteadOfKeepingAResponseTimesSpeedLag(int fps)
    {
        var follow = new RugFollowController();
        follow.Step(Vector2.Zero, 0, Radius, Response, false);
        var previous = Vector2.Zero;
        var sawGlide = false;
        for (var i = 1; i <= fps * 4; ++i)
        {
            var time = (double)i / fps;
            var distance = time <= 1 ? 3 * time * time : 3 + 6 * (time - 1);
            var player = new Vector2((float)distance, 0);
            follow.Step(player, time, Radius, Response, false);
            Assert.InRange(follow.Anchor.X - previous.X, 0, 8f / fps);
            Assert.InRange(Vector2.Distance(follow.Anchor, player), 0, .8f);
            Assert.InRange(follow.Velocity.X, 0, 8);
            sawGlide |= follow.Anchor.X > 0 && follow.Anchor.X < player.X - .01f;
            previous = follow.Anchor;
        }
        Assert.True(sawGlide, "Crossing the wake circle must glide, not snap to the traveller.");
        Assert.InRange(Vector2.Distance(follow.Anchor, new(21, 0)), 0, .001f);
        Assert.InRange(Math.Abs(follow.Velocity.X - 6), 0, .001f);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(144)]
    public void AbruptStopDuringCatchupSettlesMonotonicallyWithoutReboundAndRearmsWakeCircle(int fps)
    {
        var follow = new RugFollowController();
        follow.Step(Vector2.Zero, 0, Radius, Response, false);
        var movingFrames = Math.Max(1, fps / 6);
        for (var i = 1; i <= movingFrames; ++i)
            follow.Step(new(6f * i / fps, 0), (double)i / fps, Radius, Response, false);
        var stop = new Vector2(6f * movingFrames / fps, 0);
        Assert.True(follow.Anchor.X < stop.X);
        var previous = follow.Anchor.X;
        for (var i = 1; i <= fps * 3; ++i)
        {
            follow.Step(stop, (double)(movingFrames + i) / fps, Radius, Response, false);
            Assert.InRange(follow.Anchor.X, previous, stop.X);
            Assert.InRange(follow.Velocity.X, 0, 8);
            Assert.Equal(0, follow.Anchor.Y);
            previous = follow.Anchor.X;
        }
        Assert.Equal(stop, follow.Anchor);
        Assert.Equal(Vector2.Zero, follow.Velocity);
        for (var i = 1; i <= fps; ++i)
            follow.Step(stop + new Vector2(.1f, .1f), (double)(movingFrames + fps * 3 + i) / fps,
                Radius, Response, false);
        Assert.Equal(stop, follow.Anchor);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(144)]
    public void AbruptReversalHasSmallBoundedExcursionThenMatchesTheNewDirection(int fps)
    {
        var follow = new RugFollowController();
        follow.Step(Vector2.Zero, 0, Radius, Response, false);
        for (var i = 1; i <= fps; ++i)
            follow.Step(new(6f * i / fps, 0), (double)i / fps, Radius, Response, false);
        for (var i = 1; i <= fps * 2; ++i)
        {
            var player = new Vector2(6 - 6f * i / fps, 0);
            follow.Step(player, 1 + (double)i / fps, Radius, Response, false);
            Assert.InRange(follow.Anchor.X, player.X - .001f, 6.3f);
            Assert.InRange(Vector2.Distance(follow.Anchor, player), 0, .7f);
            Assert.InRange(Math.Abs(follow.Velocity.X), 0, 9);
            Assert.Equal(0, follow.Anchor.Y);
        }
        Assert.InRange(Vector2.Distance(follow.Anchor, new(-6, 0)), 0, .001f);
        Assert.InRange(Math.Abs(follow.Velocity.X + 6), 0, .001f);
    }

    [Fact]
    public void RepeatedTurnsKeepFlourishAndMotionUniformBoundedWithoutChangingTheAnchor()
    {
        var animated = new RugFollowController();
        var quiet = new RugFollowController();
        animated.Step(Vector2.Zero, 0, Radius, Response);
        quiet.Step(Vector2.Zero, 0, Radius, Response, false);
        var sawSway = false;
        var sawWrappedPhase = false;
        var previousPhase = 0f;
        for (var i = 1; i <= 1800; ++i)
        {
            var time = i / 60d;
            var player = new Vector2((float)(3 * Math.Sin(time * 2)), (float)(2 * Math.Sin(time * 3)));
            Assert.True(animated.Step(player, time, Radius, Response));
            Assert.True(quiet.Step(player, time, Radius, Response, false));
            Assert.Equal(animated.Anchor, quiet.Anchor);
            Assert.Equal(animated.Velocity, quiet.Velocity);
            Assert.Equal(quiet.Anchor, quiet.Center);
            Assert.Equal(0, quiet.Travel.Z);
            Assert.InRange(Vector2.Distance(animated.Anchor, player), 0, 1);
            Assert.InRange(Vector2.Distance(animated.Center, animated.Anchor), 0,
                RugFollowController.MaximumFlourishOffset + .000001f);
            Assert.InRange(animated.Travel.Z, 0, 1);
            Assert.InRange(animated.Travel.W, 0, MathF.Tau);
            Assert.InRange(new Vector2(animated.Travel.X, animated.Travel.Y).Length(), .999999f, 1.000001f);
            Assert.InRange(animated.Velocity.Length(), 0, 15);
            sawSway |= Vector2.Distance(animated.Anchor, animated.Center) > .01f;
            sawWrappedPhase |= animated.Travel.W < previousPhase;
            previousPhase = animated.Travel.W;
        }
        Assert.True(sawSway, "The bounds test must exercise actual animated displacement.");
        Assert.True(sawWrappedPhase, "Distance phase must stay bounded during long travel.");
    }

    [Fact]
    public void FlourishFadesAwayAfterStopping()
    {
        var follow = MovingCarpet();
        Assert.True(follow.Travel.Z > .1f);
        for (var i = 1; i <= 360; ++i)
            follow.Step(new(2.4f, 0), .4 + i / 60d, Radius, Response);
        Assert.Equal(new Vector2(2.4f, 0), follow.Anchor);
        Assert.Equal(follow.Anchor, follow.Center);
        Assert.Equal(0, follow.Travel.Z);
        Assert.Equal(Vector2.Zero, follow.Velocity);
    }

    [Fact]
    public void ZeroWakeRadiusStillGlidesInsteadOfSnapping()
    {
        var follow = new RugFollowController();
        follow.Step(Vector2.Zero, 0, 0, Response, false);
        follow.Step(new(.05f, 0), .1, 0, Response, false);
        Assert.InRange(follow.Anchor.X, .00001f, .04999f);
        Assert.InRange(follow.Velocity.X, .00001f, .6f);
    }

    [Theory]
    [InlineData(100, 20, .5)]
    [InlineData(7, 0, .5)]
    [InlineData(2.5f, 0, 1.401)]
    [InlineData(2.5f, 0, .1)]
    [InlineData(2.5f, 0, double.MaxValue)]
    public void DiscontinuitiesReanchorAndDiscardAllVelocityAndFlourish(float x, float z, double time)
    {
        var follow = MovingCarpet();
        Assert.NotEqual(Vector2.Zero, follow.Velocity);
        var player = new Vector2(x, z);
        Assert.True(follow.Step(player, time, Radius, Response));
        Assert.True(follow.HasCenter);
        Assert.Equal(player, follow.Anchor);
        Assert.Equal(player, follow.Center);
        Assert.Equal(Vector2.Zero, follow.Velocity);
        Assert.Equal(Vector4.Zero, follow.Travel);
    }

    [Fact]
    public void RepeatedTimestampDoesNotAdvanceOrConsumeAnUnobservedPlayerSample()
    {
        var follow = MovingCarpet();
        var control = MovingCarpet();
        var anchor = follow.Anchor;
        var center = follow.Center;
        var velocity = follow.Velocity;
        var travel = follow.Travel;
        Assert.True(follow.Step(new(3, 1), .4, Radius, Response));
        Assert.Equal(anchor, follow.Anchor);
        Assert.Equal(center, follow.Center);
        Assert.Equal(velocity, follow.Velocity);
        Assert.Equal(travel, follow.Travel);
        follow.Step(new(3, 0), .5, Radius, Response);
        control.Step(new(3, 0), .5, Radius, Response);
        Assert.Equal(control.Anchor, follow.Anchor);
        Assert.Equal(control.Velocity, follow.Velocity);
        Assert.Equal(control.Travel, follow.Travel);
    }

    [Fact]
    public void MotionOffSuppressesExistingFlourishEvenAtARepeatedTimestamp()
    {
        var follow = MovingCarpet();
        var anchor = follow.Anchor;
        var velocity = follow.Velocity;
        Assert.True(follow.Travel.Z > 0);
        follow.Step(new(2.4f, 0), .4, Radius, Response, false);
        Assert.Equal(anchor, follow.Anchor);
        Assert.Equal(velocity, follow.Velocity);
        Assert.Equal(anchor, follow.Center);
        Assert.Equal(0, follow.Travel.Z);
    }

    [Theory]
    [InlineData(5, .125)]
    [InlineData(30, 1)]
    public void ExactDiscontinuityLimitsDoNotUnexpectedlySnap(float distance, double elapsed)
    {
        var follow = new RugFollowController();
        follow.Step(Vector2.Zero, 0, 0, Response, false);
        Assert.True(follow.Step(new(distance, 0), elapsed, 0, Response, false));
        Assert.InRange(follow.Anchor.X, .00001f, distance - .00001f);
        Assert.NotEqual(Vector2.Zero, follow.Velocity);
    }

    [Fact]
    public void InvalidInputClearsStateAndNextValidSampleReanchors()
    {
        var invalid = new (Vector2 Player, double Time, float Radius, float Response)[]
        {
            (new(float.NaN), 1, Radius, Response),
            (new(float.PositiveInfinity), 1, Radius, Response),
            (Vector2.One, double.NaN, Radius, Response),
            (Vector2.One, double.PositiveInfinity, Radius, Response),
            (Vector2.One, -1, Radius, Response),
            (Vector2.One, 1, float.NaN, Response),
            (Vector2.One, 1, float.PositiveInfinity, Response),
            (Vector2.One, 1, -1, Response),
            (Vector2.One, 1, 9, Response),
            (Vector2.One, 1, Radius, float.NaN),
            (Vector2.One, 1, Radius, float.PositiveInfinity),
            (Vector2.One, 1, Radius, .01f),
            (Vector2.One, 1, Radius, 3),
        };
        foreach (var (player, time, radius, response) in invalid)
        {
            var follow = MovingCarpet();
            Assert.False(follow.Step(player, time, radius, response));
            AssertCleared(follow);
            Assert.True(follow.Step(new(7, 8), 2, Radius, Response));
            Assert.Equal(new Vector2(7, 8), follow.Anchor);
            Assert.Equal(follow.Anchor, follow.Center);
            Assert.Equal(Vector2.Zero, follow.Velocity);
            Assert.Equal(Vector4.Zero, follow.Travel);
        }
    }

    [Fact]
    public void FiniteExtremeCoordinatesReanchorWithoutOverflowingMotion()
    {
        var follow = MovingCarpet();
        Assert.True(follow.Step(new(float.MaxValue, float.MinValue), .5, Radius, Response));
        Assert.Equal(new Vector2(float.MaxValue, float.MinValue), follow.Center);
        Assert.Equal(Vector2.Zero, follow.Velocity);
        Assert.Equal(Vector4.Zero, follow.Travel);
        Assert.True(follow.Step(new(float.MinValue, float.MaxValue), .6, Radius, Response));
        Assert.Equal(new Vector2(float.MinValue, float.MaxValue), follow.Center);
        Assert.Equal(Vector2.Zero, follow.Velocity);
        Assert.Equal(Vector4.Zero, follow.Travel);
    }

    [Fact]
    public void ExplicitResetClearsAllMotionAndZoneHistory()
    {
        var follow = MovingCarpet();
        follow.Reset();
        AssertCleared(follow);
        follow.Step(new(3, 4), 0, Radius, Response);
        Assert.Equal(new Vector2(3, 4), follow.Center);
        Assert.Equal(Vector2.Zero, follow.Velocity);
        Assert.Equal(Vector4.Zero, follow.Travel);
    }

    private static RugFollowController MovingCarpet()
    {
        var follow = new RugFollowController();
        follow.Step(Vector2.Zero, 0, Radius, Response);
        for (var i = 1; i <= 24; ++i)
            follow.Step(new(i / 10f, 0), i / 60d, Radius, Response);
        return follow;
    }

    private static void AssertCleared(RugFollowController follow)
    {
        Assert.False(follow.HasCenter);
        Assert.Equal(Vector2.Zero, follow.Anchor);
        Assert.Equal(Vector2.Zero, follow.Center);
        Assert.Equal(Vector2.Zero, follow.Velocity);
        Assert.Equal(Vector4.Zero, follow.Travel);
    }
}
