using System.Numerics;
using XivCloth.Core;

namespace XivCloth.Core.Tests;

public sealed class ContactOrderTests
{
    [Fact]
    public void ShallowTreadCorrectionCanClearAnAdjacentDeepRiserInEveryEnumeration()
    {
        MeasuredTriangle[] faces=[
            new(new(.5f,.3f,-1),new(.5f,.3f,1),new(.5f,.45f,-1)),
            new(new(.5f,.45f,-1),new(.5f,.3f,1),new(.5f,.45f,1)),
            new(new(.5f,.45f,-1),new(.5f,.45f,1),new(2,.45f,-1)),
            new(new(2,.45f,-1),new(.5f,.45f,1),new(2,.45f,1))];
        foreach(var order in Permutations(faces))
        {
            var p=new Vector3(.6f,.44424173f,-.8f);var checks=0;
            var scene=new MeasuredTriangleScene(1,order);
            Assert.True(scene.Project(ref p,.004f,.05f,ref checks));
            Assert.Equal(.6f,p.X);Assert.Equal(-.8f,p.Z);Assert.InRange(p.Y,.4539999f,.4540001f);
            Assert.InRange(checks,4,8);Assert.True(scene.IsClear(p,.004f,ref checks));
        }
    }

    [Fact]
    public void TriangleInteriorCanClearAdjacentRiserAfterShallowTreadInEveryEnumeration()
    {
        MeasuredTriangle[] faces=[
            new(new(.5f,.3f,-1),new(.5f,.3f,1),new(.5f,.45f,-1)),
            new(new(.5f,.45f,-1),new(.5f,.3f,1),new(.5f,.45f,1)),
            new(new(.5f,.45f,-1),new(.5f,.45f,1),new(2,.45f,-1)),
            new(new(2,.45f,-1),new(.5f,.45f,1),new(2,.45f,1))];
        foreach(var order in Permutations(faces))
        {
            Vector3[] positions=[new(.4f,.444f,0),new(.7f,.454f,-.2f),new(.7f,.454f,.2f)];
            var previous=positions.Select(p=>p+new Vector3(0,.03f,0)).ToArray();
            var scene=new MeasuredTriangleScene(1,order);var work=new CollisionWork(10_000);
            Assert.True(TriangleContacts.Project(positions,previous,[0,1,2],[1,1,1],scene,.004f,ref work));
            Assert.Equal(SweepVerdict.Clear,TriangleContacts.OneSidedClear(positions,[0,1,2],scene,.002f,ref work));
            Assert.Equal(SweepVerdict.Clear,TriangleContacts.Sweep(previous,positions,[0,1,2],scene,.002f,ref work));
        }
    }

    [Fact]
    public void MultiPlaneWallCornerKeepsShallowCorrectionsForEveryEnumeration()
    {
        MeasuredTriangle[] faces=[
            new(new(0,-1,-1),new(0,1,-1),new(0,0,1)),
            new(new(-1,-1,0),new(1,-1,0),new(0,1,0)),
            new(new(-1,0,-1),new(0,0,1),new(1,0,-1))];
        foreach(var order in Permutations(faces))
        {
            var p=new Vector3(.002f,-.002f,.002f);var checks=0;var scene=new MeasuredTriangleScene(1,order);
            Assert.True(scene.Project(ref p,.004f,.05f,ref checks));
            Assert.InRange(Vector3.Distance(p,new(.004f)),0,1e-7f);
            Assert.True(scene.IsClear(p,.004f,ref checks));
        }
    }

    [Fact]
    public void GenuinelyDeepContactStillRefusesAfterUnrelatedShallowCorrection()
    {
        MeasuredTriangle[] faces=[
            new(new(0,-1,-1),new(0,1,-1),new(0,0,1)),
            new(new(-1,0,-1),new(0,0,1),new(1,0,-1))];
        foreach(var order in Permutations(faces))
        {
            var p=new Vector3(-.1f,-.002f,0);var checks=0;var scene=new MeasuredTriangleScene(1,order);
            Assert.False(scene.Project(ref p,.004f,.05f,ref checks));
            Assert.Equal(-.1f,p.X);Assert.Equal(0,p.Z); // no deep depenetration
            Assert.InRange(checks,3,4);
        }
    }

    [Fact]
    public void GenuinelyBuriedWholeFaceStillRefusesWithoutDeepDepenetration()
    {
        Vector3[] positions=[new(-.2f,-.1f,-.2f),new(0,.1f,.2f),new(.2f,-.1f,-.2f)];
        var original=positions.ToArray();var previous=positions.Select(p=>p+Vector3.UnitY).ToArray();
        var scene=new MeasuredTriangleScene(1,[new(new(-1,0,-1),new(0,0,1),new(1,0,-1))]);
        var work=new CollisionWork(1000);
        Assert.False(TriangleContacts.Project(positions,previous,[0,1,2],[1,1,1],scene,.004f,ref work));
        Assert.Equal(original,positions);
    }

    private static IEnumerable<MeasuredTriangle[]> Permutations(MeasuredTriangle[] values)
    {
        var copy=values.ToArray();return Generate(0);
        IEnumerable<MeasuredTriangle[]> Generate(int at)
        {
            if(at==copy.Length){yield return copy.ToArray();yield break;}
            for(var i=at;i<copy.Length;i++)
            {
                (copy[at],copy[i])=(copy[i],copy[at]);
                foreach(var next in Generate(at+1))yield return next;
                (copy[at],copy[i])=(copy[i],copy[at]);
            }
        }
    }
}
