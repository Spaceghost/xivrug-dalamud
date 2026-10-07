using System.Numerics;

namespace XivSurface.Core.Collision.Tests;

public sealed class PcbDecoderTests
{
    private static PcbDecodeResult Decode(byte[] bytes, PcbCollider? collider = null, PcbQuery? query = null,
        PcbDecodeLimits? limits = null) => PcbDecoder.Decode(bytes, collider ?? PcbCollider.Identity,
            query ?? PcbQuery.GameFloor, limits ?? PcbDecodeLimits.Default);
    private static OwnedPcbMesh Complete(PcbDecodeResult result)
    { Assert.Equal(PcbDecodeStatus.Complete, result.Status); Assert.NotNull(result.Mesh); return result.Mesh; }
    private static void Refused(PcbDecodeResult result, PcbDecodeStatus status = PcbDecodeStatus.InvalidData)
    { Assert.Equal(status, result.Status); Assert.Null(result.Mesh); }

    [Theory][InlineData(1)][InlineData(4)]
    public void VersionsDecodeRawTriangleAndPreserveSourceMetadata(int version)
    {
        var bytes = Fixture.Build(Node.Triangle); Fixture.I32(bytes, 4, version);
        var mesh = Complete(Decode(bytes)); Assert.Equal(version, mesh.Version);
        Assert.Equal(1, mesh.NodeCount); Assert.Equal(1, mesh.SourcePrimitiveCount); Assert.Equal(3, mesh.SourceVertexCount);
        var t = mesh.Triangles[0]; Assert.Equal(Vector3.Zero, t.A); Assert.Equal(Vector3.UnitZ, t.B); Assert.Equal(Vector3.UnitX, t.C);
        Assert.Equal(0x4000UL, t.PrimitiveMaterial); Assert.Equal(t.PrimitiveMaterial, t.EffectiveMaterial);
        Assert.Equal(16, t.NodeOffset); Assert.Equal(0, t.PrimitiveIndex);
    }

    [Fact] public void MixedRawAndCompressedVerticesUseRawFirstAndNodeBounds()
    {
        var n = new Node([new(10, 20, 30)], [(0, 0, 65535), (65535, 32768, 0)], [new(0, 1, 2)], new(10,20,30), new(12,24,36));
        var t = Complete(Decode(Fixture.Build(n))).Triangles[0];
        Assert.Equal(new Vector3(10,20,30), t.A); Assert.Equal(new Vector3(10,20,36), t.B);
        Assert.Equal(new Vector3(12, 20 + 4 * (32768 / 65535f), 30), t.C);
    }

    [Fact] public void InternalNodesAndBothBranchesContributeGeometryWithRelativeOffsets()
    {
        var nodes = new[] { Node.Triangle with { Child1=1, Child2=2 }, Node.Triangle,
            Node.Triangle with { Child1=3 }, Node.Triangle };
        var mesh = Complete(Decode(Fixture.Build(nodes)));
        Assert.Equal(4, mesh.NodeCount); Assert.Equal(4, mesh.Triangles.Length);
        Assert.Equal(new[] {16,112,208,304}, mesh.Triangles.ToArray().Select(t=>t.NodeOffset).Order().ToArray());
    }

    [Fact] public void EmptyInternalNodeStillTraversesItsChild()
    {
        var empty = new Node([],[],[],Vector3.Zero,Vector3.One,Child1:1);
        var mesh=Complete(Decode(Fixture.Build(empty,Node.Triangle)));
        Assert.Equal(2,mesh.NodeCount); Assert.Equal(1,mesh.Triangles.Length);
    }

    [Fact] public void EntireDecodedValueIsOwnedAfterInputMutation()
    {
        var b=Fixture.Build(Node.Triangle); var mesh=Complete(Decode(b));
        Array.Fill(b,(byte)255); Assert.Equal(Vector3.UnitZ,mesh.Triangles[0].B);
    }

    [Fact] public void FullAffineTransformIncludesRotationNonuniformScaleShearAndTranslation()
    {
        var m = new Matrix4x4(2,1,0,0, 0,3,1,0, 1,0,4,0, 10,20,30,1);
        var n=Node.Triangle with {Raw=[new(1,2,3),new(2,2,3),new(1,3,4)]};
        var t=Complete(Decode(Fixture.Build(n),PcbCollider.Identity with {World=m})).Triangles[0];
        Assert.Equal(new Vector3(15,27,44),t.A); Assert.Equal(new Vector3(17,28,44),t.B); Assert.Equal(new Vector3(16,30,49),t.C);
    }

    [Fact] public void ReflectionPreservesIndicesAndReversesWorldWindingNaturally()
    {
        var t=Complete(Decode(Fixture.Build(Node.Triangle),PcbCollider.Identity with {World=Matrix4x4.CreateScale(-2,3,4)})).Triangles[0];
        Assert.Equal(new Vector3(-2,0,0),t.C); Assert.True(Vector3.Cross(t.B-t.A,t.C-t.A).Y<0);
    }

    [Fact] public void SkinnyFiniteTriangleIsNotRoundedAwayByFloatDotProduct()
    {
        var n=Node.Triangle with {Raw=[Vector3.Zero,new(1,0,.0001f),Vector3.UnitX]};
        Assert.Equal(1,Complete(Decode(Fixture.Build(n))).Triangles.Length);
    }

    [Theory][InlineData(0UL,0UL,false)][InlineData(0x4000UL,0UL,true)][InlineData(0UL,0x4000UL,false)]
    [InlineData(0x4000UL,0x4000UL,true)][InlineData(0x4001UL,0x4000UL,true)]
    public void MaterialFilterHasNativeZeroValueAnyBitSemantics(ulong material,ulong value,bool accepted)
    {
        var n=Node.Triangle with {Primitives=[new(0,1,2,material)]};
        Assert.Equal(accepted?1:0,Complete(Decode(Fixture.Build(n),query:new(1,0x4000,value))).Triangles.Length);
    }

    [Fact] public void ObjectOverrideIsAppliedBeforeFilterAndValueIsNotRemasked()
    {
        var n=Node.Triangle with {Primitives=[new(0,1,2,0x10001)]};
        var c=PcbCollider.Identity with {ObjectMaterialMask=1,ObjectMaterialValue=0x4000};
        var t=Complete(Decode(Fixture.Build(n),c)).Triangles[0]; Assert.Equal(0x14000UL,t.EffectiveMaterial);
        Refused(Decode(Fixture.Build(n),query:new(0,0x4000,0x4000)),PcbDecodeStatus.InvalidRequest);
    }

    [Fact] public void ZeroObjectMaskUsesNativeUnadjustedFastPath()
    {
        var n=Node.Triangle with {Primitives=[new(0,1,2,0)]};
        Assert.Empty(Complete(Decode(Fixture.Build(n),PcbCollider.Identity with {ObjectMaterialValue=0x4000})).Triangles.ToArray());
    }

    [Fact] public void LayerFilterRejectsNonmatchingButStillValidatesAllSource()
    {
        var b=Fixture.Build(Node.Triangle); var c=PcbCollider.Identity with {LayerMask=2};
        Assert.Empty(Complete(Decode(b,c)).Triangles.ToArray()); b[100]=255; Refused(Decode(b,c));
    }

    [Fact] public void QueryBoundsUseWorldSpaceAndIncludeTouchingTriangles()
    {
        var c=PcbCollider.Identity with {World=Matrix4x4.CreateTranslation(10,20,30)};
        var q=PcbQuery.GameFloor with {Bounds=new(new(11,20,30),new(12,21,31))};
        Assert.Equal(1,Complete(Decode(Fixture.Build(Node.Triangle),c,q)).Triangles.Length);
        q=q with {Bounds=new(new(12,20,30),new(13,21,31))};
        Assert.Empty(Complete(Decode(Fixture.Build(Node.Triangle),c,q)).Triangles.ToArray());
    }

    [Theory][InlineData(0)][InlineData(2)][InlineData(3)][InlineData(5)][InlineData(-1)]
    public void LegacyAndUnknownVersionsExplicitlyRefuse(int version)
    { var b=Fixture.Build(Node.Triangle); Fixture.I32(b,4,version); Refused(Decode(b),PcbDecodeStatus.UnsupportedVersion); }

    [Theory][InlineData(0)][InlineData(15)][InlineData(63)][InlineData(80)][InlineData(107)]
    public void TruncationNeverPublishesPartialGeometry(int length)
    { var b=Fixture.Build(Node.Triangle); Refused(Decode(b[..length])); }

    [Theory][InlineData(-1)][InlineData(1)][InlineData(40)][InlineData(47)][InlineData(int.MaxValue)]
    public void InvalidRelativeOffsetsRefuse(int offset)
    { var b=Fixture.Build(Node.Triangle,Node.Triangle); Fixture.I32(b,24,offset); Refused(Decode(b)); }

    [Fact] public void DuplicateChildrenAndDisconnectedDeclaredNodesRefuse()
    {
        Refused(Decode(Fixture.Build(Node.Triangle with {Child1=1,Child2=1},Node.Triangle)));
        Refused(Decode(Fixture.Build(Node.Triangle,Node.Triangle)));
    }

    [Fact] public void BackwardCycleRefuses()
    { Refused(Decode(Fixture.Build(Node.Triangle with {Child1=1},Node.Triangle with {Child1=0}))); }

    [Fact] public void OverlappingSiblingNodeRangesRefuseEvenIfEachIsIndividuallyReadable()
    {
        var root=new Node([],[],[],Vector3.Zero,Vector3.One,Child1:1,Child2:2);
        var b=Fixture.Build(root,new Node([],[],[],Vector3.Zero,Vector3.One),new Node([],[],[],Vector3.Zero,Vector3.One));
        // Second node begins inside the first node's bounds, yet has a readable empty header.
        Fixture.I32(b,28,64); Array.Clear(b,80,48);
        Refused(Decode(b));
    }

    [Theory][InlineData(8,-1)][InlineData(12,-1)][InlineData(12,0)][InlineData(12,2)]
    public void InvalidOrInexactDeclaredTotalsRefuse(int field,int value)
    { var b=Fixture.Build(Node.Triangle); Fixture.I32(b,field,value); Refused(Decode(b)); }

    [Fact] public void PrimitiveLimitsChargeFilteredGeometryToo()
    {
        var b=Fixture.Build(Node.Triangle); var limits=PcbDecodeLimits.Default with {Primitives=0};
        Refused(Decode(b,query:new(1,0,0),limits:limits),PcbDecodeStatus.LimitExceeded);
    }

    [Theory][InlineData(0)][InlineData(1)][InlineData(2)][InlineData(3)]
    public void ExplicitByteNodePrimitiveVertexBudgetsRefuseWithoutSnapshot(int kind)
    {
        var b=Fixture.Build(Node.Triangle with {Child1=1},Node.Triangle);
        var l=PcbDecodeLimits.Default;
        l=kind switch {0=>l with {Bytes=64},1=>l with {Nodes=1},2=>l with {Primitives=1},_=>l with {Vertices=5}};
        Refused(Decode(b,limits:l),PcbDecodeStatus.LimitExceeded);
    }

    [Fact] public void HardMaximumCannotBeOverriddenByCaller()
    { Refused(Decode(Fixture.Build(Node.Triangle),limits:PcbDecodeLimits.Maximum with {Bytes=int.MaxValue}),PcbDecodeStatus.InvalidRequest); }

    [Theory][InlineData(0)][InlineData(1)][InlineData(2)]
    public void InvalidPrimitiveIndicesRefuse(int component)
    { var b=Fixture.Build(Node.Triangle); b[100+component]=3; Refused(Decode(b)); }

    [Fact] public void MoreThan256VerticesRefusesBeforePayloadRead()
    { var b=Fixture.Build(Node.Triangle); Fixture.U16(b,60,257); Refused(Decode(b)); }

    [Fact] public void Index255IsValidAtExactVertexCapacity()
    {
        var raw=Enumerable.Repeat(Vector3.Zero,256).ToArray();raw[254]=Vector3.UnitZ;raw[255]=Vector3.UnitX;
        var n=Node.Triangle with {Raw=raw,Primitives=[new(0,254,255)]};
        Assert.Equal(1,Complete(Decode(Fixture.Build(n))).Triangles.Length);
    }

    [Theory][InlineData(float.NaN)][InlineData(float.PositiveInfinity)][InlineData(float.NegativeInfinity)]
    public void NonfiniteVerticesAndBoundsRefuse(float value)
    {
        var b=Fixture.Build(Node.Triangle); Fixture.F32(b,64,value); Refused(Decode(b));
        b=Fixture.Build(Node.Triangle); Fixture.F32(b,32,value); Refused(Decode(b));
    }

    [Fact] public void InvertedBoundsAndDegenerateFacesRefuse()
    {
        Refused(Decode(Fixture.Build(Node.Triangle with {Min=new(2,0,0)})));
        Refused(Decode(Fixture.Build(Node.Triangle with {Raw=[Vector3.Zero,Vector3.UnitX,Vector3.UnitX*2]})));
    }

    [Theory][InlineData(0)][InlineData(1)][InlineData(2)][InlineData(3)]
    public void InvalidTransformsOrWorldOverflowRefuse(int variant)
    {
        var m=Matrix4x4.Identity;
        switch(variant){case 0:m.M14=.1f;break;case 1:m.M22=0;break;case 2:m.M41=float.NaN;break;case 3:m.M11=float.MaxValue;break;}
        var n=Node.Triangle with {Raw=[Vector3.Zero,Vector3.UnitZ,Vector3.UnitX*2]};
        Refused(Decode(Fixture.Build(n),PcbCollider.Identity with {World=m}),variant==3?PcbDecodeStatus.InvalidData:PcbDecodeStatus.InvalidRequest);
    }

    [Fact] public void MalformedFilteredChildCannotLeaveEarlierAcceptedTriangleVisible()
    {
        var b=Fixture.Build(Node.Triangle with {Child1=1},Node.Triangle with {Primitives=[new(0,1,255,0)]});
        Refused(Decode(b));
    }

    [Fact] public void CancellationRefusesWithoutOutput()
    {
        using var cancellation=new CancellationTokenSource();cancellation.Cancel();
        Refused(PcbDecoder.Decode(Fixture.Build(Node.Triangle),PcbCollider.Identity,PcbQuery.GameFloor,
            PcbDecodeLimits.Default,cancellation.Token),PcbDecodeStatus.Cancelled);
    }

    [Fact] public void IdenticalInputsAreDeterministicAndReturnedArraysAreNotShared()
    {
        var b=Fixture.Build(Node.Triangle with {Child1=1},Node.Triangle);
        var a=Complete(Decode(b));var c=Complete(Decode(b));Assert.NotSame(a,c);
        Assert.Equal(a.Triangles.ToArray(),c.Triangles.ToArray());
    }

    [Fact] public void OddCompressedVertexCountSupportsUnalignedPrimitiveMaterial()
    {
        var n=new Node([Vector3.Zero,Vector3.UnitZ],[(65535,0,0)],[new(0,1,2,0x8000000000004000)],Vector3.Zero,Vector3.One);
        var t=Complete(Decode(Fixture.Build(n))).Triangles[0];
        Assert.Equal(Vector3.UnitX,t.C); Assert.Equal(0x8000000000004000UL,t.EffectiveMaterial);
    }

    [Fact] public void ZeroPrimitiveEmptyPcbIsCompleteRatherThanMissingData()
    {
        var empty=new Node([],[],[],Vector3.Zero,Vector3.Zero);
        var mesh=Complete(Decode(Fixture.Build(empty),limits:PcbDecodeLimits.Default with {Vertices=0,Primitives=0}));
        Assert.Empty(mesh.Triangles.ToArray());Assert.Equal(1,mesh.NodeCount);
    }

    [Fact] public void DeepValidTreeUsesBoundedIterativeTraversalNotNativeRecursion()
    {
        const int count=1500;
        var nodes=Enumerable.Range(0,count).Select(i=>Node.Triangle with {Child1=i+1<count?i+1:-1}).ToArray();
        var bytes=Fixture.Build(nodes);
        var result=Complete(Decode(bytes,limits:PcbDecodeLimits.Default with {Nodes=count}));
        Assert.Equal(count,result.NodeCount);Assert.Equal(count,result.Triangles.Length);
        Refused(Decode(bytes,limits:PcbDecodeLimits.Default with {Nodes=count-1}),PcbDecodeStatus.LimitExceeded);
    }

    [Fact] public void TinyMalformedBuffersAndSeededMutationsNeverThrowOrPublishPartialResults()
    {
        var random=new Random(27019);
        var valid=Fixture.Build(Node.Triangle with {Child1=1,Child2=2},Node.Triangle,Node.Triangle);
        for(int trial=0;trial<1500;++trial)
        {
            var bytes=trial<300?new byte[trial]:valid.ToArray();
            if(trial<300)random.NextBytes(bytes);
            else for(int j=0;j<1+trial%5;++j)bytes[random.Next(bytes.Length)]=(byte)random.Next(256);
            var result=Decode(bytes);
            if(result.Status==PcbDecodeStatus.Complete)
            {
                Assert.NotNull(result.Mesh);
                Assert.InRange(result.Mesh.NodeCount,1,PcbDecodeLimits.Default.Nodes);
                Assert.InRange(result.Mesh.SourcePrimitiveCount,0,PcbDecodeLimits.Default.Primitives);
                foreach(var t in result.Mesh.Triangles)
                    Assert.True(float.IsFinite(t.A.X)&&float.IsFinite(t.A.Y)&&float.IsFinite(t.A.Z)
                        &&float.IsFinite(t.B.X)&&float.IsFinite(t.B.Y)&&float.IsFinite(t.B.Z)
                        &&float.IsFinite(t.C.X)&&float.IsFinite(t.C.Y)&&float.IsFinite(t.C.Z));
            }
            else Assert.Null(result.Mesh);
        }
    }
}
