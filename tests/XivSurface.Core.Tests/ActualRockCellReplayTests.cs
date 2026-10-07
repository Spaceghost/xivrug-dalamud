using System.Numerics;
using System.Reflection;
using System.Collections.Immutable;
using XivSurface.Core;

namespace XivSurface.Core.Tests;

/// <summary>Five-face numeric reduction of the captured local topology. The
/// full87-face live reports remain audit artifacts, not packaged game data.</summary>
public sealed class ActualRockCellReplayTests
{
    private static ClothCellReplay Query()
    {
        // Original faces32,46,14,15,25: two flat neighbors and an outside-cell
        // ramp chain down to the missing corner's lower patch. Every portal is
        // their actual shared edge, not a fabricated connection through space.
        LayerTriangle[] faces=[
            T(new(-60.652245f,18.60022f,-11.209211f),new(-60.869545f,18.60022f,-10.845806f),new(-59.979073f,18.600334f,-9.949728f)),
            T(new(-59.979073f,18.600334f,-9.949728f),new(-60.869545f,18.60022f,-10.845806f),new(-60.902954f,18.600334f,-9.567053f)),
            T(new(-60.652245f,18.60022f,-11.209211f),new(-60.52952f,19.229284f,-12.163473f),new(-60.869545f,18.60022f,-10.845806f)),
            T(new(-59.75699f,18.638187f,-11.521078f),new(-60.52952f,19.229284f,-12.163473f),new(-60.652245f,18.60022f,-11.209211f)),
            T(new(-60.41322f,18.212801f,-10.761953f),new(-59.75699f,18.638187f,-11.521078f),new(-60.652245f,18.60022f,-11.209211f))];
        var portals=ImmutableArray.CreateBuilder<FloorReplayPortal>();
        Edge(0,1,faces[0].B,faces[0].C);Edge(0,2,faces[0].A,faces[0].B);
        Edge(2,3,faces[2].A,faces[2].B);Edge(3,4,faces[3].A,faces[3].C);
        var center=new LayerFloorHit(new(-60.6f,18.600254f,-10.6f),faces[0]);
        var state=new FloorReplayState(1,0,faces[0],center.Position,
            faces.Select(t=>new FloorReplayFace(t,0,true)).ToImmutableArray(),portals.ToImmutable());
        return new(state,center,new(-60.8f,18.600227f,-10.8f),new(-60.4f,18.238422f,-10.8f),
            new(-60.8f,18.600262f,-10.400001f),new(-60.4f,18.600279f,-10.400001f),
            LayerQueryResult.Unknown,"Uncovered footprint",new(-60.41118f,-10.779091f))
            {DiscoveryResult=LayerQueryResult.Success,AttemptResult=LayerQueryResult.Pending};
        void Edge(int from,int to,Vector3 a,Vector3 b)
        { portals.Add(new(from,to,[],a,b,a,b));portals.Add(new(to,from,[],a,b,a,b)); }
        static LayerTriangle T(Vector3 a,Vector3 b,Vector3 c)
        { var normal=Vector3.Normalize(Vector3.Cross(b-a,c-a));return new(a,b,c,normal.Y<0?-normal:normal); }
    }
    private static LocalFloorLayer Restore(ClothCellReplay q) => (LocalFloorLayer)typeof(LocalFloorLayer)
        .GetMethod("RestoreReplay",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[q.Layer])!;
    [Fact] public void CapturedMissingAreaIsRealAndUnlimitedTimeDoesNotResolveIt()
    {
        var q=Query();var layer=Restore(q);
        Assert.Equal(LayerQueryResult.Unknown,layer.TryCellCeiling(q.Center,q.A,q.B,q.C,q.D,out _));
        var helper=(ClothCellCeiling)typeof(LocalFloorLayer).GetField("cellCeiling",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(layer)!;
        Assert.Equal("Uncovered footprint",helper.LastFailure);
        Assert.InRange(helper.MissingArea,.0010515,.0010516);
        Assert.Equal(q.MissingWitness,helper.MissingWitness);Assert.Equal(13,helper.LastCornerMask);
    }
    [Fact] public void WitnessIsAlreadyKnownOnLowerPatchWithoutMeasuredLocalRiser()
    {
        var q=Query();var at=q.MissingWitness!.Value;var found=q.Layer.Faces.Where(f=>
            f.WalkableGraph&&f.Triangle.TryHeight(at,out var y)&&f.Triangle.Contains(new(at.X,y,at.Y))).ToArray();
        Assert.Single(found);Assert.True(found[0].Triangle.TryHeight(at,out var height));
        Assert.InRange(height,18.2251f,18.2253f);
        Assert.InRange(q.Center.Position.Y-height,.374f,.376f);
        Assert.All(q.Layer.Portals,e=>Assert.Empty(e.Risers));
        Assert.All(q.Layer.Faces,f=>Assert.True(f.WalkableGraph));
    }
    [Fact] public void UsingAllGloballyConnectedFacesWouldAdmitAmbiguousLayers()
    {
        var q=Query();var helper=new ClothCellCeiling();var all=q.Layer.Faces.Select(f=>f.Triangle).ToArray();
        Assert.Equal(LayerQueryResult.Unknown,helper.Measure(q.Center,q.A,q.B,q.C,q.D,all,out _));
        Assert.Equal("Overlapping floors have different heights",helper.LastFailure);
        Assert.InRange(helper.LastOverlapDelta,.3874,.3876);
    }
    [Fact] public void RepeatKnownSuccessStopsWithoutChangingCapturedCoverageResult()
    {
        var q=Query();var progress=new ClothWitnessProgress();var queryCalls=0;var attempts=0;
        var stamp=Restore(q).DiscoveryStamp;
        for(;attempts<4;attempts++)
        {
            var result=LocalFloorLayer.Replay(q);Assert.Equal(LayerQueryResult.Unknown,result.Result);
            var at=result.MissingWitness!.Value;
            if(!progress.CanDiscover(at,stamp))break;
            // Captured native follow-up was Success on this already-known face.
            Assert.Equal(LayerQueryResult.Success,q.DiscoveryResult);queryCalls++;
            progress.Record(at,q.DiscoveryResult!.Value,stamp,stamp);
        }
        Assert.Equal(1,queryCalls);Assert.Equal(1,attempts);
        Assert.Equal(LayerQueryResult.Unknown,LocalFloorLayer.Replay(q).Result);
    }
}
