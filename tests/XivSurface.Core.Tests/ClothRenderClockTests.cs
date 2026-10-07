namespace XivSurface.Core.Tests;

public sealed class ClothRenderClockTests
{
    [Theory]
    [InlineData(2)] [InlineData(1.7)]
    public void BothOscillatorsRemainContinuousAcrossCommonPeriodAndOld256SecondSnap(double frequency)
    {
        foreach(var boundary in new[]{ClothRenderClock.CommonPeriodSeconds,256,1024,ClothRenderClock.CommonPeriodSeconds*1000})
        {
            var before=Math.Sin(ClothRenderClock.Phase(boundary-.00001)*frequency+.37);
            var after=Math.Sin(ClothRenderClock.Phase(boundary+.00001)*frequency+.37);
            Assert.InRange(Math.Abs(after-before),0,.0001);
        }
    }

    [Theory]
    [InlineData(2)] [InlineData(1.7)]
    public void LongUptimeStillAdvancesEachRenderFrameWithoutEarlyFloatQuantization(double frequency)
    {
        const double now=1_000_000_000.123,dt=1d/120;
        var first=ClothRenderClock.Phase(now);var next=ClothRenderClock.Phase(now+dt);
        Assert.NotEqual(first,next);
        Assert.InRange(Math.Abs(Math.Sin(next*frequency)-Math.Sin((first+dt)*frequency)),0,.00002);
        Assert.InRange(first,0,(float)ClothRenderClock.CommonPeriodSeconds);
    }

    [Fact]
    public void InvalidOrNegativeClocksAreQuietAndZeroIsStable()
    {
        foreach(var seconds in new[]{double.NaN,double.PositiveInfinity,double.NegativeInfinity,-1,0})
            Assert.Equal(0,ClothRenderClock.Phase(seconds));
    }
}
