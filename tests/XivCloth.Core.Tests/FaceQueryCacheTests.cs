using System.Numerics;
using XivCloth.Core;

namespace XivCloth.Core.Tests;

public sealed class FaceQueryCacheTests
{
    private static MeasuredTriangleScene Scene(float offset=0,long generation=1)
    {
        var faces=new MeasuredTriangle[128];
        for(var i=0;i<faces.Length;i++)
        {
            var p=new Vector3(i%16*.2f+offset,(i%3)*.013f,i/16*.2f);
            faces[i]=new(p,p+new Vector3(0,0,.18f),p+new Vector3(.18f,0,0));
        }
        return new(generation,faces);
    }
    [Theory][InlineData(0f)][InlineData(9000f)]
    public void RepeatedExpandedShrunkMovingBoxesMatchExactUncachedCandidates(float shift)
    {
        var random=new Random(7801);var cache=new FaceQueryCache(4);var scene=Scene(shift);
        Span<int> direct=stackalloc int[128];Span<int> cached=stackalloc int[128];
        for(var i=0;i<1200;i++)
        {
            var group=i/30;var position=new Vector3(shift+(group%16)*.2f,.01f,(group/16)*.2f);
            position+=new Vector3((float)random.NextDouble()*.04f-.02f,0,(float)random.NextDouble()*.04f-.02f);
            var box=new Box3(position,position+new Vector3(.025f,.002f,.018f)).Expand(i%7*.01f);
            var a=new CollisionWork(20_000);var b=new CollisionWork(20_000);
            var ac=scene.Query(box,direct,ref a);var bc=cache.Query(i%4,scene,box,cached,ref b);
            Assert.Equal(ac,bc);Assert.True(direct[..ac].SequenceEqual(cached[..bc]));
        }
    }
    [Fact]
    public void SameGenerationDifferentSceneCannotReuseAnOldEmptyAnswer()
    {
        var cache=new FaceQueryCache(1);var before=Scene(50);var after=Scene();
        var box=new Box3(new(-.01f),new(.19f));Span<int> output=stackalloc int[128];
        var first=new CollisionWork(10_000);Assert.Equal(0,cache.Query(0,before,box,output,ref first));
        var second=new CollisionWork(10_000);Assert.True(cache.Query(0,after,box,output,ref second)>0);
        var none=new CollisionWork(0);Assert.Equal(0,cache.Query(0,MeasuredTriangleScene.Empty,box,output,ref none));
        var third=new CollisionWork(10_000);Assert.True(cache.Query(0,after,box,output,ref third)>0);
    }
    [Fact]
    public void EveryExhaustedRefillAndOutputTruncationFailsClosedThenRecovers()
    {
        var scene=Scene();var box=new Box3(new(-.01f),new(1.7f));Span<int> output=stackalloc int[128];
        Span<int> reference=stackalloc int[128];
        for(var limit=0;limit<150;limit++)
        {
            var cache=new FaceQueryCache(1);var exhausted=new CollisionWork(limit);
            Assert.Equal(-1,cache.Query(0,scene,box,output,ref exhausted));
            Assert.Equal(limit,exhausted.Used);
            var retry=new CollisionWork(10_000);var direct=new CollisionWork(10_000);
            var expected=scene.Query(box,reference,ref direct);
            Assert.Equal(expected,cache.Query(0,scene,box,output,ref retry));
            Assert.True(reference[..expected].SequenceEqual(output[..expected]));
            var shortOutput=new CollisionWork(10_000);
            Assert.Equal(-1,cache.Query(0,scene,box,Span<int>.Empty,ref shortOutput));
        }
    }
    [Fact]
    public void BeginningANewCallRechargesColdWorkRatherThanBankingFailedAttemptWarmth()
    {
        var cache=new FaceQueryCache(1);var scene=Scene();var box=new Box3(new(.04f,-.01f,.04f),new(.1f,.01f,.1f));
        Span<int> output=stackalloc int[128];
        var first=new CollisionWork(10000);Assert.Equal(1,cache.Query(0,scene,box,output,ref first));
        var warm=new CollisionWork(10000);Assert.Equal(1,cache.Query(0,scene,box,output,ref warm));
        Assert.True(warm.Used<first.Used);
        cache.BeginCall();var retry=new CollisionWork(10000);Assert.Equal(1,cache.Query(0,scene,box,output,ref retry));
        Assert.Equal(first.Used,retry.Used);
    }
    [Fact]
    public void WarmContainedQueriesAvoidTreeTraversalWithoutAllocationOrUncountedFiltering()
    {
        var cache=new FaceQueryCache(1);var scene=Scene();var box=new Box3(new(.04f,-.01f,.04f),new(.1f,.01f,.1f));
        Span<int> output=stackalloc int[128];var warm=new CollisionWork(10000);Assert.Equal(1,cache.Query(0,scene,box,output,ref warm));
        var direct=new CollisionWork(10000);Assert.Equal(1,scene.Query(box,output,ref direct));
        var zero=new CollisionWork(0);Assert.Equal(-1,cache.Query(0,scene,box,output,ref zero));
        var one=new CollisionWork(1);Assert.Equal(-1,cache.Query(0,scene,box,output,ref one));
        var used=0;var before=GC.GetAllocatedBytesForCurrentThread();
        for(var i=0;i<1000;i++)
        {
            var work=new CollisionWork(10000);
            if(cache.Query(0,scene,box,output,ref work)!=1)throw new InvalidOperationException();
            used+=work.Used;
        }
        var allocated=GC.GetAllocatedBytesForCurrentThread()-before;
        Assert.Equal(0,allocated);Assert.Equal(2000,used);Assert.True(used/1000<direct.Used);
    }
}
