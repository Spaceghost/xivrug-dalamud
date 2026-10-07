using System.Numerics;

namespace XivSurface.Core.Tests;

public class CarpetIdleBehaviorTests
{
    private static void Advance(CarpetIdleBehavior state, double until, bool ride = false)
    {
        for (double now = 0; now <= until; now += .5)
            state.Update(true, ride, true, Vector3.Zero, 0, now);
    }

    [Fact]
    public void FifteenSecondsStartsFiniteFootworkThenRest()
    {
        var state = new CarpetIdleBehavior(); Advance(state, 14.5);
        Assert.Equal(CarpetIdleState.Waiting, state.State);
        Assert.Equal(CarpetIdleState.Footwork, state.Update(true, false, true, Vector3.Zero, 0, 15));
        for (var t = 15.5; t < 20.6; t += .5) state.Update(true, false, true, Vector3.Zero, 0, t);
        Assert.Equal(CarpetIdleState.Resting, state.Update(true, false, true, Vector3.Zero, 0, 20.6));
    }

    [Fact]
    public void TinyMovementAccumulatesAndResetsAnIdlePose()
    {
        var state = new CarpetIdleBehavior(); Advance(state, 22);
        for (var i = 1; i <= 20; i++) state.Update(true, false, true, new Vector3(i * .001f, 0, 0), 0, 22 + i * .016);
        Assert.Equal(CarpetIdleState.Waiting, state.State);
        Assert.InRange(state.IdleSeconds, 0, .2);
    }

    [Fact]
    public void RideCanSuppressWalkPoseButYieldsImmediatelyToUnsafeState()
    {
        var state = new CarpetIdleBehavior();
        state.Update(true, true, true, Vector3.Zero, 0, 0);
        Assert.Equal(CarpetIdleState.Riding, state.Update(true, true, true, Vector3.UnitX, 0, .2));
        Assert.Equal(CarpetIdleState.Waiting, state.Update(true, true, false, Vector3.UnitX, 0, .3));
        Assert.Equal(0, state.IdleSeconds);
    }

    [Fact]
    public void SuspendedFramesNeverCountAsIdle()
    {
        var state = new CarpetIdleBehavior(); Advance(state, 14);
        Assert.Equal(CarpetIdleState.Waiting, state.Update(true, false, true, Vector3.Zero, 0, 100));
        Assert.Equal(0, state.IdleSeconds);
    }

    [Fact]
    public void TurningAndDisablingIdleClearRest()
    {
        var state = new CarpetIdleBehavior(); Advance(state, 22);
        Assert.Equal(CarpetIdleState.Waiting, state.Update(true, false, true, Vector3.Zero, .1f, 22.1));
        Assert.Equal(CarpetIdleState.Riding, state.Update(false, true, true, Vector3.Zero, .1f, 22.2));
    }
    [Fact]
    public void MeasuredWalkingAndReturnDurationFinishesBeforeResting()
    {
        var state=new CarpetIdleBehavior();
        for(double t=0;t<32;t+=.5)state.Update(true,false,true,Vector3.Zero,0,t,17);
        Assert.Equal(CarpetIdleState.Footwork,state.State);
        Assert.Equal(CarpetIdleState.Resting,state.Update(true,false,true,Vector3.Zero,0,32,17));
    }

}
