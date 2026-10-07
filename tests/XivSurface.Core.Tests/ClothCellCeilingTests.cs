using System.Numerics;

namespace XivSurface.Core.Tests;

public sealed class ClothCellCeilingTests
{
    private static readonly Vector3 A = new(0,0,0), B = new(1,0,0), C = new(0,0,1), D = new(1,0,1);
    private static LayerTriangle Tri(Vector3 a,Vector3 b,Vector3 c)
    {
        var normal=Vector3.Normalize(Vector3.Cross(b-a,c-a));
        if(normal.Y<0)normal=-normal;
        return new(a,b,c,normal);
    }
    private static LayerFloorHit Center(IEnumerable<LayerTriangle> triangles)
    {
        var at=new Vector3(.5f,0,.5f);
        return new(at,triangles.First(t=>t.Contains(at)));
    }

    [Fact]
    public void ActualOffCenterPeakRaisesTheClothDespiteAllFivePointSamplesMissingIt()
    {
        var triangles=Peak();var center=Center(triangles);var helper=new ClothCellCeiling();
        Assert.Equal(0,center.Position.Y);
        Assert.All(new[]{A,B,C,D},corner=>Assert.Contains(triangles,t=>t.Contains(corner)));
        var result=helper.Measure(center,A,B,C,D,triangles,out var height);
        Assert.True(result==LayerQueryResult.Success,$"{helper.LastFailure}; faces={helper.LastFaceCount}, corners={helper.LastCornerMask}, overlap={helper.LastOverlapDelta}, witness={helper.MissingWitness}, area={helper.MissingArea:R}");
        Assert.Equal(.1f,height,6);
        Assert.Equal(.1f,helper.LastLift,6);
        var old=ClothSurface.Build(new(.5f),new(.5f),0,new[]{A,B,C,D},2,0,false,[0]);
        Assert.All(old.Positions,p=>Assert.True(p.Y<.1f));
        var measured=ClothSurface.Build(new(.5f),new(.5f),0,new[]{A,B,C,D},2,0,false,[height]);
        Assert.All(measured.Positions,p=>Assert.True(p.Y>=.1f+ClothSurface.Clearance));
        Assert.InRange(helper.LastOperations,1,ClothCellCeiling.MaximumOperations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClippingUsesOnlyHeightInsideTheCellNotADistantHighTriangleVertex(bool reversed)
    {
        var triangle=Tri(new(-10,-10,-10),new(10,10,-10),new(0,0,10));
        if(reversed)triangle=triangle with{B=triangle.C,C=triangle.B};
        var helper=new ClothCellCeiling();var center=new LayerFloorHit(new(.5f,.5f,.5f),triangle);
        Assert.Equal(LayerQueryResult.Success,helper.Measure(center,A,B with{Y=1},C,D with{Y=1},[triangle],out var height));
        Assert.Equal(1,height,6); Assert.True(height<triangle.MaximumY);
        Assert.InRange(helper.LastLift,0,.000001f);
    }

    [Fact]
    public void OverlappingDifferentStoreysCannotProduceACeilingEvenIfBothCoverTheCell()
    {
        var floor=FlatFloor();var upper=floor.Select(t=>t with{A=t.A+Vector3.UnitY*.2f,B=t.B+Vector3.UnitY*.2f,C=t.C+Vector3.UnitY*.2f}).ToArray();
        var faces=floor.Concat(upper).ToArray();var helper=new ClothCellCeiling();
        Assert.Equal(LayerQueryResult.Unknown,helper.Measure(Center(floor),A,B,C,D,faces,out _));
        Assert.Null(helper.MissingWitness); // Ambiguity is not a missing-face discovery request.
        Assert.True(float.IsNaN(helper.LastLift));
    }

    [Fact]
    public void FiveMeasuredPointsDoNotFillAnUnknownPositiveAreaHole()
    {
        var faces=new List<LayerTriangle>();
        foreach(var point in new[]{A,B,C,D,new Vector3(.5f,0,.5f)})
        {
            var low=point-new Vector3(.02f,0,.02f);var high=point+new Vector3(.02f,0,.02f);
            faces.Add(Tri(low,new(high.X,0,low.Z),high));faces.Add(Tri(low,high,new(low.X,0,high.Z)));
        }
        var helper=new ClothCellCeiling();
        Assert.Equal(LayerQueryResult.Unknown,helper.Measure(Center(faces),A,B,C,D,faces,out _));
        Assert.NotNull(helper.MissingWitness);
        var witness=helper.MissingWitness.Value;
        Assert.InRange(witness.X,0,1);Assert.InRange(witness.Y,0,1);
        Assert.DoesNotContain(faces,face=>face.Contains(new(witness.X,0,witness.Y)));
        Assert.True(helper.MissingArea>0);
    }

    [Fact]
    public void HeightOrderingThatChangesAcrossAnOverlapIsStillAmbiguous()
    {
        var floor=FlatFloor();var rising=floor.Select(t=>Tri(t.A with{Y=t.A.X*.1f-.05f},t.B with{Y=t.B.X*.1f-.05f},t.C with{Y=t.C.X*.1f-.05f})).ToArray();
        var helper=new ClothCellCeiling();
        Assert.Equal(LayerQueryResult.Unknown,helper.Measure(Center(floor),A,B,C,D,floor.Concat(rising).ToArray(),out _));
    }

    [Fact]
    public void SharedSeamsAndDuplicateSameHeightFacesAreNotFalseAmbiguities()
    {
        var floor=FlatFloor();var helper=new ClothCellCeiling();
        Assert.Equal(LayerQueryResult.Success,helper.Measure(Center(floor),A,B,C,D,floor.Concat(floor).ToArray(),out var height));
        Assert.Equal(0,height);
    }

    [Fact]
    public void WorldTranslationPreservesTheMeasuredPeakBound()
    {
        var shift=new Vector3(1000,20,-1000);
        var triangles=Peak().Select(t=>t with{A=t.A+shift,B=t.B+shift,C=t.C+shift}).ToArray();
        var old=Center(Peak());var center=new LayerFloorHit(old.Position+shift,old.Triangle with{A=old.Triangle.A+shift,B=old.Triangle.B+shift,C=old.Triangle.C+shift});
        var helper=new ClothCellCeiling();
        var result=helper.Measure(center,A+shift,B+shift,C+shift,D+shift,triangles,out var height);
        Assert.True(result==LayerQueryResult.Success,$"{helper.LastFailure}; faces={helper.LastFaceCount}, corners={helper.LastCornerMask}, overlap={helper.LastOverlapDelta}, witness={helper.MissingWitness}, area={helper.MissingArea:R}");
        Assert.Equal(20.1f,height,5);
    }

    [Fact]
    public void DeadlineAndStructuralLimitsCannotPublishAPartialMaximum()
    {
        var floor=FlatFloor();var helper=new ClothCellCeiling();
        Assert.Equal(LayerQueryResult.Pending,helper.Measure(Center(floor),A,B,C,D,floor,out _,()=>false));
        var excessive=Enumerable.Repeat(floor[0],ClothCellCeiling.MaximumTriangles+1).ToArray();
        Assert.Equal(LayerQueryResult.Unknown,helper.Measure(Center(floor),A,B,C,D,excessive,out _));
        var checks=0;
        Assert.Equal(LayerQueryResult.Pending,helper.Measure(Center(Peak()),A,B,C,D,Peak(),out _,()=>++checks<3));
        Assert.InRange(helper.LastOperations,0,ClothCellCeiling.MaximumOperations);
    }

    [Fact]
    public void DifferentLayerCornerAndMismatchedCenterCannotUseTheEnvelope()
    {
        var floor=FlatFloor();var helper=new ClothCellCeiling();
        Assert.Equal(LayerQueryResult.Unknown,helper.Measure(Center(floor),A,B with{Y=.2f},C,D,floor,out _));
        Assert.Equal(LayerQueryResult.Unknown,helper.Measure(Center(floor),A,B,C,new(.2f,0,.2f),floor,out _));
    }

    [Fact]
    public void PortalNeedsPositiveLengthInsideFootprintNotBoundaryTouchOrOutsideDetour()
    {
        Assert.True(ClothCellCeiling.PortalIntersectsCell(new(-1,0,.5f),new(2,0,.5f),A,B,C,D));
        Assert.True(ClothCellCeiling.PortalIntersectsCell(new(.25f,0,.5f),new(.75f,0,.5f),A,B,C,D));
        Assert.False(ClothCellCeiling.PortalIntersectsCell(new(0,0,-1),new(0,0,2),A,B,C,D));
        Assert.False(ClothCellCeiling.PortalIntersectsCell(new(-1,0,-1),A,A,B,C,D));
        Assert.False(ClothCellCeiling.PortalIntersectsCell(new(2,0,0),new(2,0,1),A,B,C,D));
        Assert.False(ClothCellCeiling.PortalIntersectsCell(new(.5f,0,.5f),new(.5f,1,.5f),A,B,C,D));
        Assert.False(ClothCellCeiling.PortalIntersectsCell(new(float.NaN),D,A,B,C,D));
    }

    [Fact]
    public void TessellatedActualSlopeKeepsPerCornerHeightsInsteadOfHighestCornerPlateau()
    {
        var faces=new List<LayerTriangle>();
        for(var z=0;z<4;z++)for(var x=0;x<4;x++)
        {
            var a=Point(x/4f,z/4f);var b=Point((x+1)/4f,z/4f);
            var c=Point(x/4f,(z+1)/4f);var d=Point((x+1)/4f,(z+1)/4f);
            faces.Add(Tri(a,b,c));faces.Add(Tri(b,d,c));
        }
        var at=Point(.5f,.5f);var center=new LayerFloorHit(at,faces.First(face=>face.Contains(at)));
        var helper=new ClothCellCeiling();
        Assert.Equal(LayerQueryResult.Success,helper.Measure(center,Point(0,0),Point(1,0),Point(0,1),Point(1,1),faces,out var ceiling));
        Assert.Equal(.75f,ceiling);Assert.InRange(helper.LastLift,0,.000001f);
        static Vector3 Point(float x,float z)=>new(x,x*.5f+z*.25f,z);
    }

    [Fact]
    public void ResidualBoundCoversBothActualRenderedDiagonalsAcrossACrease()
    {
        var high=D with{Y=.8f};LayerTriangle[] faces=[Tri(A,B,high),Tri(A,high,C)];
        var center=new LayerFloorHit(new(.5f,.4f,.5f),faces[0]);var helper=new ClothCellCeiling();
        Assert.Equal(LayerQueryResult.Success,helper.Measure(center,A,B,C,high,faces,out _));
        Assert.Equal(.4f,helper.LastLift,5);
        for(var z=0;z<=20;z++)for(var x=0;x<=20;x++)
        {
            var u=x/20f;var v=z/20f;var source=.8f*Math.Min(u,v);
            var otherDiagonal=.8f*Math.Max(0,u+v-1);
            Assert.True(otherDiagonal+helper.LastLift>=source-.000001f);
            Assert.True(source+helper.LastLift>=source);
        }
    }

    private static LayerTriangle[] FlatFloor()=>[Tri(A,B,D),Tri(A,D,C)];

    private static List<LayerTriangle> Peak()
    {
        float[] stops=[0,.175f,.325f,1];var triangles=new List<LayerTriangle>();
        for(var z=0;z<3;z++)for(var x=0;x<3;x++)
        {
            var a=new Vector3(stops[x],0,stops[z]);var b=new Vector3(stops[x+1],0,stops[z]);
            var c=new Vector3(stops[x],0,stops[z+1]);var d=new Vector3(stops[x+1],0,stops[z+1]);
            if(x==1&&z==1)
            {
                var peak=new Vector3(.25f,.1f,.25f);
                triangles.AddRange([Tri(a,b,peak),Tri(b,d,peak),Tri(d,c,peak),Tri(c,a,peak)]);
            }
            else triangles.AddRange([Tri(a,b,d),Tri(a,d,c)]);
        }
        return triangles;
    }
}
