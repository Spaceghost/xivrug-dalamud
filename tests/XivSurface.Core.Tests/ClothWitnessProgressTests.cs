using System.Numerics;
using XivSurface.Core;

namespace XivSurface.Core.Tests;

public sealed class ClothWitnessProgressTests
{
    [Fact] public void SameSuccessfulWitnessIsNotQueriedAgainInOneLoop()
    {
        var progress=new ClothWitnessProgress();var at=new Vector2(-60.41118f,-10.779091f);
        Assert.True(progress.CanDiscover(at,default));progress.Record(at,LayerQueryResult.Success,default,default);
        Assert.False(progress.CanDiscover(at,default));
    }
    [Fact] public void ANewWitnessRetainsDiscoveryWithoutInventingCoverage()
    {
        var progress=new ClothWitnessProgress();progress.Record(Vector2.Zero,LayerQueryResult.Success,default,default);
        Assert.True(progress.CanDiscover(new(.001f,0),default));
        progress.Record(new(.001f,0),LayerQueryResult.Success,default,default);
        Assert.False(progress.CanDiscover(new(.001f,0),default));
    }
    [Fact] public void PendingDiscoveryIsNotRecordedAsCompleted()
    {
        var progress=new ClothWitnessProgress();progress.Record(Vector2.Zero,LayerQueryResult.Pending,default,default);
        Assert.True(progress.CanDiscover(Vector2.Zero,default));
    }
    [Fact] public void FreshUpdateHasNoPermanentBackoffOrRetainedEvidence()
    {
        var first=new ClothWitnessProgress();first.Record(Vector2.Zero,LayerQueryResult.Success,default,default);
        Assert.False(first.CanDiscover(Vector2.Zero,default));
        Assert.True(new ClothWitnessProgress().CanDiscover(Vector2.Zero,default));
    }
    [Fact] public void InvalidWitnessNeverQueries()
    {
        Assert.False(new ClothWitnessProgress().CanDiscover(new(float.NaN,0),default));
        Assert.False(new ClothWitnessProgress().CanDiscover(new(0,float.PositiveInfinity),default));
    }
    [Fact] public void ChangedMeasuredGraphAllowsTheSameWitnessToContinue()
    {
        var progress=new ClothWitnessProgress();var before=new ClothDiscoveryStamp(5,0,8);var after=new ClothDiscoveryStamp(6,0,10);
        progress.Record(Vector2.Zero,LayerQueryResult.Success,before,after);
        Assert.True(progress.CanDiscover(Vector2.Zero,after));
        progress.Record(Vector2.Zero,LayerQueryResult.Success,after,after);
        Assert.False(progress.CanDiscover(Vector2.Zero,after));
        Assert.True(progress.CanDiscover(Vector2.Zero,new(6,2,10)));
    }
}
