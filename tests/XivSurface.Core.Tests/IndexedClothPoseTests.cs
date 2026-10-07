using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace XivSurface.Core.Tests;

public sealed class IndexedClothPoseTests
{
    private static Vector3[] Fold => [new(-1,0,0),new(0,0,0),new(0,0,1),new(0,1,0),new(0,1,1)];
    private static Vector2[] Texture => [new(0,0),new(.5f,0),new(.5f,1),new(1,0),new(1,1)];
    private static int[] Faces => [0,2,1,1,2,3,3,2,4];

    [Fact]
    public void VerticalFoldKeepsDifferentParticlesAtSameXZAndDoesNotInventHeightBounds()
    {
        var original = Fold;
        var pose = new IndexedClothPose(original,Texture,Faces);
        var upload = new IndexedClothVertex[Faces.Length];
        pose.WriteTriangleList(upload);
        Assert.Equal(original,pose.Positions.ToArray());
        Assert.Equal(pose.Positions[1].X,pose.Positions[3].X);
        Assert.Equal(pose.Positions[1].Z,pose.Positions[3].Z);
        Assert.NotEqual(pose.Positions[1].Y,pose.Positions[3].Y);
        for (var i = 0; i < upload.Length; i++)
        {
            var index = Faces[i];
            Assert.Equal(original[index],upload[i].Position);
            Assert.Equal(Texture[index],upload[i].UV);
            Assert.Equal(pose.Normals[index],upload[i].Normal);
        }
        Assert.All(pose.Normals.ToArray(),n=>Assert.InRange(n.Length(),.99999f,1.00001f));
        Assert.Equal(-Vector3.UnitX,pose.Normals[4]);
    }

    [Fact]
    public void PublicationOwnsInputsAndSubsequentSourceMutationCannotAlterUpload()
    {
        var p=Fold;var uv=Texture;var ix=Faces;
        var pose=new IndexedClothPose(p,uv,ix);
        var expected=new IndexedClothVertex[ix.Length];pose.WriteTriangleList(expected);
        Array.Fill(p,new Vector3(float.NaN));Array.Fill(uv,new Vector2(float.NaN));Array.Fill(ix,-1);
        var actual=new IndexedClothVertex[expected.Length];pose.WriteTriangleList(actual);
        Assert.Equal(expected,actual);
    }

    [Fact]
    public void OppositeWindingKeepsDownwardMaterialNormals()
    {
        var pose=new IndexedClothPose([new(0,0,0),new(1,0,0),new(0,0,1)],
            [Vector2.Zero,Vector2.UnitX,Vector2.UnitY],[0,1,2]);
        Assert.All(pose.Normals.ToArray(),n=>Assert.Equal(-Vector3.UnitY,n));
    }

    [Fact]
    public void NormalCancellationUsesIncidentFaceInsteadOfUnrelatedWorldUp()
    {
        var pose=new IndexedClothPose([new(0,0,0),new(0,1,0),new(0,0,1),new(0,1,0)],
            [Vector2.Zero,Vector2.UnitX,Vector2.UnitY,Vector2.One],[0,1,2,0,2,3]);
        Assert.Equal(Vector3.UnitX,pose.Normals[0]);
        Assert.Equal(Vector3.UnitX,pose.Normals[2]);
    }

    [Theory]
    [InlineData(-1)] [InlineData(5)] [InlineData(int.MaxValue)]
    public void InvalidIndexIsRejected(int value)
    {
        var indices=Faces;indices[0]=value;
        Assert.Throws<ArgumentException>(()=>new IndexedClothPose(Fold,Texture,indices));
    }

    [Theory]
    [InlineData(float.NaN)] [InlineData(float.PositiveInfinity)] [InlineData(10001f)]
    public void InvalidPositionIsRejected(float value)
    {
        var positions=Fold;positions[0].X=value;
        Assert.Throws<ArgumentException>(()=>new IndexedClothPose(positions,Texture,Faces));
    }

    [Fact]
    public void MalformedChartTopologyAndOutputLengthsAreRefused()
    {
        Assert.Throws<ArgumentException>(()=>new IndexedClothPose(Fold,new Vector2[2],Faces));
        var uv=Texture;uv[0].X=-.1f;
        Assert.Throws<ArgumentException>(()=>new IndexedClothPose(Fold,uv,Faces));
        Assert.Throws<ArgumentException>(()=>new IndexedClothPose(Fold,Texture,[0,1,1]));
        Assert.Throws<ArgumentException>(()=>new IndexedClothPose(Fold,Texture,[0,1,2,3]));
        Assert.Throws<ArgumentException>(()=>new IndexedClothPose(Fold,Texture,[0,2,1])); // unused particles
        var p=Fold;p[1]=p[0];
        Assert.Throws<ArgumentException>(()=>new IndexedClothPose(p,Texture,Faces));
        var pose=new IndexedClothPose(Fold,Texture,Faces);
        Assert.Throws<ArgumentException>(()=>pose.WriteTriangleList(new IndexedClothVertex[Faces.Length+1]));
        Assert.Throws<ArgumentException>(()=>pose.WriteTriangleList(new IndexedClothVertex[Faces.Length-1]));
    }

    [Fact]
    public void VertexLayoutContainsOnlyPositionNormalAndMaterialUV()
    {
        Assert.Equal(32,Marshal.SizeOf<IndexedClothVertex>());
        Assert.Equal(0,Marshal.OffsetOf<IndexedClothVertex>(nameof(IndexedClothVertex.Position)).ToInt32());
        Assert.Equal(12,Marshal.OffsetOf<IndexedClothVertex>(nameof(IndexedClothVertex.Normal)).ToInt32());
        Assert.Equal(24,Marshal.OffsetOf<IndexedClothVertex>(nameof(IndexedClothVertex.UV)).ToInt32());
    }

    [Fact]
    public void ReusedUploadBufferAllocatesNothingAndNeverInterpolatesWithEarlierPose()
    {
        var pose=new IndexedClothPose(Fold,Texture,Faces);
        var shifted=Fold.Select(p=>p+new Vector3(.3f,-.4f,.2f)).ToArray();
        var next=new IndexedClothPose(shifted,Texture,Faces);
        var buffer=new IndexedClothVertex[Faces.Length];
        for(var i=0;i<20;i++){pose.WriteTriangleList(buffer);next.WriteTriangleList(buffer);}
        var allocated=GC.GetAllocatedBytesForCurrentThread();
        for(var i=0;i<100;i++){pose.WriteTriangleList(buffer);next.WriteTriangleList(buffer);}
        Assert.Equal(0,GC.GetAllocatedBytesForCurrentThread()-allocated);
        for(var i=0;i<buffer.Length;i++)Assert.Equal(shifted[Faces[i]],buffer[i].Position);
    }

    [Fact]
    public void CompiledIndexedShaderHasNoHeightOrContactInputs()
    {
        using var stream=typeof(IndexedClothPose).Assembly.GetManifestResourceStream("XivSurface.IndexedClothVertex.dxbc");
        Assert.NotNull(stream);
        using var memory=new MemoryStream();stream.CopyTo(memory);var bytes=memory.ToArray();
        Assert.Equal("DXBC",Encoding.ASCII.GetString(bytes,0,4));
        var expected=new Dictionary<(string,int),byte>{[("POSITION",0)]=7,[("NORMAL",0)]=7,[("TEXCOORD",0)]=3};
        var found=false;
        for(var i=0;i<BitConverter.ToInt32(bytes,28);i++)
        {
            var chunk=BitConverter.ToInt32(bytes,32+i*4);
            if(Encoding.ASCII.GetString(bytes,chunk,4)!="ISGN")continue;
            var data=chunk+8;Assert.Equal(3,BitConverter.ToInt32(bytes,data));
            for(var j=0;j<3;j++)
            {
                var entry=data+8+j*24;var offset=data+BitConverter.ToInt32(bytes,entry);
                var name=Encoding.ASCII.GetString(bytes,offset,Array.IndexOf(bytes,(byte)0,offset)-offset);
                var index=BitConverter.ToInt32(bytes,entry+4);
                Assert.True(expected.Remove((name,index),out var mask));
                Assert.Equal(3,BitConverter.ToInt32(bytes,entry+12));
                Assert.Equal(mask,bytes[entry+20]);
            }
            found=true;
        }
        Assert.True(found);Assert.Empty(expected);
    }
}
