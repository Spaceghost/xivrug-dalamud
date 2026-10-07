using System.Buffers.Binary;
using System.Numerics;

namespace XivSurface.Core.Collision;

/// <summary>Experimental pure decoder for the profiled PCB versions 1/4. The caller
/// must own stable input bytes throughout the call. All declared nodes are validated
/// even outside the query or material filter. Complete describes this supplied graph,
/// not native lifetime, complete scene coverage, reachable support, or rendered terrain.
/// Real-resource corpus and native ray-hit parity remain prerequisites for activation.</summary>
public static class PcbDecoder
{
    private readonly record struct NodeRange(int Begin, int End);

    public static PcbDecodeResult Decode(ReadOnlySpan<byte> bytes, PcbCollider collider, PcbQuery query,
        PcbDecodeLimits limits, CancellationToken cancellation = default)
    {
        if (!limits.IsValid || !query.IsValid || !Numeric.TransformValid(collider.World))
            return Refuse(PcbDecodeStatus.InvalidRequest);
        if (cancellation.IsCancellationRequested) return Refuse(PcbDecodeStatus.Cancelled);
        if (bytes.Length > limits.Bytes) return Refuse(PcbDecodeStatus.LimitExceeded);
        if (bytes.Length < 64) return Refuse(PcbDecodeStatus.InvalidData);
        int version = I32(bytes, 4), children = I32(bytes, 8), primitiveTotal = I32(bytes, 12);
        if (version is not (1 or 4)) return Refuse(PcbDecodeStatus.UnsupportedVersion);
        if (children < 0 || primitiveTotal < 0) return Refuse(PcbDecodeStatus.InvalidData);
        if (children >= limits.Nodes || primitiveTotal > limits.Primitives) return Refuse(PcbDecodeStatus.LimitExceeded);

        // Bounded capacities are validated above; no unmanaged pointers or input spans escape.
        var pending = new Stack<int>(children + 1);
        var visited = new HashSet<int>(children + 1);
        var ranges = new List<NodeRange>(children + 1);
        var output = new List<PcbTriangle>(primitiveTotal);
        Span<Vector3> vertices = stackalloc Vector3[256];
        pending.Push(16); visited.Add(16);
        int primitives = 0, vertexCount = 0;
        bool matchingLayer = (collider.LayerMask & query.LayerMask) != 0;
        while (pending.TryPop(out int at))
        {
            if (cancellation.IsCancellationRequested) return Refuse(PcbDecodeStatus.Cancelled);
            if (at < 16 || at > bytes.Length - 48) return Refuse(PcbDecodeStatus.InvalidData);
            int compressed = U16(bytes, at + 40), primitiveCount = U16(bytes, at + 42), raw = U16(bytes, at + 44);
            int count = raw + compressed;
            if (count > 256) return Refuse(PcbDecodeStatus.InvalidData);
            if (vertexCount > limits.Vertices - count || primitives > limits.Primitives - primitiveCount
                || ranges.Count >= limits.Nodes) return Refuse(PcbDecodeStatus.LimitExceeded);
            vertexCount += count; primitives += primitiveCount;
            if (primitives > primitiveTotal || ranges.Count > children) return Refuse(PcbDecodeStatus.InvalidData);
            long endWide = (long)at + 48 + 12L * raw + 6L * compressed + 12L * primitiveCount;
            if (endWide > bytes.Length) return Refuse(PcbDecodeStatus.InvalidData);
            int end = (int)endWide;
            var bounds = new PcbBounds(V3(bytes, at + 16), V3(bytes, at + 28));
            if (!bounds.IsValid) return Refuse(PcbDecodeStatus.InvalidData);
            ranges.Add(new(at, end));
            for (int i = 0; i < count; ++i)
            {
                Vector3 local;
                if (i < raw) local = V3(bytes, at + 48 + i * 12);
                else
                {
                    int start = at + 48 + raw * 12 + (i - raw) * 6;
                    // Matches the audited native float decode, including its raw-first indexing.
                    local = bounds.Min + ((bounds.Max - bounds.Min) / 65535f)
                        * new Vector3(U16(bytes, start), U16(bytes, start + 2), U16(bytes, start + 4));
                }
                if (!Numeric.Finite(local)) return Refuse(PcbDecodeStatus.InvalidData);
                vertices[i] = Vector3.Transform(local, collider.World);
                if (!Numeric.Finite(vertices[i])) return Refuse(PcbDecodeStatus.InvalidData);
            }
            int primitiveStart = at + 48 + raw * 12 + compressed * 6;
            for (int i = 0; i < primitiveCount; ++i)
            {
                if ((i & 63) == 0 && cancellation.IsCancellationRequested) return Refuse(PcbDecodeStatus.Cancelled);
                int start = primitiveStart + i * 12;
                int ia = bytes[start], ib = bytes[start + 1], ic = bytes[start + 2];
                if (ia >= count || ib >= count || ic >= count) return Refuse(PcbDecodeStatus.InvalidData);
                Vector3 a = vertices[ia], b = vertices[ib], c = vertices[ic];
                if (!Numeric.Nondegenerate(a, b, c)) return Refuse(PcbDecodeStatus.InvalidData);
                ulong material = BinaryPrimitives.ReadUInt64LittleEndian(bytes[(start + 4)..]);
                ulong effective = collider.Material(material);
                if (matchingLayer && query.Matches(effective)
                    && (!query.Bounds.HasValue || query.Bounds.Value.MayContain(a, b, c)))
                    output.Add(new(a, b, c, material, effective, at, i));
            }
            // Positive node-relative offsets only; no recursion, backward aliases or pointer wrap.
            for (int branch = 0; branch < 2; ++branch)
            {
                int offset = I32(bytes, at + 8 + branch * 4);
                if (offset == 0) continue;
                long childWide = (long)at + offset;
                if (offset < 0 || childWide < end || childWide > bytes.Length - 48)
                    return Refuse(PcbDecodeStatus.InvalidData);
                if (visited.Count >= limits.Nodes) return Refuse(PcbDecodeStatus.LimitExceeded);
                if (!visited.Add((int)childWide)) return Refuse(PcbDecodeStatus.InvalidData);
                pending.Push((int)childWide);
            }
        }
        if (ranges.Count != children + 1 || primitives != primitiveTotal) return Refuse(PcbDecodeStatus.InvalidData);
        ranges.Sort(static (a, b) => a.Begin.CompareTo(b.Begin));
        for (int i = 1; i < ranges.Count; ++i)
            if (ranges[i].Begin < ranges[i - 1].End) return Refuse(PcbDecodeStatus.InvalidData);
        if (cancellation.IsCancellationRequested) return Refuse(PcbDecodeStatus.Cancelled);
        return new(PcbDecodeStatus.Complete, new(output.ToArray(), version, ranges.Count, primitives, vertexCount));
    }

    private static PcbDecodeResult Refuse(PcbDecodeStatus status) => PcbDecodeResult.Refuse(status);
    private static int I32(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadInt32LittleEndian(b[at..]);
    private static int U16(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt16LittleEndian(b[at..]);
    private static float F32(ReadOnlySpan<byte> b, int at) => BitConverter.Int32BitsToSingle(I32(b, at));
    private static Vector3 V3(ReadOnlySpan<byte> b, int at) => new(F32(b, at), F32(b, at + 4), F32(b, at + 8));
}
