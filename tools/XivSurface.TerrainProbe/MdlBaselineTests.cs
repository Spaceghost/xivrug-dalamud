using System.Buffers.Binary;
using System.Buffers;
using System.Numerics;
using System.Runtime.InteropServices;
using Lumina.Data;
using Lumina.Data.Parsing;
using Lumina.Models.Models;
using XivSurface.RenderedGeometry;

// Original frozen 53-case parser baseline; only wrapped in a callable test method.
internal static class MdlBaselineTests
{
    internal static void Run()
    {
var passed = 0;
void Test(string name, Action body) { body(); passed++; Console.WriteLine($"PASS {name}"); }
void Equal<T>(T wanted, T actual) where T : notnull { if (!EqualityComparer<T>.Default.Equals(wanted, actual)) throw new Exception($"Expected {wanted}, got {actual}"); }
RenderedMdlResult Parse(Fixture f) => RenderedMdlTriangles.Extract("bg/test/static/stairs.mdl", f.Bytes);
void Reject(string name, Action<Fixture> mutate, RenderedMdlStatus expected)
{ Test(name, () => { var f = Fixture.Make(); mutate(f); var result = Parse(f); Equal(expected, result.Status); if (result.Geometry is not null) throw new Exception("Partial output escaped rejection."); }); }

Test("float3 stairs retain vertical riser and indexed adjacency", () => {
    var f = Fixture.Make(); var result = Parse(f); Equal(RenderedMdlStatus.Success, result.Status);
    var g = result.Geometry!; Equal(6, g.Positions.Length); Equal(12, g.Indices.Length); Equal(0, g.Lod);
    Equal(new Vector3(0, 0, 0), g.Positions[0]); Equal(new Vector3(0, .2f, 1), g.Positions[4]);
    Equal(2, g.Indices[6]); Equal(4, g.Indices[8]); Equal(false, g.AuthorizesWorldSupport);
    Equal("fixture.mtrl", g.Materials[0]); Equal(64, g.Sha256.Length);
});
Test("stride and nonzero declaration offset honored", () => {
    var result = Parse(Fixture.Make(stride: 32, offset: 8)); Equal(RenderedMdlStatus.Success, result.Status);
    Equal(new Vector3(0, .2f, 1), result.Geometry!.Positions[4]);
});
Test("position in second stream honored", () => {
    var result = Parse(Fixture.Make(stream: 1)); Equal(RenderedMdlStatus.Success, result.Status);
    Equal(new Vector3(0, .2f, 1), result.Geometry!.Positions[4]);
});
Test("float4 position supported", () => Equal(RenderedMdlStatus.Success, Parse(Fixture.Make(Vertex.VertexType.Single4, 16)).Status));
Test("half4 position supported", () => {
    var result = Parse(Fixture.Make(Vertex.VertexType.Half4, 8)); Equal(RenderedMdlStatus.Success, result.Status);
    if (Math.Abs(result.Geometry!.Positions[4].Y - .2f) > .001f) throw new Exception("Bad half decode");
});
Test("select only LOD0 Main at nonzero mesh index", () => {
    var result = Parse(Fixture.Make(meshCount: 2, mainIndex: 1)); Equal(RenderedMdlStatus.Success, result.Status);
    Equal(6, result.Geometry!.Positions.Length); Equal(1, result.Geometry.Meshes[0].SourceMesh);
});
Test("published result owns buffers", () => {
    var fixture = Fixture.Make(); var first = Parse(fixture).Geometry!; var original = first.Positions[0];
    Array.Fill(fixture.Bytes, (byte)0xff); Equal(original, first.Positions[0]); Equal(0, first.Indices[0]);
});
Test("path is bounded descriptive BG identifier", () => {
    var f = Fixture.Make(); foreach (var id in new[] { "chara/x.mdl", "bg/../x.mdl", "bg/./x.mdl", "bg//x.mdl", "bg/x.mdl\0", "bg/x.MDL", "" })
        Equal(RenderedMdlStatus.InvalidIdentifier, RenderedMdlTriangles.Extract(id, f.Bytes).Status);
});
Test("cancel before allocation", () => {
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    try { RenderedMdlTriangles.Extract("bg/x.mdl", Fixture.Make().Bytes, cancelled.Token); throw new Exception("Did not cancel"); }
    catch (OperationCanceledException) { }
});
Test("file byte hard cap", () => Equal(RenderedMdlStatus.TooLarge,
    RenderedMdlTriangles.Extract("bg/x.mdl", new byte[RenderedMdlTriangles.MaximumBytes + 1]).Status));
Test("short resource rejected", () => Equal(RenderedMdlStatus.Truncated, RenderedMdlTriangles.Extract("bg/x.mdl", new byte[67]).Status));
Reject("unknown version", f => f.U32(0, 0x01000007), RenderedMdlStatus.UnsupportedVersion);
Reject("declaration count cap before allocation", f => f.U16(12, 65535), RenderedMdlStatus.TooLarge);
Reject("unterminated declaration stays bounded", f => { for (var i = 0; i < 17; i++) f.Bytes[68 + i * 8] = 0; }, RenderedMdlStatus.UnsupportedLayout);
Reject("oversize string table before allocation", f => f.U32(f.StringHeader + 4, uint.MaxValue), RenderedMdlStatus.TooLarge);
Test("reconstructed streaming index buffer supported", () => {
    var f = Fixture.Make(); f.Bytes[65] = 1; Equal(RenderedMdlStatus.Success, Parse(f).Status);
});
Reject("invalid streaming flag rejected", f => f.Bytes[65] = 2, RenderedMdlStatus.UnsupportedLayout);
Reject("edge geometry unsupported", f => f.Bytes[66] = 1, RenderedMdlStatus.UnsupportedLayout);
Reject("skinned bones rejected", f => f.ChangeModel(m => { m.BoneCount = 1; return m; }), RenderedMdlStatus.UnsupportedDeformation);
Reject("bone table rejected", f => f.ChangeModel(m => { m.BoneTableCount = 1; return m; }), RenderedMdlStatus.UnsupportedDeformation);
Reject("shape deformation rejected", f => f.ChangeModel(m => { m.ShapeCount = 1; return m; }), RenderedMdlStatus.UnsupportedDeformation);
Reject("conditional attributes rejected", f => f.ChangeModel(m => { m.AttributeCount = 1; return m; }), RenderedMdlStatus.UnsupportedDeformation);
Reject("waving not disabled rejected", f => f.Bytes[f.ModelHeader + Fixture.Flags1Offset] = 0, RenderedMdlStatus.UnsupportedDeformation);
Reject("blend declaration rejected", f => f.Bytes[68 + 3] = (byte)Vertex.VertexUsage.BlendWeights, RenderedMdlStatus.UnsupportedDeformation);
Reject("unsupported position type", f => f.Bytes[68 + 2] = (byte)Vertex.VertexType.Half2, RenderedMdlStatus.UnsupportedLayout);
Reject("bad position stream", f => f.Bytes[68] = 2, RenderedMdlStatus.UnsupportedLayout);
Reject("position outside stride", f => f.Bytes[68 + 1] = 8, RenderedMdlStatus.InvalidRange);
Reject("index range overflow", f => f.U32(f.MeshHeader + 16, uint.MaxValue), RenderedMdlStatus.InvalidRange);
Reject("triangle count cap before output allocation", f => f.U32(f.MeshHeader + 4, (RenderedMdlTriangles.MaximumTriangles + 1u) * 3), RenderedMdlStatus.TooLarge);
Reject("non-triangle topology rejected", f => f.U32(f.MeshHeader + 4, 11), RenderedMdlStatus.UnsupportedLayout);
Reject("index references missing vertex", f => f.U16(f.IndexData, 60000), RenderedMdlStatus.InvalidIndex);
Reject("repeated-index triangle rejected", f => f.U16(f.IndexData + 2, 0), RenderedMdlStatus.Degenerate);
Reject("NaN position rejected", f => f.U32(f.VertexData, 0x7fc00000), RenderedMdlStatus.InvalidVertex);
Reject("infinite position rejected", f => f.U32(f.VertexData, 0x7f800000), RenderedMdlStatus.InvalidVertex);
Reject("position outside model bounds", f => f.U32(f.VertexData, BitConverter.SingleToUInt32Bits(100)), RenderedMdlStatus.InvalidBounds);
Reject("vertex buffer overlaps metadata", f => f.U32(16, 68), RenderedMdlStatus.InvalidRange);
Reject("index buffer past EOF", f => f.U32(28, uint.MaxValue), RenderedMdlStatus.InvalidRange);
Reject("Main also tagged water rejected", f => f.ChangeLod(l => { l.WaterMeshIndex = 0; l.WaterMeshCount = 1; return l; }), RenderedMdlStatus.UnsupportedLayout);
Test("TerrainShadow has its own mesh namespace", () => {
    Equal(RenderedMdlStatus.Success, Parse(Fixture.Make(terrainShadow: true)).Status);
});
Reject("TerrainShadow range still validated", f => f.ChangeLod(l => { l.TerrainShadowMeshCount = 1; return l; }), RenderedMdlStatus.InvalidRange);
Test("truncation never emits a partial mesh", () => {
    var f = Fixture.Make(); foreach (var length in new[] { 68, 100, f.StringHeader + 7, f.ModelHeader + 3, f.MeshHeader + 20, f.VertexData - 1, f.Bytes.Length - 1 }) {
        var result = RenderedMdlTriangles.Extract("bg/x.mdl", f.Bytes.AsMemory(0, length));
        if (result.Status == RenderedMdlStatus.Success || result.Geometry is not null) throw new Exception($"Accepted {length}");
    }
});
Test("model v6 static layout supported", () => { var f = Fixture.Make(); f.U32(0, 0x01000006); Equal(RenderedMdlStatus.Success, Parse(f).Status); });
Test("full unconditional submesh partition supported", () => {
    Equal(RenderedMdlStatus.Success, Parse(Fixture.Make(submeshes: [
        new() { IndexOffset = 0, IndexCount = 6 }, new() { IndexOffset = 6, IndexCount = 6 }])).Status);
});
Test("partial submesh partition cannot create phantom support", () => {
    Equal(RenderedMdlStatus.UnsupportedLayout, Parse(Fixture.Make(submeshes: [new() { IndexOffset = 0, IndexCount = 9 }])).Status);
});
Test("submesh gaps and overlaps rejected", () => {
    foreach (uint offset in new uint[] { 3, 9 })
        Equal(RenderedMdlStatus.UnsupportedLayout, Parse(Fixture.Make(submeshes: [
            new() { IndexOffset = 0, IndexCount = 6 }, new() { IndexOffset = offset, IndexCount = 3 }])).Status);
});
Test("conditional submesh rejected", () => {
    Equal(RenderedMdlStatus.UnsupportedDeformation, Parse(Fixture.Make(submeshes: [
        new() { IndexOffset = 0, IndexCount = 12, AttributeIndexMask = 1 }])).Status);
});
Test("inspection preserves unknown draw fields without changing strict extraction", () => {
    var f = Fixture.Make(submeshes: [new() { IndexOffset = 0, IndexCount = 6, AttributeIndexMask = 29 },
        new() { IndexOffset = 6, IndexCount = 6, AttributeIndexMask = 49 }]);
    Equal(RenderedMdlStatus.UnsupportedDeformation, Parse(f).Status);
    var inspected = RenderedMdlTriangles.InspectUnresolvedDrawGroups("bg/x.mdl", f.Bytes);
    Equal(RenderedMdlStatus.Success, inspected.Status); var geometry = inspected.Geometry!;
    Equal(true, geometry.InspectionOnly); Equal(true, geometry.UnresolvedSubmeshVisibility);
    Equal(false, geometry.AuthorizesWorldSupport); Equal(29u, geometry.DrawRanges[0].RawVisibilityField);
    Equal(49u, geometry.DrawRanges[1].RawVisibilityField); Equal(6, geometry.DrawRanges[1].FirstIndex);
    f.U32(f.IndexData, 0); Equal(49u, geometry.DrawRanges[1].RawVisibilityField);
});
Test("inspection does not enable deformation", () => {
    var f = Fixture.Make(); f.ChangeModel(m => { m.BoneCount = 1; return m; });
    Equal(RenderedMdlStatus.UnsupportedDeformation, RenderedMdlTriangles.InspectUnresolvedDrawGroups("bg/x.mdl", f.Bytes).Status);
});
Test("submesh bone deformation rejected", () => {
    Equal(RenderedMdlStatus.UnsupportedDeformation, Parse(Fixture.Make(submeshes: [
        new() { IndexOffset = 0, IndexCount = 12, BoneCount = 1 }])).Status);
});
Test("homogeneous W must be one", () => {
    var f = Fixture.Make(Vertex.VertexType.Single4, 16); f.U32(f.VertexData + 12, 0);
    Equal(RenderedMdlStatus.InvalidVertex, Parse(f).Status);
});
Test("inspection retains finite nonunit W without homogeneous reinterpretation", () => {
    var f = Fixture.Make(Vertex.VertexType.Single4, 16); f.U32(f.VertexData + 12, BitConverter.SingleToUInt32Bits(.39f));
    Equal(RenderedMdlStatus.InvalidVertex, Parse(f).Status);
    var inspected = RenderedMdlTriangles.InspectUnresolvedDrawGroups("bg/x.mdl", f.Bytes);
    Equal(RenderedMdlStatus.Success, inspected.Status); Equal(.39f, inspected.Geometry!.PositionW[0]);
    Equal(true, inspected.Geometry.UnresolvedPositionW); Equal(false, inspected.Geometry.AuthorizesWorldSupport);
    f.U32(f.VertexData + 12, 0x7fc00000);
    Equal(RenderedMdlStatus.InvalidVertex, RenderedMdlTriangles.InspectUnresolvedDrawGroups("bg/x.mdl", f.Bytes).Status);
});
Test("cancel during bounded input snapshot", () => {
    using var cancel = new CancellationTokenSource(); using var manager = new CancellingMemory(Fixture.Make().Bytes, cancel);
    var memory = manager.Memory; manager.Armed = true;
    try { RenderedMdlTriangles.Extract("bg/x.mdl", memory, cancel.Token); throw new Exception("Did not cancel"); }
    catch (OperationCanceledException) { }
});
Test("deterministic malformed-byte corpus never escapes partial output", () => {
    var original = Fixture.Make().Bytes; var random = new Random(7521);
    for (var iteration = 0; iteration < 1000; iteration++) {
        var bytes = (byte[])original.Clone(); var offset = random.Next(bytes.Length);
        bytes[offset] ^= (byte)(1 << random.Next(8));
        var result = RenderedMdlTriangles.Extract("bg/x.mdl", bytes);
        if ((result.Status == RenderedMdlStatus.Success) != (result.Geometry is not null)) throw new Exception("Partial result");
    }
});
Console.WriteLine($"{passed} rendered-MDL tests PASS; installed Lumina {typeof(LuminaBinaryReader).Assembly.GetName().Version}");
    }
sealed record Fixture(byte[] Bytes, int StringHeader, int ModelHeader, int LodHeader, int MeshHeader, int VertexData, int IndexData)
{
    internal static readonly int Flags1Offset = (int)Marshal.OffsetOf<MdlStructs.ModelHeader>("Flags1");
    public void U32(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Bytes.AsSpan(offset), value);
    public void U16(int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(Bytes.AsSpan(offset), value);
    public void ChangeModel(Func<MdlStructs.ModelHeader, MdlStructs.ModelHeader> change) {
        var model = change(MemoryMarshal.Read<MdlStructs.ModelHeader>(Bytes.AsSpan(ModelHeader))); MemoryMarshal.Write(Bytes.AsSpan(ModelHeader), in model);
    }
    public void ChangeLod(Func<MdlStructs.LodStruct, MdlStructs.LodStruct> change) {
        var lod = change(MemoryMarshal.Read<MdlStructs.LodStruct>(Bytes.AsSpan(LodHeader))); MemoryMarshal.Write(Bytes.AsSpan(LodHeader), in lod);
    }
    private static void Struct<T>(BinaryWriter writer, T value) where T : unmanaged => writer.Write(MemoryMarshal.AsBytes(new[] { value }.AsSpan()));
    public static Fixture Make(Vertex.VertexType type = Vertex.VertexType.Single3, byte stride = 12, byte offset = 0, byte stream = 0, ushort meshCount = 1, ushort mainIndex = 0,
        MdlStructs.SubmeshStruct[]? submeshes = null, bool terrainShadow = false)
    {
        submeshes ??= [];
        using var data = new MemoryStream(); using var writer = new BinaryWriter(data);
        writer.Write(0x01000005u); writer.Write(0u); writer.Write(0u); writer.Write(meshCount); writer.Write((ushort)1);
        for (var i = 0; i < 12; i++) writer.Write(0u);
        writer.Write((byte)1); writer.Write((byte)0); writer.Write((byte)0); writer.Write((byte)0);
        for (var m = 0; m < meshCount; m++) {
            writer.Write(new[] { stream, offset, (byte)type, (byte)Vertex.VertexUsage.Position, (byte)0, (byte)0, (byte)0, (byte)0 });
            writer.Write((byte)255); writer.Write(new byte[127]);
        }
        var stringHeader = (int)data.Position; writer.Write((ushort)1); writer.Write((ushort)0);
        var text = System.Text.Encoding.UTF8.GetBytes("fixture.mtrl\0"); writer.Write((uint)text.Length); writer.Write(text);
        var modelHeader = (int)data.Position;
        Struct(writer, new MdlStructs.ModelHeader { Radius = 20, MeshCount = meshCount, MaterialCount = 1, LodCount = 1,
            SubmeshCount = (ushort)submeshes.Length, TerrainShadowMeshCount = (byte)(terrainShadow ? 1 : 0) });
        var lodHeader = (int)data.Position;
        Struct(writer, new MdlStructs.LodStruct { MeshIndex = mainIndex, MeshCount = 1,
            TerrainShadowMeshCount = (ushort)(terrainShadow ? 1 : 0) });
        Struct(writer, default(MdlStructs.LodStruct)); Struct(writer, default(MdlStructs.LodStruct));
        var meshHeader = (int)data.Position;
        for (var m = 0; m < meshCount; m++) {
            writer.Write((ushort)6); writer.Write((ushort)0); writer.Write(12u);
            writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((ushort)submeshes.Length); writer.Write((ushort)255);
            writer.Write(0u); writer.Write(0u); writer.Write(0u); writer.Write(0u);
            writer.Write(stride); writer.Write(stride); writer.Write(stride); writer.Write((byte)(stream + 1));
        }
        if (terrainShadow) Struct(writer, default(MdlStructs.TerrainShadowMeshStruct));
        foreach (var submesh in submeshes) Struct(writer, submesh);
        writer.Write(0u); // material string offset
        writer.Write(0u); // no submesh bone map
        writer.Write((byte)0); // alignment padding
        for (var box = 0; box < 4; box++) { for (var c = 0; c < 4; c++) writer.Write(c == 3 ? 0f : -10f); for (var c = 0; c < 4; c++) writer.Write(c == 3 ? 0f : 10f); }
        var vertexData = (int)data.Position;
        Vector3[] points = [new(0, 0, 0), new(1, 0, 0), new(0, 0, 1), new(1, 0, 1), new(0, .2f, 1), new(1, .2f, 1)];
        foreach (var p in points) {
            var start = data.Position; writer.Write(new byte[offset]);
            if (type == Vertex.VertexType.Half4) { writer.Write(BitConverter.HalfToUInt16Bits((Half)p.X)); writer.Write(BitConverter.HalfToUInt16Bits((Half)p.Y)); writer.Write(BitConverter.HalfToUInt16Bits((Half)p.Z)); writer.Write(BitConverter.HalfToUInt16Bits((Half)1)); }
            else { writer.Write(p.X); writer.Write(p.Y); writer.Write(p.Z); if (type == Vertex.VertexType.Single4) writer.Write(1f); }
            while (data.Position < start + stride) writer.Write((byte)0xcc);
        }
        var indexData = (int)data.Position;
        foreach (ushort i in new ushort[] { 0, 2, 1, 1, 2, 3, 2, 3, 4, 3, 5, 4 }) writer.Write(i);
        var result = new Fixture(data.ToArray(), stringHeader, modelHeader, lodHeader, meshHeader, vertexData, indexData);
        result.Bytes[modelHeader + Flags1Offset] = 4;
        result.U32(16, (uint)vertexData); result.U32(28, (uint)indexData); result.U32(40, (uint)(indexData - vertexData)); result.U32(52, 24);
        return result;
    }
}

sealed class CancellingMemory(byte[] bytes, CancellationTokenSource source) : MemoryManager<byte>
{
    public bool Armed { get; set; }
    public override Span<byte> GetSpan() { if (Armed) source.Cancel(); return bytes; }
    public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();
    public override void Unpin() { }
    protected override void Dispose(bool disposing) { }
}
}
