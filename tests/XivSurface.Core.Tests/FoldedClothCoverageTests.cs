using System.Numerics;

namespace XivSurface.Core.Tests;

public sealed class FoldedClothCoverageTests
{
    [Theory]
    [InlineData(0)] // concave
    [InlineData(1)] // bow tie
    [InlineData(2)] // one collinear triangle
    [InlineData(3)] // nearly collapsed at actual game-scale coordinates
    [InlineData(4)] // duplicate corner but nonzero remaining footprint
    public void FoldedMeasuredSlopeKeepsEveryMaterialTriangleAndItsOriginalXZ(int shape)
    {
        var corners=Shape(shape);
        var faces=Floor();var center=Hit(corners,faces);var helper=new ClothCellCeiling();
        Assert.Equal(LayerQueryResult.Success,helper.Measure(center,corners[0],corners[1],corners[2],corners[3],faces,out var ceiling));
        Assert.InRange(helper.LastLift,0,.00003f);
        var planar=new CoplanarCellCoverage();
        Assert.True(planar.Proves(center,corners[0],corners[1],corners[2],corners[3],faces));
        for(var parity=0;parity<2;parity++)
        {
            var mesh=ClothSurface.Build(new(-60.5f,-10.5f),new(.5f),center.Position.Y,corners,2,0,false,[ceiling],
                diagonalParity:parity,cellLifts:[helper.LastLift]);
            Assert.Equal(6,mesh.Indices.Length);
            Assert.Equal(new[]{new Vector2(0,0),new(1,0),new(0,1),new(1,1)},mesh.UV);
            for(var i=0;i<4;i++)Assert.Equal(new Vector2(corners[i].X,corners[i].Z),new(mesh.Positions[i].X,mesh.Positions[i].Z));
            for(var triangle=0;triangle<6;triangle+=3)
            for(var u=0;u<=8;u++)for(var v=0;v<=8-u;v++)
            {
                var point=mesh.Positions[mesh.Indices[triangle]]*(1-(u+v)/8f)
                    +mesh.Positions[mesh.Indices[triangle+1]]*(u/8f)+mesh.Positions[mesh.Indices[triangle+2]]*(v/8f);
                Assert.True(point.Y>=Height(point.X,point.Z)+ClothSurface.Clearance-.00001f);
                Assert.InRange(point.X,-61.00001f,-59.99999f); // wall at X=-60 remains uncrossed
            }
        }
    }

    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void FoldedCornerSamplesCannotAuthorizeUnknownInteriorOrAnotherStorey(int shape)
    {
        var corners=Shape(shape);var centerPoint=corners.Aggregate(Vector3.Zero,(sum,p)=>sum+p)/4;
        var patches=new List<LayerTriangle>();
        foreach(var p in corners.Append(centerPoint))AddRectangle(patches,p.X-.012f,p.Z-.012f,p.X+.012f,p.Z+.012f);
        var helper=new ClothCellCeiling();var center=Hit(corners,patches);
        Assert.Equal(LayerQueryResult.Unknown,helper.Measure(center,corners[0],corners[1],corners[2],corners[3],patches,out _));
        Assert.NotNull(helper.MissingWitness);Assert.True(helper.MissingArea>0);
        Assert.True(float.IsNaN(helper.LastLift));
        var full=Floor();var overlapping=full.Concat(full.Select(t=>t with{A=t.A+Vector3.UnitY*.3f,B=t.B+Vector3.UnitY*.3f,C=t.C+Vector3.UnitY*.3f})).ToArray();
        Assert.Equal(LayerQueryResult.Unknown,helper.Measure(Hit(corners,full),corners[0],corners[1],corners[2],corners[3],overlapping,out _));
        Assert.Contains("Overlapping",helper.LastFailure);
    }

    [Fact]
    public void EntirelyCollapsedMaterialNeedsSeparateLineEvidence()
    {
        var a=P(-61,-11);var b=P(-60.8f,-11);var c=P(-60.4f,-11);var d=P(-60,-11);
        var corners=new[]{a,b,c,d};var faces=Floor();var helper=new ClothCellCeiling();
        Assert.Equal(LayerQueryResult.Unknown,helper.Measure(Hit(corners,faces),a,b,c,d,faces,out _));
        Assert.False(ClothCellCeiling.PortalIntersectsCell(P(-61,-12),P(-60,-10),a,b,c,d));
    }

    private static Vector3[] Shape(int shape)=>shape switch
    {
        0=>[P(-61,-11),P(-60,-11),P(-61,-10),P(-60.8f,-10.8f)],
        1=>[P(-61,-11),P(-60,-10),P(-61,-10),P(-60,-11)],
        2=>[P(-61,-11),P(-60,-11),P(-61,-10),P(-60.5f,-10.5f)],
        3=>[P(-60.00004f,-11),P(-60,-11),P(-60.00002f,-10),P(-60.00003f,-10.5f)],
        _=>[P(-61,-11),P(-61,-11),P(-61,-10),P(-60,-10)],
    };
    private static float Height(float x,float z)=>18+.35f*(x+61)+.21f*(z+11);
    private static Vector3 P(float x,float z)=>new(x,Height(x,z),z);
    private static LayerTriangle[] Floor(){var faces=new List<LayerTriangle>();AddRectangle(faces,-62,-12,-59,-9);return faces.ToArray();}
    private static void AddRectangle(List<LayerTriangle> faces,float x0,float z0,float x1,float z1)
    { faces.Add(Tri(P(x0,z0),P(x1,z0),P(x1,z1)));faces.Add(Tri(P(x0,z0),P(x1,z1),P(x0,z1))); }
    private static LayerTriangle Tri(Vector3 a,Vector3 b,Vector3 c)
    {var n=Vector3.Normalize(Vector3.Cross(b-a,c-a));if(n.Y<0)n=-n;return new(a,b,c,n);}
    private static LayerFloorHit Hit(Vector3[] corners,IEnumerable<LayerTriangle> faces)
    {var p=corners.Aggregate(Vector3.Zero,(sum,p)=>sum+p)/4;return new(p,faces.First(t=>t.Contains(p)));}
}
