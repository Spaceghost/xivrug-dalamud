using System.Numerics;
using XivSurface.Core;

namespace XivSurface.Core.Tests;

public sealed class CoplanarCellCoverageTests
{
    private static Vector3 Point(float x,float z,float slope=.2f,float height=0) => new(x,height+x*slope,z);
    private static LayerTriangle Triangle(Vector3 a,Vector3 b,Vector3 c)
    {
        var normal=Vector3.Normalize(Vector3.Cross(b-a,c-a)); if(normal.Y<0)normal=-normal;
        return new(a,b,c,normal);
    }
    private static LayerTriangle[] Grid(int size,float slope=.2f,float height=0,float offset=0,
        Func<int,int,bool>? include=null)
    {
        var triangles=new List<LayerTriangle>();
        for(var z=0;z<size;z++) for(var x=0;x<size;x++)
        {
            if(include?.Invoke(x,z)==false)continue;
            var a=Point(offset+(float)x/size,offset+(float)z/size,slope,height);
            var b=Point(offset+(float)(x+1)/size,offset+(float)z/size,slope,height);
            var c=Point(offset+(float)x/size,offset+(float)(z+1)/size,slope,height);
            var d=Point(offset+(float)(x+1)/size,offset+(float)(z+1)/size,slope,height);
            triangles.Add(Triangle(a,b,c)); triangles.Add(Triangle(b,d,c));
        }
        return triangles.ToArray();
    }
    private static LayerFloorHit Center(IReadOnlyList<LayerTriangle> faces,float slope=.2f,float height=0,float offset=0)
    {
        var at=Point(offset+.5f,offset+.5f,slope,height);
        return new(at,faces.First(t=>t.Contains(at)));
    }
    private static bool Prove(CoplanarCellCoverage proof,LayerFloorHit center,IReadOnlyList<LayerTriangle> faces,
        float slope=.2f,float height=0,float offset=0,Func<bool>? deadline=null) => proof.Proves(center,
            Point(offset,offset,slope,height),Point(offset+1,offset,slope,height),
            Point(offset,offset+1,slope,height),Point(offset+1,offset+1,slope,height),faces,deadline);

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(4)] [InlineData(8)] [InlineData(11)]
    public void DenseActualCoplanarRampSpanningManySeamsIsCovered(int density)
    {
        var faces=Grid(density); var center=Center(faces); var proof=new CoplanarCellCoverage();
        Assert.False(center.ProvesPlanarCell(Point(0,0),Point(1,0),Point(0,1),Point(1,1)));
        Assert.True(Prove(proof,center,faces),$"ops={proof.LastOperations},pieces={proof.LastPeakPieces}");
        Assert.InRange(proof.LastOperations,1,CoplanarCellCoverage.MaximumOperations);
        Assert.InRange(proof.LastPeakPieces,1,CoplanarCellCoverage.MaximumPieces);
    }

    [Theory]
    [InlineData(0)] [InlineData(600)] [InlineData(-600)]
    public void OppositeWindingAndWorldTranslationPreserveCoverage(float offset)
    {
        var faces=Grid(4,offset:offset).Select((t,i)=>i%2==0?t:t with {B=t.C,C=t.B}).ToArray();
        Assert.True(Prove(new(),Center(faces,offset:offset),faces,offset:offset));
    }

    [Fact]
    public void DuplicateHalfCellAreaDoesNotPretendToCoverMissingHalf()
    {
        var faces=Grid(1); var duplicates=Enumerable.Repeat(faces[0],24).ToArray();
        Assert.False(Prove(new(),Center(faces),duplicates));
        Assert.True(Prove(new(),Center(faces),faces.Concat(faces).Concat(faces).ToArray()));
    }

    [Theory]
    [InlineData(.125f)] [InlineData(.001f)] [InlineData(.00001f)]
    public void PositiveAreaHoleCannotBeHiddenByOverlappingOrRepeatedFaces(float width)
    {
        var left=Rectangle(0,0,.75f-width/2,1);
        var right=Rectangle(.75f+width/2,0,1,1);
        var faces=left.Concat(right).Concat(left).Concat(right).ToArray();
        Assert.False(Prove(new(),Center(faces),faces));
    }

    [Fact]
    public void FourMeasuredCornersAndCenterDoNotProveAnInteriorHole()
    {
        var faces=Grid(8,include:(x,z)=>x!=5||z!=5);
        Assert.False(Prove(new(),Center(faces),faces));
    }

    [Fact]
    public void ExtraOverlapIsAllowedOnlyWhenTrueUnionCoversEverything()
    {
        var faces=Rectangle(0,0,.75f,1).Concat(Rectangle(.25f,0,1,1)).ToArray();
        Assert.True(Prove(new(),Center(faces),faces));
    }

    [Fact]
    public void MixedPlanesAndWrongMeasuredLayerCannotAuthorizeSlopeFlattening()
    {
        var faces=Grid(1); var wrong=Grid(1,height:.2f);
        Assert.False(Prove(new(),Center(faces),[faces[0],wrong[1]]));
        Assert.False(Prove(new(),Center(wrong,height:.2f),faces));
    }

    [Fact]
    public void MalformedMismatchedCenterAndCollapsedCellsFailClosed()
    {
        var faces=Grid(1);var center=Center(faces);var proof=new CoplanarCellCoverage();
        Assert.False(proof.Proves(center,Point(0,0),Point(1,0),Point(0,1),new(float.NaN),faces));
        Assert.False(proof.Proves(center,Point(0,0),Point(1,0),Point(0,1),Point(.2f,.2f),faces));
        Assert.False(proof.Proves(center,Point(0,0),Point(0,0),Point(1,1),Point(1,1),faces));
    }

    [Fact]
    public void StructuralAndClockBudgetsFailClosedAndNextRequestCanRecover()
    {
        var faces=Grid(8);var proof=new CoplanarCellCoverage();var center=Center(faces);
        Assert.False(Prove(proof,center,faces,deadline:()=>false));
        var checks=0;
        Assert.False(Prove(proof,center,faces,deadline:()=>++checks<8));
        Assert.False(Prove(proof,center,Enumerable.Repeat(faces[0],CoplanarCellCoverage.MaximumTriangles+1).ToArray()));
        Assert.True(Prove(proof,center,faces));
    }

    [Theory]
    [InlineData(4)] [InlineData(8)]
    public void ActualLocalLayerUnionRequiresFreshSamePlaneConnectedFaces(int density)
    {
        var faces=Grid(density);var layer=Observe(faces);var center=Center(faces);
        layer.BeginFrame(center,.1);
        Assert.True(layer.ProvesPlanarCell(center,Point(0,0),Point(1,0),Point(0,1),Point(1,1)));
        Assert.False(layer.ProvesPlanarCell(center,Point(0,0),Point(1,0),Point(0,1),Point(1,1),()=>false));
        // Only the actual root is refreshed after the witness TTL. Old
        // neighboring collision faces must not keep planar permission alive.
        Assert.False(layer.BeginFrame(center,2.2));
        Assert.False(layer.ProvesPlanarCell(center,Point(0,0),Point(1,0),Point(0,1),Point(1,1)));
    }

    [Fact]
    public void MissingLocalFaceStaysUnprovedEvenWithAllGridSamplesOnTheSamePlane()
    {
        var faces=Grid(4,include:(x,z)=>x!=3||z!=3);var layer=Observe(faces);var center=Center(faces);
        layer.BeginFrame(center,.1);
        Assert.False(layer.ProvesPlanarCell(center,Point(0,0),Point(1,0),Point(0,1),Point(1,1)));
    }

    [Fact]
    public void SeparateOverlappingDeckCannotProvideTheOtherHalfOfActualFloor()
    {
        var lower=Grid(1);var upper=Grid(1,height:.2f);var layer=Observe([lower[0]]);
        var center=Center(lower);
        Assert.False(layer.ProvesPlanarCell(center,Point(0,0),Point(1,0),Point(0,1),Point(1,1)));
        var high=Center(upper,height:.2f);
        Assert.False(layer.BeginFrame(high,.1));
        Assert.False(layer.ProvesPlanarCell(center,Point(0,0),Point(1,0),Point(0,1),Point(1,1)));
    }

    [Fact]
    public void ActualPlayerSwitchSelectsCorrectCoplanarUnionWhileBothConnectedDecksRemain()
    {
        var lower=Grid(1,slope:0);var layer=Observe(lower);
        var rampA=Triangle(new(1,0,0),new(3,.2f,0),new(1,0,1));
        var rampB=Triangle(new(3,.2f,0),new(3,.2f,1),new(1,0,1));
        var upperA=Triangle(new(0,.2f,0),new(3,.2f,0),new(0,.2f,1));
        var upperB=Triangle(new(3,.2f,0),new(3,.2f,1),new(0,.2f,1));
        foreach(var triangle in new[]{rampA,rampB,upperB,upperA})
            layer.BeginFrame(new((triangle.A+triangle.B+triangle.C)/3,triangle),.1);
        Assert.Equal(6,layer.Count);
        var low=Center(lower,slope:0);var high=Center([upperA,upperB],slope:0,height:.2f);
        Assert.False(layer.BeginFrame(low,.2));
        Assert.True(layer.ProvesPlanarCell(low,Point(0,0,0),Point(1,0,0),Point(0,1,0),Point(1,1,0)));
        Assert.False(layer.ProvesPlanarCell(high,Point(0,0,0,.2f),Point(1,0,0,.2f),Point(0,1,0,.2f),Point(1,1,0,.2f)));
        Assert.False(layer.BeginFrame(high,.3));
        Assert.True(layer.ProvesPlanarCell(high,Point(0,0,0,.2f),Point(1,0,0,.2f),Point(0,1,0,.2f),Point(1,1,0,.2f)));
        Assert.False(layer.ProvesPlanarCell(low,Point(0,0,0),Point(1,0,0),Point(0,1,0),Point(1,1,0)));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void UnionPermissionKeepsRealRampThroughEitherRenderedDiagonal(int diagonal)
    {
        var faces=Grid(8);var center=Center(faces);var layer=Observe(faces);layer.BeginFrame(center,.1);
        Vector3[] contacts=[Point(0,0),Point(1,0),Point(0,1),Point(1,1)];
        var proven=layer.ProvesPlanarCell(center,contacts[0],contacts[1],contacts[2],contacts[3]);Assert.True(proven);
        var mesh=ClothSurface.Build(new(.5f,.5f),new(.5f,.5f),.1f,contacts,2,0,false,[.1f],
            diagonalParity:diagonal,planarCells:[proven]);
        var fine=SupportVisualRefinement.Refine(mesh,3);
        for(var i=0;i<fine.Positions.Length;i++)
        {
            Assert.Equal(fine.Positions[i].X*.2f,fine.GroundMinimum![i],5);
            Assert.Equal(fine.Positions[i].X*.2f+ClothSurface.Clearance,fine.Positions[i].Y,5);
        }
    }

    [Fact]
    public void RepeatedProofDoesNotReallocatePolygonScratchOrAccumulateState()
    {
        var faces=Grid(2);var center=Center(faces);var proof=new CoplanarCellCoverage();
        for(var i=0;i<32;i++)Assert.True(Prove(proof,center,faces));
        var before=GC.GetAllocatedBytesForCurrentThread();
        for(var i=0;i<100;i++)Assert.True(Prove(proof,center,faces));
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread()-before,0,8192); // framework/assert incidental only
    }

    private static LocalFloorLayer Observe(LayerTriangle[] faces)
    {
        var layer=new LocalFloorLayer();
        for(var i=0;i<faces.Length;i++)
        {
            var hit=new LayerFloorHit((faces[i].A+faces[i].B+faces[i].C)/3,faces[i]);
            if(i==0)Assert.True(layer.Seed(hit));else layer.BeginFrame(hit,.01);
        }
        Assert.Equal(faces.Length,layer.Count);return layer;
    }
    private static LayerTriangle[] Rectangle(float x0,float z0,float x1,float z1)
    {
        var a=Point(x0,z0);var b=Point(x1,z0);var c=Point(x0,z1);var d=Point(x1,z1);
        return [Triangle(a,b,c),Triangle(b,d,c)];
    }
}
