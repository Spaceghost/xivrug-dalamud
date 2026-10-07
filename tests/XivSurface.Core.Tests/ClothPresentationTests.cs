using System.Numerics;
using XivSurface.Core;

namespace XivSurface.Core.Tests;

public sealed class ClothPresentationTests
{
    private static ClothMesh Mesh(float x) => ClothSurface.Build(new(x, 0), new(2), 0, new float[9], 3, 0, false);

    [Fact]
    public void MeshPublicationHasNoInstantTranslationAndKeepsWholeTopology()
    {
        var flow = new ClothPresentation(); var a = Mesh(0); var b = Mesh(.6f);
        flow.Update(a, Vector2.Zero, 0);
        var first = flow.Update(b, new(.6f, 0), .2);
        Assert.Same(a, first);
        var middle = flow.Update(b, new(.6f, 0), .25);
        Assert.Equal(.6f * (1 - 1.5f * MathF.Exp(-.5f)), flow.Center.X, 4);
        Assert.Same(b.Indices, middle.Indices); Assert.Same(b.UV, middle.UV);
        for (var i = 0; i < a.Positions.Length; i++)
            Assert.InRange(middle.Positions[i].X, a.Positions[i].X, b.Positions[i].X);
        Assert.NotSame(b, flow.Update(b, new(.6f, 0), .31));
        for (var i = 1; i <= 100; i++) flow.Update(b, new(.6f, 0), .31 + i * .02);
        Assert.Same(b, flow.Update(b, new(.6f, 0), 2.32));
    }

    [Fact]
    public void InterruptedTransitionStartsAtDisplayedSurfaceInsteadOfOldTarget()
    {
        var flow = new ClothPresentation(); var a = Mesh(0); var b = Mesh(.5f); var c = Mesh(.8f);
        flow.Update(a, Vector2.Zero, 0); flow.Update(b, new(.5f, 0), .1);
        var displayed = flow.Update(b, new(.5f, 0), .15);
        var center = flow.Center;
        Assert.Same(displayed, flow.Update(c, new(.8f, 0), .16));
        Assert.Equal(center, flow.Center);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(144)]
    public void ConvergesWithoutOvershootAtDifferentFrameRates(int hz)
    {
        var flow = new ClothPresentation(); var a = Mesh(0); var b = Mesh(1);
        flow.Update(a, Vector2.Zero, 0); flow.Update(b, Vector2.UnitX, .1);
        var old = 0f;
        for (var i = 1; i <= hz; i++)
        {
            flow.Update(b, Vector2.UnitX, .1 + (double)i / hz);
            Assert.InRange(flow.Center.X, old, 1); old = flow.Center.X;
        }
        Assert.Equal(1, flow.Center.X);
    }

    [Fact]
    public void ZoneLikeDiscontinuitiesAndClockResetsDoNotDragOldGeometry()
    {
        var flow = new ClothPresentation(); var a = Mesh(0); var b = Mesh(20);
        flow.Update(a, Vector2.Zero, 0);
        Assert.Same(b, flow.Update(b, new(20, 0), .01));
        flow.Reset(); Assert.Same(a, flow.Update(a, Vector2.Zero, 0));
        Assert.Same(b, flow.Update(b, new(20, 0), 2));
    }

    [Fact]
    public void SlowIrregularCollisionBatchesDoNotCreateFixedDurationPauses()
    {
        var flow = new ClothPresentation(); var target = Mesh(0);
        flow.Update(target, Vector2.Zero, 0);
        var publication = 0d; var targetX = 0f;
        foreach (var duration in new[] { .18, .30, .22, .26 })
        {
            targetX += .6f; target = Mesh(targetX);
            flow.Update(target, new(targetX, 0), publication);
            var previous = flow.Center.X;
            for (var i = 1; i <= (int)Math.Round(duration * 100); i++)
            {
                flow.Update(target, new(targetX, 0), publication + i * .01);
                Assert.True(flow.Center.X > previous); // includes frames beyond the old .10s stop
                Assert.InRange(flow.Center.X, previous, targetX);
                previous = flow.Center.X;
            }
            publication += duration;
        }
    }

    [Fact]
    public void CenterAndEveryMaterialVertexUseTheSameContinuousWeight()
    {
        var flow = new ClothPresentation(); var a = Mesh(0); var b = Mesh(.7f);
        flow.Update(a, Vector2.Zero, 0); flow.Update(b, new(.7f, 0), .2);
        for (var step = 1; step <= 30; step++)
        {
            var displayed = flow.Update(b, new(.7f, 0), .2 + step * .01);
            for (var i = 0; i < a.Positions.Length; i++)
            {
                Assert.InRange(Math.Abs(displayed.Positions[i].X - a.Positions[i].X - flow.Center.X), 0, .00001f);
                Assert.Equal(a.Positions[i].Y, displayed.Positions[i].Y, 6);
                Assert.Equal(a.Positions[i].Z, displayed.Positions[i].Z, 6);
            }
        }
    }

    [Fact]
    public void DampedApproachIsFrameRateIndependentBeforeSettling()
    {
        static (Vector2 Center, ClothMesh Mesh) Sample(int hz)
        {
            var flow = new ClothPresentation(); var a = Mesh(0); var b = Mesh(1);
            flow.Update(a, Vector2.Zero, 0); flow.Update(b, Vector2.UnitX, .2);
            var displayed = a;
            for (var i = 1; i <= hz; i++) displayed = flow.Update(b, Vector2.UnitX, .2 + .3 * i / hz);
            return (flow.Center, displayed);
        }
        var slow = Sample(30); var fast = Sample(144);
        Assert.InRange(Vector2.Distance(slow.Center, fast.Center), 0, .00001f);
        for (var i = 0; i < slow.Mesh.Positions.Length; i++)
            Assert.InRange(Vector3.Distance(slow.Mesh.Positions[i], fast.Mesh.Positions[i]), 0, .00001f);
    }

    [Fact]
    public void LongPublicationIntervalCannotMakeTheResponseSluggish()
    {
        var flow = new ClothPresentation(); var a = Mesh(0); var b = Mesh(1);
        flow.Update(a, Vector2.Zero, 0);
        for (var i = 1; i <= 30; i++) flow.Update(a, Vector2.Zero, i * .1);
        flow.Update(b, Vector2.UnitX, 3);
        flow.Update(b, Vector2.UnitX, 3.3);
        Assert.InRange(flow.Center.X, .712f, .713f); // max .12s damping time, not a multi-second cadence
        flow.Update(b, Vector2.UnitX, 3.6);
        Assert.InRange(flow.Center.X, .959f, .960f);
    }

    [Fact]
    public void CompatiblePublicationPreservesPositionAndVelocity()
    {
        var flow = new ClothPresentation(); var a = Mesh(0); var b = Mesh(.6f); var c = Mesh(1.2f);
        flow.Update(a, Vector2.Zero, 0); flow.Update(b, new(.6f, 0), .1);
        var shown = flow.Update(b, new(.6f, 0), .2);
        var at = flow.Center; var velocity = flow.Velocity;
        Assert.True(velocity.X > 0);
        Assert.Same(shown, flow.Update(c, new(1.2f, 0), .2));
        Assert.Equal(at, flow.Center); Assert.Equal(velocity, flow.Velocity);
        var next = flow.Update(c, new(1.2f, 0), .20001);
        Assert.InRange(Math.Abs(flow.Velocity.X - velocity.X), 0, .01f);
        Assert.InRange((flow.Center.X - at.X) / .00001f, velocity.X - .03f, velocity.X + .03f);
        for (var i = 0; i < next.Positions.Length; i++)
            Assert.InRange((next.Positions[i].X - shown.Positions[i].X) / .00001f, velocity.X - .03f, velocity.X + .03f);
    }

    [Theory]
    [InlineData(.1, 30)] [InlineData(.1, 60)] [InlineData(.1, 120)]
    [InlineData(.2, 30)] [InlineData(.2, 60)] [InlineData(.2, 120)]
    public void RegularBatchesReduceSteadySpeedRippleAndAcceleration(double interval, int hz)
    {
        var flow = new ClothPresentation(); var target = Mesh(0); var targetX = 0f;
        flow.Update(target, Vector2.Zero, 0);
        var every = (int)Math.Round(interval * hz); var dt = 1d / hz;
        var response = Math.Clamp(interval * .5, ClothPresentation.MinimumResponseSeconds, ClothPresentation.MaximumResponseSeconds);
        var speeds = new List<double>(); var accelerations = new List<double>(); var oldAccelerations = new List<double>();
        var lags = new List<double>();
        double oldX = 0, previousSpeed = 0, previousOldSpeed = 0;
        for (var frame = 1; frame <= hz * 8; frame++)
        {
            var now = frame * dt; var before = flow.Center.X; var oldBefore = oldX;
            flow.Update(target, new(targetX, 0), now);
            oldX = targetX + (oldX - targetX) * Math.Exp(-dt / response);
            var speed = (flow.Center.X - before) / dt; var oldSpeed = (oldX - oldBefore) / dt;
            if (frame > hz * 3)
            {
                speeds.Add(speed); lags.Add(6 * now - flow.Center.X);
                accelerations.Add((speed - previousSpeed) / dt);
                oldAccelerations.Add((oldSpeed - previousOldSpeed) / dt);
            }
            previousSpeed = speed; previousOldSpeed = oldSpeed;
            // Same order as ClothContactSampler: advance old presentation,
            // then publish new contacts at that same timestamp.
            if (frame % every == 0)
            {
                targetX = (float)(6 * now); target = Mesh(targetX);
                var velocity = flow.Velocity;
                flow.Update(target, new(targetX, 0), now);
                Assert.Equal(velocity, flow.Velocity);
            }
        }
        Assert.InRange(speeds.Average(), 5.99, 6.01);
        Assert.InRange(speeds.Max() / speeds.Min(), 1, 1.61);
        Assert.True(Rms(accelerations) < Rms(oldAccelerations) * .3);
        // This is publication-filter latency only. Runtime batches sampled at
        // their START have an additional interval*speed of sampling latency;
        // these tests deliberately do not claim that pipeline is decoupled.
        Assert.InRange(lags.Average(), 6 * (interval * .5 + response * 2) - .01,
            6 * (interval * .5 + response * 2) + .01);
    }

    [Theory]
    [InlineData(30)] [InlineData(60)] [InlineData(120)]
    public void StopCannotOvershootAndReversalCancelsOnlyOutwardVelocity(int hz)
    {
        var flow = new ClothPresentation(); var a = Mesh(0); var b = Mesh(1);
        flow.Update(a, Vector2.Zero, 0); flow.Update(b, Vector2.UnitX, .2);
        flow.Update(b, Vector2.UnitX, .28);
        Assert.True(flow.Velocity.X > 0);
        var start = flow.Center.X; var reverse = Mesh(-.5f);
        flow.Update(reverse, new(-.5f, 0), .28);
        Assert.Equal(start, flow.Center.X); Assert.Equal(Vector2.Zero, flow.Velocity);
        var previous = start;
        for (var i = 1; i <= hz * 2; i++)
        {
            var shown = flow.Update(reverse, new(-.5f, 0), .28 + (double)i / hz);
            Assert.InRange(flow.Center.X, -.5f, previous);
            Assert.InRange(flow.Velocity.X, -20, 0);
            for (var j = 0; j < shown.Positions.Length; j++)
                Assert.InRange(shown.Positions[j].X, reverse.Positions[j].X, a.Positions[j].X + start + .00001f);
            previous = flow.Center.X;
        }
        Assert.Equal(-.5f, flow.Center.X);
        // A target that stops short of the incoming trajectory cannot inherit
        // a velocity that carries it beyond its new measured interval.
        var stop = Mesh(-.49f);
        flow.Update(stop, new(-.49f, 0), 2.28);
        for (var i = 1; i <= hz; i++)
        {
            flow.Update(stop, new(-.49f, 0), 2.28 + (double)i / hz);
            Assert.InRange(flow.Center.X, -.5f, -.49f);
        }
    }

    [Fact]
    public void CornerAndRisingFloorStayInsideMeasuredCoordinateIntervals()
    {
        static ClothMesh Lift(ClothMesh mesh, Vector3 offset) =>
            new(mesh.Positions.Select(p => p + offset).ToArray(), mesh.Normals, mesh.UV, mesh.Indices, mesh.Size);
        var flow = new ClothPresentation(); var a = Mesh(0); var b = Mesh(.8f);
        flow.Update(a, Vector2.Zero, 0); flow.Update(b, new(.8f, 0), .1);
        var start = flow.Update(b, new(.8f, 0), .2);
        var at = flow.Center;
        var turn = Lift(Mesh(.8f), new(0, .4f, .8f));
        flow.Update(turn, new(.8f, .8f), .2);
        for (var frame = 1; frame <= 120; frame++)
        {
            var shown = flow.Update(turn, new(.8f, .8f), .2 + frame / 120d);
            Assert.InRange(flow.Center.X, at.X, .8f); Assert.InRange(flow.Center.Y, 0, .8f);
            for (var i = 0; i < shown.Positions.Length; i++)
            {
                var p = shown.Positions[i]; var lower = Vector3.Min(start.Positions[i], turn.Positions[i]);
                var upper = Vector3.Max(start.Positions[i], turn.Positions[i]);
                Assert.InRange(p.X, lower.X, upper.X); Assert.InRange(p.Y, lower.Y, upper.Y); Assert.InRange(p.Z, lower.Z, upper.Z);
                Assert.InRange(shown.Normals[i].Length(), .99999f, 1.00001f);
            }
        }
        // Endpoint intervals are not evidence about unsampled intermediate
        // walls or terrain: scene-depth rejection remains independently needed.
    }

    [Fact]
    public void RepeatedTimestampDoesNotSpendMotionAndResetClearsMomentum()
    {
        var flow = new ClothPresentation(); var a = Mesh(0); var b = Mesh(1);
        flow.Update(a, Vector2.Zero, 0); flow.Update(b, Vector2.UnitX, .1);
        var shown = flow.Update(b, Vector2.UnitX, .15); var at = flow.Center; var velocity = flow.Velocity;
        for (var i = 0; i < 5; i++)
        {
            Assert.Same(shown, flow.Update(b, Vector2.UnitX, .15));
            Assert.Equal(at, flow.Center); Assert.Equal(velocity, flow.Velocity);
        }
        flow.Reset(); flow.Update(a, Vector2.Zero, 3);
        Assert.Equal(Vector2.Zero, flow.Center); Assert.Equal(Vector2.Zero, flow.Velocity);
        flow.Update(b, Vector2.UnitX, 5);
        Assert.Equal(Vector2.UnitX, flow.Center); Assert.Equal(Vector2.Zero, flow.Velocity);
    }

    [Fact]
    public void AbruptNearbyStopClampsIncomingMomentumWithoutRebound()
    {
        var flow = new ClothPresentation(); var a = Mesh(0); var b = Mesh(1);
        flow.Update(a, Vector2.Zero, 0); flow.Update(b, Vector2.UnitX, .1);
        var start = flow.Update(b, Vector2.UnitX, .15); var at = flow.Center.X;
        Assert.True(flow.Velocity.X > 1);
        var goal = at + .001f; var stop = Mesh(goal);
        flow.Update(stop, new(goal, 0), .15);
        for (var i = 1; i <= 120; i++)
        {
            var shown = flow.Update(stop, new(goal, 0), .15 + i / 120d);
            Assert.InRange(flow.Center.X, at, goal); Assert.True(flow.Velocity.X >= 0);
            for (var j = 0; j < shown.Positions.Length; j++)
                Assert.InRange(shown.Positions[j].X, start.Positions[j].X, stop.Positions[j].X);
        }
        Assert.Equal(goal, flow.Center.X); Assert.Equal(Vector2.Zero, flow.Velocity);
    }

    [Fact]
    public void ChangingBatchCadenceDoesNotResetCompatibleVelocity()
    {
        var flow = new ClothPresentation(); var target = Mesh(0); var targetX = 0f;
        flow.Update(target, Vector2.Zero, 0);
        var now = 0d;
        foreach (var duration in new[] { .1, .2, .075, .15, .25, .09 })
        {
            for (var i = 1; i <= 30; i++) flow.Update(target, new(targetX, 0), now + duration * i / 30);
            now += duration;
            var velocity = flow.Velocity; var at = flow.Center;
            targetX += (float)(6 * duration); target = Mesh(targetX);
            flow.Update(target, new(targetX, 0), now);
            Assert.Equal(at, flow.Center); Assert.Equal(velocity, flow.Velocity);
        }
    }

    private static double Rms(List<double> values) => Math.Sqrt(values.Average(v => v * v));
}
