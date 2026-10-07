using System.Numerics;
using XivCloth.Core;

namespace XivCloth.Core.Tests;

public sealed class PrismBoundsTests
{
    private static MeasuredTriangle Floor(float y=0) => new(new(-1,y,-1),new(0,y,1),new(1,y,-1));

    [Fact]
    public void InfiniteBlockedSideCannotBePrunedByFiniteHeightBounds()
    {
        var below=new Box3(new(-.1f,-100,-.1f),new(.1f,-99,.1f));
        Assert.False(PrismBounds.ProvesClear(below,Floor(),.002f));
        var work=new CollisionWork(1000);
        Assert.Equal(SweepVerdict.Unproven,TriangleContacts.OneSidedClear(
            [new(-.1f,-100,-.1f),new(0,-100,.1f),new(.1f,-100,-.1f)],[0,1,2],new(1,[Floor()]),.002f,ref work));
    }

    [Fact]
    public void WholeClothFreeSideAndExteriorPrismAreProvableButTouchingIsNot()
    {
        Assert.True(PrismBounds.ProvesClear(new(new(-10,1,-10),new(10,2,10)),Floor(),.002f));
        Assert.True(PrismBounds.ProvesClear(new(new(2,-100,2),new(3,100,3)),Floor(),.002f));
        Assert.False(PrismBounds.ProvesClear(new(new(-.1f,.002f,-.1f),new(.1f,.002f,.1f)),Floor(),.002f));
        Assert.False(PrismBounds.ProvesClear(new(new(-1,-.1f,-1),new(-1,0,-1)),Floor(),.002f));
    }

    [Theory]
    [InlineData(0f)][InlineData(9000f)]
    public void RotatedLayeredRandomScenesKeepFrozenOneSidedVerdicts(float offset)
    {
        var random=new Random(518924);
        for(var sample=0;sample<240;sample++)
        {
            var rotation=Quaternion.CreateFromYawPitchRoll((float)random.NextDouble()*6,
                (float)random.NextDouble()*2-1,(float)random.NextDouble()*2-1);
            var shift=new Vector3(offset,offset,-offset);
            Vector3 Transform(Vector3 p)=>Vector3.Transform(p,rotation)+shift;
            MeasuredTriangle TransformFace(MeasuredTriangle t)=>new(Transform(t.A),Transform(t.B),Transform(t.C));
            var positions=new Vector3[12];var indices=Enumerable.Range(0,12).ToArray();
            for(var f=0;f<4;f++)
            {
                var at=new Vector3((float)random.NextDouble()*6-3,(float)random.NextDouble()*2-1,(float)random.NextDouble()*6-3);
                positions[3*f]=Transform(at);positions[3*f+1]=Transform(at+new Vector3(.7f,.1f,0));
                positions[3*f+2]=Transform(at+new Vector3(0,-.1f,.8f));
            }
            var faces=new MeasuredTriangle[8];
            for(var i=0;i<faces.Length;i++)
            {
                var origin=new Vector3((float)random.NextDouble()*6-3,(float)random.NextDouble()*3-1,(float)random.NextDouble()*6-3);
                faces[i]=TransformFace(new(origin,origin+new Vector3(0,0,.9f),origin+new Vector3(.8f,0,0)));
            }
            var scene=new MeasuredTriangleScene(1,faces);
            var oldWork=new CollisionWork(100_000);var newWork=new CollisionWork(100_000);
            Assert.Equal(FrozenPrismChecks.OneSidedClear(positions,indices,scene,.002f,ref oldWork),
                TriangleContacts.OneSidedClear(positions,indices,scene,.002f,ref newWork));
        }
    }

    [Fact]
    public void BoundRefitAndEveryExclusionAreCountedAndExhaustionIsNotClear()
    {
        Vector3[] points=[new(-.1f,1,-.1f),new(0,1,.1f),new(.1f,1,-.1f)];
        var scene=new MeasuredTriangleScene(1,[Floor()]);
        for(var limit=0;limit<4;limit++)
        {
            var work=new CollisionWork(limit);
            Assert.Equal(SweepVerdict.BudgetExceeded,TriangleContacts.OneSidedClear(points,[0,1,2],scene,.002f,ref work));
            Assert.Equal(limit,work.Used);
        }
        var success=new CollisionWork(4);
        Assert.Equal(SweepVerdict.Clear,TriangleContacts.OneSidedClear(points,[0,1,2],scene,.002f,ref success));
        Assert.Equal(4,success.TreeNodes);Assert.Equal(0,success.Pairs);
    }

    [Fact]
    public void LargeClearSheetAvoidsPairScanningRatherThanIncreasingBudget()
    {
        var pattern=ClothRestPattern.Circle(2,9,height:1);
        var scene=new MeasuredTriangleScene(1,Enumerable.Repeat(Floor(),128).ToArray());
        var oldWork=new CollisionWork(20_000);var newWork=new CollisionWork(20_000);
        var positions=pattern.Positions.ToArray();var indices=pattern.Indices.ToArray();
        Assert.Equal(SweepVerdict.Clear,FrozenPrismChecks.OneSidedClear(positions,indices,scene,.002f,ref oldWork));
        Assert.Equal(SweepVerdict.Clear,TriangleContacts.OneSidedClear(positions,indices,scene,.002f,ref newWork));
        Assert.Equal(16_384,oldWork.Used);Assert.Equal(209,newWork.Used);
    }
}
