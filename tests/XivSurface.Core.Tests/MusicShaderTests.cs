using System.Text;
using XivSurface.Core;

namespace XivSurface.Core.Tests;

public sealed class MusicShaderTests
{
    [Theory]
    [InlineData("MusicVertex")]
    [InlineData("MusicPixel")]
    public void ShippedShadersAgreeOn160ByteMusicConstants(string name)
    {
        var bytes = Read(name); var data = Chunk(bytes, "RDEF");
        Assert.Equal(1, BitConverter.ToInt32(bytes, data));
        var buffer = data + BitConverter.ToInt32(bytes, data + 4);
        Assert.Equal("Music", Name(bytes, data + BitConverter.ToInt32(bytes, buffer)));
        Assert.Equal(160, BitConverter.ToInt32(bytes, buffer + 12));
        var expected = new Dictionary<string, int> { ["ViewProjection"] = 0, ["InverseViewProjection"] = 64, ["Camera"] = 128, ["View"] = 144 };
        var count = BitConverter.ToInt32(bytes, buffer + 4); Assert.Equal(4, count);
        var table = data + BitConverter.ToInt32(bytes, buffer + 8);
        for (var i = 0; i < count; i++)
        {
            var entry = table + i * 40;
            var field = Name(bytes, data + BitConverter.ToInt32(bytes, entry));
            Assert.True(expected.Remove(field, out var offset));
            Assert.Equal(offset, BitConverter.ToInt32(bytes, entry + 4));
            Assert.Equal(field.EndsWith("ViewProjection", StringComparison.Ordinal) ? 64 : 16, BitConverter.ToInt32(bytes, entry + 8));
        }
        Assert.Empty(expected);
    }

    [Fact]
    public void VertexInputIsOnlyWorldPositionAndRgbaFor28ByteUpload()
    {
        var bytes = Read("MusicVertex"); var data = Chunk(bytes, "ISGN");
        Assert.Equal(2, BitConverter.ToInt32(bytes, data));
        var expected = new Dictionary<string, byte> { ["POSITION"] = 7, ["COLOR"] = 15 };
        for (var i = 0; i < 2; i++)
        {
            var entry = data + 8 + i * 24;
            Assert.True(expected.Remove(Name(bytes, data + BitConverter.ToInt32(bytes, entry)), out var mask));
            Assert.Equal(0, BitConverter.ToInt32(bytes, entry + 4));
            Assert.Equal(3, BitConverter.ToInt32(bytes, entry + 12)); // float32
            Assert.Equal(mask, bytes[entry + 20]);
        }
    }

    [Fact]
    public void PixelReadsRealDepthButHasNoDepthOutput()
    {
        var bytes = Read("MusicPixel"); var data = Chunk(bytes, "RDEF");
        var bindings = BitConverter.ToInt32(bytes, data + 8);
        var table = data + BitConverter.ToInt32(bytes, data + 12); var found = false;
        for (var i = 0; i < bindings; i++)
        {
            var entry = table + i * 32;
            if (Name(bytes, data + BitConverter.ToInt32(bytes, entry)) != "SceneDepth") continue;
            Assert.Equal(2, BitConverter.ToInt32(bytes, entry + 4)); // texture
            Assert.Equal(0, BitConverter.ToInt32(bytes, entry + 20)); // t0
            Assert.Equal(1, BitConverter.ToInt32(bytes, entry + 24)); found = true;
        }
        Assert.True(found);
        var output = Chunk(bytes, "OSGN");
        Assert.Equal(1, BitConverter.ToInt32(bytes, output));
        Assert.Equal("SV_TARGET", Name(bytes, output + BitConverter.ToInt32(bytes, output + 8)).ToUpperInvariant());
    }

    private static byte[] Read(string name)
    {
        using var stream = typeof(Footprint).Assembly.GetManifestResourceStream("XivSurface." + name + ".dxbc");
        Assert.NotNull(stream); using var copy = new MemoryStream(); stream.CopyTo(copy); var bytes = copy.ToArray();
        Assert.Equal("DXBC", Encoding.ASCII.GetString(bytes, 0, 4)); Assert.Equal(bytes.Length, BitConverter.ToInt32(bytes, 24)); return bytes;
    }
    private static int Chunk(byte[] bytes, string name)
    {
        for (var i = 0; i < BitConverter.ToInt32(bytes, 28); i++)
        {
            var at = BitConverter.ToInt32(bytes, 32 + 4 * i);
            if (Encoding.ASCII.GetString(bytes, at, 4) == name) return at + 8;
        }
        throw new InvalidOperationException("Missing shader chunk " + name);
    }
    private static string Name(byte[] bytes, int at) => Encoding.ASCII.GetString(bytes, at, Array.IndexOf(bytes, (byte)0, at) - at);
}
