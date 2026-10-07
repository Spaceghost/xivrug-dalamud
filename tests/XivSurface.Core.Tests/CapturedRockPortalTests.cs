using System.Numerics;
using System.Text;
using System.Collections.Immutable;
using XivSurface.Core;

namespace XivSurface.Core.Tests;

public sealed class CapturedRockPortalTests
{
    private static ClothPathReplay Read(string json) => ClothCollisionReportFile.Decode(Encoding.UTF8.GetBytes(json)).PathUnknown!;

    [Fact]
    public void ActualSharedRockPortalPublishesOrderedPointsOnTheUnchangedMeasuredFaces()
    {
        var query=Read(CapturedRockPathFixtures.PortalRounding);
        Assert.Equal(6,query.Layer.Faces.Length); Assert.Equal(10,query.Layer.Portals.Length);
        Assert.Equal("portal point does not match its floor",query.Reason);
        var result=LocalFloorLayer.Replay(query);
        Assert.Equal(LayerQueryResult.Success,result.Result);
        Assert.Equal(query.From,result.Path[0]); Assert.Equal(query.To,result.Path[^1]);
        var previous=-1d; var dx=(double)query.To.X-query.From.X; var dz=(double)query.To.Z-query.From.Z;
        foreach(var point in result.Path)
        {
            Assert.Contains(query.Layer.Faces,face=>face.Triangle.Contains(point));
            var t=(((double)point.X-query.From.X)*dx+((double)point.Z-query.From.Z)*dz)/(dx*dx+dz*dz);
            Assert.True(t>=previous); previous=t;
        }
        var parent=query.Layer.Faces[4].Triangle;var child=query.Layer.Faces[5].Triangle;
        var rounded=new Vector2(-60.579163f,-11.777448f);parent.TryHeight(rounded,out var y);
        Assert.False(parent.Contains(new(rounded.X,y,rounded.Y)));
        Assert.Contains(result.Path,point=>point.X==rounded.X && point.Z==MathF.BitDecrement(rounded.Y));
        Assert.Contains(result.Path,point=>Vector2.Distance(new(point.X,point.Z),rounded)<.00001f
            && parent.TryHeight(new(point.X,point.Z),out var py)&&parent.Contains(new(point.X,py,point.Z))
            && child.TryHeight(new(point.X,point.Z),out var cy)&&child.Contains(new(point.X,cy,point.Z)));
    }

    [Fact]
    public void ActualUncoveredEndpointIsNotReplacedByANearbyPortalOrPlane()
    {
        var query=Read(CapturedRockPathFixtures.MissingEndpoint);
        Assert.DoesNotContain(query.Layer.Faces,face=>face.Triangle.Contains(query.To));
        var result=LocalFloorLayer.Replay(query);
        Assert.Equal(LayerQueryResult.Unknown,result.Result); Assert.Empty(result.Path);
        Assert.Equal(new Vector2(query.To.X,query.To.Z),result.MissingWitness);
    }

    [Fact]
    public void PortalCorrectionDoesNotReplaceAWrongLayerEndpoint()
    {
        var query=Read(CapturedRockPathFixtures.PortalRounding);
        var result=LocalFloorLayer.Replay(query with {To=query.To+Vector3.UnitY*.1f});
        Assert.Equal(LayerQueryResult.Unknown,result.Result); Assert.Empty(result.Path);
    }

    [Fact]
    public void CapturedPortalStillHonorsDeadlineAndObservationExpiry()
    {
        var query=Read(CapturedRockPathFixtures.PortalRounding);
        Assert.Equal(LayerQueryResult.Pending,LocalFloorLayer.Replay(query,10).Result);
        var expired=query with {Layer=query.Layer with {Now=query.Layer.Now+2.01}};
        Assert.Equal(LayerQueryResult.Unknown,LocalFloorLayer.Replay(expired).Result);
    }

    [Fact]
    public void ReverseActualCorridorStillUsesMeasuredPortals()
    {
        var query=Read(CapturedRockPathFixtures.PortalRounding);
        var result=LocalFloorLayer.Replay(query with {From=query.To,To=query.From});
        Assert.Equal(LayerQueryResult.Success,result.Result);
        Assert.Equal(query.To,result.Path[0]); Assert.Equal(query.From,result.Path[^1]);
        Assert.All(result.Path,p=>Assert.Contains(query.Layer.Faces,f=>f.Triangle.Contains(p)));
    }

    [Fact]
    public void LargeFloatGridCannotExpandTheExistingWorldSpaceSeamTolerance()
    {
        // A power-of-two scale preserves the exact barycentric rounding defect
        // but makes neighboring floats farther apart than the .002 world cap.
        var query=Read(CapturedRockPathFixtures.PortalRounding); const float scale=1024;
        LayerTriangle Scale(LayerTriangle t)=>t with {A=t.A*scale,B=t.B*scale,C=t.C*scale};
        var state=query.Layer with {Root=Scale(query.Layer.Root),RootPoint=query.Layer.RootPoint*scale,
            Faces=query.Layer.Faces.Select(f=>f with {Triangle=Scale(f.Triangle)}).ToImmutableArray(),
            Portals=query.Layer.Portals.Select(p=>p with {ExitA=p.ExitA*scale,ExitB=p.ExitB*scale,
                EntryA=p.EntryA*scale,EntryB=p.EntryB*scale}).ToImmutableArray()};
        var result=LocalFloorLayer.Replay(query with {Layer=state,From=query.From*scale,To=query.To*scale});
        Assert.Equal(LayerQueryResult.Unknown,result.Result); Assert.Empty(result.Path);
        Assert.Equal("portal point does not match its floor",result.Reason);
    }
}
