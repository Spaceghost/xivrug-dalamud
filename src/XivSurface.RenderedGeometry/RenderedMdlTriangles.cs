using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Lumina.Data;
using Lumina.Data.Parsing;
using Lumina.Models.Models;

namespace XivSurface.RenderedGeometry;

public enum RenderedMdlStatus { Success, InvalidIdentifier, TooLarge, Truncated, UnsupportedVersion,
    UnsupportedDeformation, UnsupportedLayout, InvalidRange, InvalidVertex, InvalidIndex, Degenerate, InvalidBounds }

public readonly record struct RenderedMdlMesh(int SourceMesh, int Material, int FirstVertex, int VertexCount,
    int FirstIndex, int IndexCount);
public readonly record struct RenderedMdlDrawRange(int SourceMesh, int SourceSubmesh, int FirstIndex,
    int IndexCount, uint RawVisibilityField);

/// <summary>Immutable geometry only. Neither a logical path nor these triangles establish that
/// the active renderer uses these bytes, a non-deforming material, or the supplied instance transform.</summary>
public sealed class RenderedMdlGeometry
{
    private readonly Vector3[] positions;
    private readonly float[] positionW;
    private readonly int[] indices;
    private readonly RenderedMdlMesh[] meshes;
    private readonly RenderedMdlDrawRange[] drawRanges;
    internal RenderedMdlGeometry(string identifier, string hash, Vector3[] positions, float[] positionW, int[] indices,
        RenderedMdlMesh[] meshes, RenderedMdlDrawRange[] drawRanges, string[] materials, Vector3 minimum, Vector3 maximum,
        bool inspectionOnly, bool unresolvedVisibility, bool unresolvedPositionW)
    {
        Identifier = identifier; Sha256 = hash; this.positions = positions; this.indices = indices;
        this.positionW = positionW; UnresolvedPositionW = unresolvedPositionW;
        this.meshes = meshes; Materials = Array.AsReadOnly(materials); Minimum = minimum; Maximum = maximum;
        this.drawRanges = drawRanges; InspectionOnly = inspectionOnly; UnresolvedSubmeshVisibility = unresolvedVisibility;
    }
    public string Identifier { get; }
    public string Sha256 { get; }
    public int Lod => 0;
    public ReadOnlySpan<Vector3> Positions => positions;
    public ReadOnlySpan<float> PositionW => positionW;
    public ReadOnlySpan<int> Indices => indices;
    public ReadOnlySpan<RenderedMdlMesh> Meshes => meshes;
    public ReadOnlySpan<RenderedMdlDrawRange> DrawRanges => drawRanges;
    public bool InspectionOnly { get; }
    public bool UnresolvedSubmeshVisibility { get; }
    public bool UnresolvedPositionW { get; }
    public IReadOnlyList<string> Materials { get; }
    public Vector3 Minimum { get; }
    public Vector3 Maximum { get; }
    public bool AuthorizesWorldSupport => false;
}

public readonly record struct RenderedMdlResult(RenderedMdlStatus Status, RenderedMdlGeometry? Geometry = null);

/// <summary>Worker-thread adapter for the EXACT installed Lumina MDL metadata layouts.
/// Input is a reconstructed/decompressed MDL resource, NOT a SqPack archive or GPU buffer.
/// No game calls, material loading, native pointers, or shared mutable parser state.</summary>
public static class RenderedMdlTriangles
{
    public const int MaximumBytes = 32 * 1024 * 1024;
    public const int MaximumTriangles = 131072;
    public const int MaximumVertices = 262144;
    public const int MaximumMeshes = 256;
    public const int MaximumSubmeshes = 4096;
    public const int MaximumStrings = 64 * 1024;
    public const float MaximumCoordinate = 32768;
    private const int FileHeaderBytes = 68, DeclarationBytes = 136;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static RenderedMdlResult Extract(string identifier, ReadOnlyMemory<byte> raw,
        CancellationToken cancellationToken = default) => ExtractInternal(identifier, raw, cancellationToken, false);

    /// <summary>OFFLINE INSPECTION ONLY: preserves otherwise-unresolved raw submesh visibility
    /// fields when there are no declared model attributes. Does not establish active draw groups,
    /// material eligibility, loaded-byte identity, or support authorization. Non-unit finite
    /// position W is preserved, not divided out or interpreted as a homogeneous coordinate.
    /// Ordinary Extract
    /// still refuses these resources; there is no permissive option on its support-facing API.</summary>
    public static RenderedMdlResult InspectUnresolvedDrawGroups(string identifier, ReadOnlyMemory<byte> raw,
        CancellationToken cancellationToken = default) => ExtractInternal(identifier, raw, cancellationToken, true);

    private static RenderedMdlResult ExtractInternal(string identifier, ReadOnlyMemory<byte> raw,
        CancellationToken cancellationToken, bool inspectionOnly)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(identifier) || identifier.Length > 768 || !identifier.EndsWith(".mdl", StringComparison.Ordinal)
            || !identifier.StartsWith("bg/", StringComparison.Ordinal) || identifier.Contains('\\')
            || identifier.Split('/').Any(p => p is "" or "." or "..") || identifier.Any(char.IsControl))
            return new(RenderedMdlStatus.InvalidIdentifier);
        if (raw.Length > MaximumBytes) return new(RenderedMdlStatus.TooLarge);
        if (raw.Length < FileHeaderBytes) return new(RenderedMdlStatus.Truncated);
        // Own a bounded snapshot before parsing; caller mutations cannot change a published result.
        var bytes = raw.ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        try { return ExtractOwned(identifier, bytes, cancellationToken, inspectionOnly); }
        catch (EndOfStreamException) { return new(RenderedMdlStatus.Truncated); }
        catch (DecoderFallbackException) { return new(RenderedMdlStatus.UnsupportedLayout); }
        catch (ArgumentException) { return new(RenderedMdlStatus.InvalidRange); }
        catch (OverflowException) { return new(RenderedMdlStatus.InvalidRange); }
    }

    private static RenderedMdlResult ExtractOwned(string identifier, byte[] bytes, CancellationToken ct, bool inspectionOnly)
    {
        var span = bytes.AsSpan();
        var version = BinaryPrimitives.ReadUInt32LittleEndian(span);
        if (version is not (0x01000005 or 0x01000006)) return new(RenderedMdlStatus.UnsupportedVersion);
        // Do not invoke Lumina's warning-printing edge-geometry loader for unsupported files.
        // The streaming flag is upload policy, not another index encoding:
        // installed Mesh.ReadIndices uses this same ushort buffer either way.
        // Only complete reconstructed buffer ranges are accepted below.
        if (span[65] > 1 || span[66] != 0) return new(RenderedMdlStatus.UnsupportedLayout);
        var declarationCount = BinaryPrimitives.ReadUInt16LittleEndian(span[12..]);
        if (declarationCount is 0 or > MaximumMeshes) return new(RenderedMdlStatus.TooLarge);
        var declarationEnd = checked(FileHeaderBytes + declarationCount * DeclarationBytes);
        if (declarationEnd + 8 > span.Length) return new(RenderedMdlStatus.Truncated);
        // Lumina's declaration reader scans until FF without a declaration-local bound.
        // Check that sentinel FIRST so malformed input cannot create an unbounded List.
        for (var d = 0; d < declarationCount; d++)
        {
            ct.ThrowIfCancellationRequested();
            var block = span.Slice(FileHeaderBytes + d * DeclarationBytes, DeclarationBytes);
            var count = 0;
            while (count < 17 && block[count * 8] != byte.MaxValue) count++;
            if (count is 0 or 17) return new(RenderedMdlStatus.UnsupportedLayout);
        }
        var stringsLength = BinaryPrimitives.ReadUInt32LittleEndian(span[(declarationEnd + 4)..]);
        var stringsCount = BinaryPrimitives.ReadUInt16LittleEndian(span[declarationEnd..]);
        if (stringsLength > MaximumStrings || stringsCount > 4096) return new(RenderedMdlStatus.TooLarge);
        if ((ulong)declarationEnd + 8 + stringsLength > (ulong)bytes.Length) return new(RenderedMdlStatus.Truncated);

        using var reader = new LuminaBinaryReader(bytes);
        reader.IsLittleEndian = true;
        var file = MdlStructs.ModelFileHeader.Read(reader);
        var declarations = new MdlStructs.VertexDeclarationStruct[declarationCount];
        for (var d = 0; d < declarations.Length; d++) declarations[d] = MdlStructs.VertexDeclarationStruct.Read(reader);
        reader.ReadUInt16(); reader.ReadUInt16(); reader.ReadUInt32();
        var strings = reader.ReadBytes((int)stringsLength);
        var model = reader.ReadStructure<MdlStructs.ModelHeader>();
        if (model.MeshCount != declarationCount || model.MaterialCount != file.MaterialCount
            || file.LodCount is 0 or > 3 || model.LodCount != file.LodCount || model.MaterialCount is 0 or > MaximumMeshes
            || model.SubmeshCount > MaximumSubmeshes) return new(RenderedMdlStatus.UnsupportedLayout);
        if (model.BoneCount != 0 || model.BoneTableCount != 0 || model.ShapeCount != 0 || model.ShapeMeshCount != 0
            || model.ShapeValueCount != 0 || model.AttributeCount != 0 || model.ElementIdCount != 0
            || !model.WavingAnimationDisabled || model.EdgeGeometryEnabled)
            return new(RenderedMdlStatus.UnsupportedDeformation);
        if (!float.IsFinite(model.Radius) || model.Radius < 0 || model.Radius > MaximumCoordinate)
            return new(RenderedMdlStatus.InvalidBounds);
        var lods = reader.ReadStructuresAsArray<MdlStructs.LodStruct>(3);
        var extraLods = model.ExtraLodEnabled ? reader.ReadStructuresAsArray<MdlStructs.ExtraLodStruct>(3) : null;
        var meshes = new MdlStructs.MeshStruct[model.MeshCount];
        for (var m = 0; m < meshes.Length; m++) { ct.ThrowIfCancellationRequested(); meshes[m] = MdlStructs.MeshStruct.Read(reader); }
        // Attributes are forbidden above. Terrain-shadow metadata is not collision support.
        reader.ReadStructuresAsArray<MdlStructs.TerrainShadowMeshStruct>(model.TerrainShadowMeshCount);
        var submeshes = reader.ReadStructuresAsArray<MdlStructs.SubmeshStruct>(model.SubmeshCount);
        reader.ReadStructuresAsArray<MdlStructs.TerrainShadowSubmeshStruct>(model.TerrainShadowSubmeshCount);
        var materialOffsets = new uint[model.MaterialCount];
        for (var m = 0; m < materialOffsets.Length; m++) materialOffsets[m] = reader.ReadUInt32();
        // No bones/shapes are permitted, so their intervening tables are empty.
        if (reader.ReadUInt32() != 0) return new(RenderedMdlStatus.UnsupportedDeformation);
        var padding = reader.ReadByte(); reader.BaseStream.Position = checked(reader.BaseStream.Position + padding);
        var overallBounds = MdlStructs.BoundingBoxStruct.Read(reader);
        var modelBounds = MdlStructs.BoundingBoxStruct.Read(reader);
        MdlStructs.BoundingBoxStruct.Read(reader); MdlStructs.BoundingBoxStruct.Read(reader);
        var metadataEnd = reader.BaseStream.Position;
        if (!ValidBounds(overallBounds) || !ValidBounds(modelBounds)) return new(RenderedMdlStatus.InvalidBounds);

        var lod = lods[0];
        if (lod.MeshCount == 0 || (uint)lod.MeshIndex + lod.MeshCount > meshes.Length) return new(RenderedMdlStatus.InvalidRange);
        // TerrainShadow indices address the separate TerrainShadowMeshStruct table,
        // not MeshStruct. A same-numbered entry therefore does not tag Main as shadow.
        if ((uint)lod.TerrainShadowMeshIndex + lod.TerrainShadowMeshCount > model.TerrainShadowMeshCount)
            return new(RenderedMdlStatus.InvalidRange);
        if (Overlaps(lod.MeshIndex, lod.MeshCount, lod.WaterMeshIndex, lod.WaterMeshCount)
            || Overlaps(lod.MeshIndex, lod.MeshCount, lod.ShadowMeshIndex, lod.ShadowMeshCount)
            || Overlaps(lod.MeshIndex, lod.MeshCount, lod.VerticalFogMeshIndex, lod.VerticalFogMeshCount))
            return new(RenderedMdlStatus.UnsupportedLayout);
        if (extraLods is not null)
        {
            var e = extraLods[0];
            if (Overlaps(lod.MeshIndex, lod.MeshCount, e.GlassMeshIndex, e.GlassMeshCount)
                || Overlaps(lod.MeshIndex, lod.MeshCount, e.LightShaftMeshIndex, e.LightShaftMeshCount)
                || Overlaps(lod.MeshIndex, lod.MeshCount, e.MaterialChangeMeshIndex, e.MaterialChangeMeshCount)
                || Overlaps(lod.MeshIndex, lod.MeshCount, e.CrestChangeMeshIndex, e.CrestChangeMeshCount))
                return new(RenderedMdlStatus.UnsupportedLayout);
        }
        if (!Range(file.VertexOffset[0], file.VertexBufferSize[0], bytes.Length)
            || !Range(file.IndexOffset[0], file.IndexBufferSize[0], bytes.Length)
            || file.VertexOffset[0] < metadataEnd || file.IndexOffset[0] < metadataEnd
            || Intersects(file.VertexOffset[0], file.VertexBufferSize[0], file.IndexOffset[0], file.IndexBufferSize[0]))
            return new(RenderedMdlStatus.InvalidRange);
        var totalVertices = 0; var totalIndices = 0;
        var drawRanges = new List<RenderedMdlDrawRange>(); var unresolvedVisibility = false; var totalDrawRanges = 0;
        var selected = new PositionLayout[lod.MeshCount];
        for (var n = 0; n < lod.MeshCount; n++)
        {
            ct.ThrowIfCancellationRequested(); var m = lod.MeshIndex + n; var mesh = meshes[m];
            if (mesh.VertexCount == 0 || mesh.IndexCount == 0 || mesh.IndexCount % 3 != 0
                || mesh.MaterialIndex >= model.MaterialCount || mesh.BoneTableIndex != 255
                || (uint)mesh.SubMeshIndex + mesh.SubMeshCount > submeshes.Length)
                return new(RenderedMdlStatus.UnsupportedLayout);
            if (mesh.IndexCount > MaximumTriangles * 3 || totalIndices + (long)mesh.IndexCount > MaximumTriangles * 3
                || totalVertices + (long)mesh.VertexCount > MaximumVertices) return new(RenderedMdlStatus.TooLarge);
            totalVertices += mesh.VertexCount; totalIndices += (int)mesh.IndexCount;
            totalDrawRanges += Math.Max(1, (int)mesh.SubMeshCount);
            if (totalDrawRanges > MaximumSubmeshes) return new(RenderedMdlStatus.TooLarge);
            if (!Range((ulong)mesh.StartIndex * 2, (ulong)mesh.IndexCount * 2, file.IndexBufferSize[0]))
                return new(RenderedMdlStatus.InvalidRange);
            ulong submeshEnd = mesh.StartIndex;
            for (var s = 0; s < mesh.SubMeshCount; s++)
            {
                var sub = submeshes[mesh.SubMeshIndex + s];
                if (sub.BoneCount != 0 || (!inspectionOnly && sub.AttributeIndexMask != 0)) return new(RenderedMdlStatus.UnsupportedDeformation);
                unresolvedVisibility |= sub.AttributeIndexMask != 0;
                if (sub.IndexOffset < mesh.StartIndex || !Range(sub.IndexOffset - mesh.StartIndex, sub.IndexCount, mesh.IndexCount)
                    || sub.IndexOffset % 3 != mesh.StartIndex % 3 || sub.IndexCount % 3 != 0)
                    return new(RenderedMdlStatus.InvalidRange);
                // Do not promote unused/conditional parts of an index buffer to
                // support. First version accepts only an exact full partition.
                if (sub.IndexOffset != submeshEnd || sub.IndexCount == 0) return new(RenderedMdlStatus.UnsupportedLayout);
                submeshEnd += sub.IndexCount;
            }
            if (mesh.SubMeshCount != 0 && submeshEnd != (ulong)mesh.StartIndex + mesh.IndexCount)
                return new(RenderedMdlStatus.UnsupportedLayout);
            var layout = ValidateLayout(mesh, declarations[m], file.VertexBufferSize[0]);
            if (layout.Status != RenderedMdlStatus.Success) return new(layout.Status);
            selected[n] = layout;
        }
        var positions = new Vector3[totalVertices]; var positionW = new float[totalVertices];
        var indices = new int[totalIndices]; var unresolvedPositionW = false;
        var ranges = new RenderedMdlMesh[lod.MeshCount];
        var vertexBase = 0; var indexBase = 0; var minimum = new Vector3(float.PositiveInfinity); var maximum = new Vector3(float.NegativeInfinity);
        for (var n = 0; n < lod.MeshCount; n++)
        {
            var m = lod.MeshIndex + n; var mesh = meshes[m]; var layout = selected[n];
            for (var v = 0; v < mesh.VertexCount; v++)
            {
                if ((v & 255) == 0) ct.ThrowIfCancellationRequested();
                var offset = checked((int)(file.VertexOffset[0] + layout.Offset + (uint)(v * layout.Stride)));
                var value = ReadPosition(span[offset..], layout.Type);
                if (!Finite(value) || Math.Abs(value.X) > MaximumCoordinate || Math.Abs(value.Y) > MaximumCoordinate
                    || Math.Abs(value.Z) > MaximumCoordinate || (!inspectionOnly && Math.Abs(value.W - 1) > .001f))
                    return new(RenderedMdlStatus.InvalidVertex);
                unresolvedPositionW |= Math.Abs(value.W - 1) > .001f;
                var point = new Vector3(value.X, value.Y, value.Z);
                if (!Inside(point, modelBounds, .01f)) return new(RenderedMdlStatus.InvalidBounds);
                positions[vertexBase + v] = point; positionW[vertexBase + v] = value.W;
                minimum = Vector3.Min(minimum, point); maximum = Vector3.Max(maximum, point);
            }
            var indexStart = checked((int)(file.IndexOffset[0] + mesh.StartIndex * 2));
            for (var i = 0; i < mesh.IndexCount; i += 3)
            {
                if ((i & 255) == 0) ct.ThrowIfCancellationRequested();
                var a = BinaryPrimitives.ReadUInt16LittleEndian(span[(indexStart + i * 2)..]);
                var b = BinaryPrimitives.ReadUInt16LittleEndian(span[(indexStart + i * 2 + 2)..]);
                var c = BinaryPrimitives.ReadUInt16LittleEndian(span[(indexStart + i * 2 + 4)..]);
                if (a >= mesh.VertexCount || b >= mesh.VertexCount || c >= mesh.VertexCount) return new(RenderedMdlStatus.InvalidIndex);
                if (a == b || b == c || c == a || Vector3.Cross(positions[vertexBase + b] - positions[vertexBase + a],
                    positions[vertexBase + c] - positions[vertexBase + a]).LengthSquared() <= 1e-16f)
                    return new(RenderedMdlStatus.Degenerate);
                indices[indexBase + i] = vertexBase + a; indices[indexBase + i + 1] = vertexBase + b; indices[indexBase + i + 2] = vertexBase + c;
            }
            ranges[n] = new(m, mesh.MaterialIndex, vertexBase, mesh.VertexCount, indexBase, (int)mesh.IndexCount);
            if (mesh.SubMeshCount == 0) drawRanges.Add(new(m, -1, indexBase, (int)mesh.IndexCount, 0));
            for (var s = 0; s < mesh.SubMeshCount; s++)
            {
                var subIndex = mesh.SubMeshIndex + s; var sub = submeshes[subIndex];
                drawRanges.Add(new(m, subIndex, checked(indexBase + (int)(sub.IndexOffset - mesh.StartIndex)), (int)sub.IndexCount, sub.AttributeIndexMask));
            }
            vertexBase += mesh.VertexCount; indexBase += (int)mesh.IndexCount;
        }
        var materials = new string[materialOffsets.Length];
        for (var m = 0; m < materials.Length; m++)
        {
            if (materialOffsets[m] >= strings.Length) return new(RenderedMdlStatus.InvalidRange);
            var text = strings.AsSpan((int)materialOffsets[m]); var end = text.IndexOf((byte)0);
            if (end is <= 0 or > 768) return new(RenderedMdlStatus.UnsupportedLayout);
            materials[m] = StrictUtf8.GetString(text[..end]);
        }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var at = 0; at < bytes.Length; at += 65536) { ct.ThrowIfCancellationRequested(); hash.AppendData(span.Slice(at, Math.Min(65536, bytes.Length - at))); }
        return new(RenderedMdlStatus.Success, new(identifier, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
            positions, positionW, indices, ranges, drawRanges.ToArray(), materials, minimum, maximum, inspectionOnly, unresolvedVisibility, unresolvedPositionW));
    }

    private readonly record struct PositionLayout(RenderedMdlStatus Status, uint Offset = 0, int Stride = 0, Vertex.VertexType Type = default);
    private static PositionLayout ValidateLayout(MdlStructs.MeshStruct mesh, MdlStructs.VertexDeclarationStruct declaration, uint bufferSize)
    {
        if (mesh.VertexStreamCount is 0 or > 3) return new(RenderedMdlStatus.UnsupportedLayout);
        MdlStructs.VertexElement? position = null;
        foreach (var element in declaration.VertexElements)
        {
            if (element.Usage > (byte)Vertex.VertexUsage.Color) return new(RenderedMdlStatus.UnsupportedLayout);
            if (element.Usage is (byte)Vertex.VertexUsage.BlendIndices or (byte)Vertex.VertexUsage.BlendWeights)
                return new(RenderedMdlStatus.UnsupportedDeformation);
            if (element.Stream >= mesh.VertexStreamCount) return new(RenderedMdlStatus.UnsupportedLayout);
            var size = (Vertex.VertexType)element.Type switch
            { Vertex.VertexType.Single3 => 12, Vertex.VertexType.Single4 => 16, Vertex.VertexType.Half4 => 8,
                Vertex.VertexType.Half2 or Vertex.VertexType.UInt or Vertex.VertexType.ByteFloat4 => 4, _ => 0 };
            if (size == 0 || mesh.VertexBufferStride[element.Stream] == 0
                || element.Offset + size > mesh.VertexBufferStride[element.Stream]
                || !Range(mesh.VertexBufferOffset[element.Stream], (ulong)mesh.VertexCount * mesh.VertexBufferStride[element.Stream], bufferSize))
                return new(RenderedMdlStatus.InvalidRange);
            if (element.Usage != (byte)Vertex.VertexUsage.Position) continue;
            if (position.HasValue || element.UsageIndex != 0 || (Vertex.VertexType)element.Type is not
                (Vertex.VertexType.Single3 or Vertex.VertexType.Single4 or Vertex.VertexType.Half4)) return new(RenderedMdlStatus.UnsupportedLayout);
            position = element;
        }
        if (!position.HasValue) return new(RenderedMdlStatus.UnsupportedLayout);
        var p = position.Value;
        return new(RenderedMdlStatus.Success, mesh.VertexBufferOffset[p.Stream] + p.Offset, mesh.VertexBufferStride[p.Stream], (Vertex.VertexType)p.Type);
    }
    private static Vector4 ReadPosition(ReadOnlySpan<byte> bytes, Vertex.VertexType type)
    {
        if (type == Vertex.VertexType.Half4) return new((float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(bytes)),
            (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..])),
            (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..])),
            (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..])));
        return new(BinaryPrimitives.ReadSingleLittleEndian(bytes), BinaryPrimitives.ReadSingleLittleEndian(bytes[4..]),
            BinaryPrimitives.ReadSingleLittleEndian(bytes[8..]), type == Vertex.VertexType.Single4 ? BinaryPrimitives.ReadSingleLittleEndian(bytes[12..]) : 1);
    }
    private static bool Range(ulong offset, ulong length, ulong capacity) => offset <= capacity && length <= capacity - offset;
    private static bool Range(ulong offset, ulong length, int capacity) => capacity >= 0 && Range(offset, length, (ulong)capacity);
    private static bool Intersects(uint a, uint an, uint b, uint bn) => (ulong)a < (ulong)b + bn && (ulong)b < (ulong)a + an;
    private static bool Overlaps(int a, int an, int b, int bn) => an > 0 && bn > 0 && a < b + bn && b < a + an;
    private static bool Finite(Vector4 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z) && float.IsFinite(p.W);
    private static bool ValidBounds(MdlStructs.BoundingBoxStruct b) => b.Min.Length == 4 && b.Max.Length == 4
        && b.Min.All(float.IsFinite) && b.Max.All(float.IsFinite) && b.Min[0] <= b.Max[0] && b.Min[1] <= b.Max[1] && b.Min[2] <= b.Max[2];
    private static bool Inside(Vector3 p, MdlStructs.BoundingBoxStruct b, float margin) => p.X >= b.Min[0] - margin && p.X <= b.Max[0] + margin
        && p.Y >= b.Min[1] - margin && p.Y <= b.Max[1] + margin && p.Z >= b.Min[2] - margin && p.Z <= b.Max[2] + margin;
}
