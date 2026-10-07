using System.Numerics;
using XivSurface.Core;

namespace XivSurface.Core.Tests;

public sealed class MusicSurfaceAnchorTests
{
    private static ClothMesh Mesh(bool main=true)=>new([new(0,0,0),new(1,0,0),new(0,0,1),new(1,1,1)],
        [Vector3.UnitY,Vector3.UnitY,Vector3.UnitY,Vector3.UnitY], [new(0,0),new(1,0),new(0,1),new(1,1)],
        main?[0,3,1,0,2,3]:[0,2,1,1,2,3],2);
    private static MusicVertex[] Local()=>Enumerable.Repeat(new MusicVertex(new(.6f,.2f,-.4f),Vector4.One),3).ToArray();

    [Theory] [InlineData(true,.5f)] [InlineData(false,.3f)]
    public void UsesActualDiagonalNotBilinearHeight(bool main,float expectedY)
    {
        var mesh=Mesh(main);var output=new MusicVertex[3];
        Assert.True(MusicSurfaceAnchor.TryGround(mesh,mesh.Positions,Vector2.One,Local(),output));
        Assert.All(output,v=>{Assert.Equal(.8f,v.Position.X,6);Assert.Equal(.3f,v.Position.Z,6);Assert.Equal(expectedY,v.Position.Y,6);});
    }
    [Fact] public void UsesFinalPreparedWorldPositionsIncludingCompressionAndContactProjection()
    {
        var mesh=Mesh();var positions=mesh.Positions.Select(p=>new Vector3(10+p.X*.5f,p.Y+3,20+p.Z)).ToArray();var output=new MusicVertex[3];
        Assert.True(MusicSurfaceAnchor.TryGround(mesh,positions,Vector2.One,Local(),output));
        Assert.Equal(new Vector3(10.4f,3.5f,20.3f),output[0].Position);
        Assert.Equal(Vector3.Zero,mesh.Positions[0]);
    }
    [Fact] public void MaterialUvRemappingDoesNotAssumeGridZeroToOne()
    {
        var mesh=Mesh();for(var i=0;i<4;i++)mesh.UV[i]=mesh.UV[i]*2-new Vector2(.5f);
        var output=new MusicVertex[3];Assert.True(MusicSurfaceAnchor.TryGround(mesh,mesh.Positions,Vector2.One,Local(),output));
        Assert.Equal(.65f,output[0].Position.X,6);Assert.Equal(.4f,output[0].Position.Z,6);Assert.Equal(.6f,output[0].Position.Y,6);
    }
    [Fact] public void UnknownMaterialCoverageMalformedTopologyAndAliasLeaveOutputUnchanged()
    {
        var sentinel=new MusicVertex(new(99,99,99),Vector4.One);var output=Enumerable.Repeat(sentinel,3).ToArray();var mesh=Mesh();
        var unsupported=Local();unsupported[2]=unsupported[2] with {Position=new(3,1,0)};
        Assert.False(MusicSurfaceAnchor.TryGround(mesh,mesh.Positions,Vector2.One,unsupported,output));Assert.All(output,v=>Assert.Equal(sentinel,v));
        mesh.Indices[5]=90;Assert.False(MusicSurfaceAnchor.TryGround(mesh,mesh.Positions,Vector2.One,Local(),output));Assert.All(output,v=>Assert.Equal(sentinel,v));
        mesh=Mesh();var local=Local();Assert.False(MusicSurfaceAnchor.TryGround(mesh,mesh.Positions,Vector2.One,local,local));
    }
    [Fact] public void ExistingFineSubdivisionAndRemapGroundsEveryModeWithoutChangingBounds()
    {
        var mesh=SupportVisualRefinement.Refine(Mesh(),3);SupportVisualRefinement.MapMaterial(mesh,Vector2.Zero,Vector2.One,Vector2.Zero,Vector2.One);
        var local=new MusicVertex[2304];var world=new MusicVertex[2304];var bands=Enumerable.Repeat(.2f,32).ToArray();
        foreach(var mode in Enum.GetValues<MusicVisualizationMode>())
        {
            var count=MusicVisualization.Build(local,bands,mode,MusicRadialDirection.CenterOut,.9f,.7f,.5,1);
            Assert.True(MusicSurfaceAnchor.TryGround(mesh,mesh.Positions,Vector2.One,local.AsSpan(0,count),world));
            for(var i=0;i<count;i++){Assert.InRange(world[i].Position.X,0,1);Assert.InRange(world[i].Position.Z,0,1);Assert.True(world[i].Position.Y>=local[i].Position.Y);}
        }
    }
    [Fact] public void GroundingHasZeroWarmAllocation()
    {
        var mesh=SupportVisualRefinement.Refine(Mesh(),4);var local=new MusicVertex[2304];var world=new MusicVertex[2304];
        var count=MusicVisualization.Build(local,Enumerable.Repeat(.2f,32).ToArray(),MusicVisualizationMode.RadialRibbons,MusicRadialDirection.RimIn,.9f,.7f,.5,1);
        for(var i=0;i<10;i++)MusicSurfaceAnchor.TryGround(mesh,mesh.Positions,Vector2.One,local.AsSpan(0,count),world);
        var before=GC.GetAllocatedBytesForCurrentThread();var all=true;
        for(var i=0;i<50;i++)all&=MusicSurfaceAnchor.TryGround(mesh,mesh.Positions,Vector2.One,local.AsSpan(0,count),world);
        var allocated=GC.GetAllocatedBytesForCurrentThread()-before;Assert.True(all);Assert.Equal(0,allocated);
    }
}
