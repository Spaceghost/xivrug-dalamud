using System.Buffers.Binary;
using System.Numerics;

namespace XivSurface.Core.Collision.Tests;

internal readonly record struct Primitive(byte A, byte B, byte C, ulong Material = 0x4000);
internal sealed record Node(Vector3[] Raw, (ushort X, ushort Y, ushort Z)[] Packed, Primitive[] Primitives,
    Vector3 Min, Vector3 Max, int Child1 = -1, int Child2 = -1)
{
    internal static Node Triangle => new([Vector3.Zero, Vector3.UnitZ, Vector3.UnitX], [], [new(0, 1, 2)],
        Vector3.Zero, Vector3.One);
    internal int Size => 48 + Raw.Length * 12 + Packed.Length * 6 + Primitives.Length * 12;
}

internal static class Fixture
{
    internal static byte[] Build(params Node[] nodes)
    {
        var offsets = new int[nodes.Length]; int size = 16;
        for (int i = 0; i < nodes.Length; ++i) { offsets[i] = size; size += nodes[i].Size; }
        var bytes = new byte[size];
        I32(bytes, 4, 1); I32(bytes, 8, nodes.Length - 1); I32(bytes, 12, nodes.Sum(n => n.Primitives.Length));
        for (int i = 0; i < nodes.Length; ++i)
        {
            var n = nodes[i]; int at = offsets[i];
            I32(bytes, at + 8, n.Child1 < 0 ? 0 : offsets[n.Child1] - at);
            I32(bytes, at + 12, n.Child2 < 0 ? 0 : offsets[n.Child2] - at);
            V3(bytes, at + 16, n.Min); V3(bytes, at + 28, n.Max);
            U16(bytes, at + 40, n.Packed.Length); U16(bytes, at + 42, n.Primitives.Length); U16(bytes, at + 44, n.Raw.Length);
            int pos = at + 48;
            foreach (var v in n.Raw) { V3(bytes, pos, v); pos += 12; }
            foreach (var v in n.Packed) { U16(bytes, pos, v.X); U16(bytes, pos + 2, v.Y); U16(bytes, pos + 4, v.Z); pos += 6; }
            foreach (var p in n.Primitives)
            { bytes[pos] = p.A; bytes[pos + 1] = p.B; bytes[pos + 2] = p.C; BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(pos + 4), p.Material); pos += 12; }
        }
        return bytes;
    }
    internal static void I32(byte[] b, int at, int value) => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(at), value);
    internal static void U16(byte[] b, int at, int value) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at), checked((ushort)value));
    internal static void F32(byte[] b, int at, float value) => I32(b, at, BitConverter.SingleToInt32Bits(value));
    internal static void V3(byte[] b, int at, Vector3 v) { F32(b, at, v.X); F32(b, at + 4, v.Y); F32(b, at + 8, v.Z); }
}
