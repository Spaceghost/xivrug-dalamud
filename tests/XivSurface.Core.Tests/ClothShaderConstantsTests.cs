using System.Text;
using XivSurface.Core;

namespace XivSurface.Core.Tests;

public class ClothShaderConstantsTests
{
    [Fact]
    public void CompiledVertexShaderConsumesBothMeasuredFloorAndSharedContactCeiling()
    {
        using var stream = typeof(Footprint).Assembly.GetManifestResourceStream("XivSurface.ClothVertex.dxbc");
        Assert.NotNull(stream);
        using var memory = new MemoryStream(); stream.CopyTo(memory);
        var bytes = memory.ToArray();
        var expected = new Dictionary<(string, int), byte>
        {
            [("POSITION", 0)] = 7, [("NORMAL", 0)] = 7,
            [("TEXCOORD", 0)] = 3, [("TEXCOORD", 1)] = 1, [("TEXCOORD", 2)] = 1,
        };
        var found = false;
        for (var i = 0; i < BitConverter.ToInt32(bytes, 28); i++)
        {
            var chunk = BitConverter.ToInt32(bytes, 32 + i * 4);
            if (Encoding.ASCII.GetString(bytes, chunk, 4) != "ISGN") continue;
            var data = chunk + 8;
            Assert.Equal(5, BitConverter.ToInt32(bytes, data));
            for (var j = 0; j < 5; j++)
            {
                var entry = data + 8 + j * 24;
                var name = NameAt(bytes, data + BitConverter.ToInt32(bytes, entry));
                var index = BitConverter.ToInt32(bytes, entry + 4);
                Assert.True(expected.Remove((name,index),out var mask), $"Unexpected vertex input {name}{index}");
                Assert.Equal(3, BitConverter.ToInt32(bytes, entry + 12)); // float32
                Assert.Equal(mask, bytes[entry + 20]);
            }
            found = true;
        }
        Assert.True(found, "A stale shader without per-vertex contact constraints must not be packaged.");
        Assert.Empty(expected);
    }
    [Theory]
    [InlineData("XivSurface.ClothVertex.dxbc")]
    [InlineData("XivSurface.IndexedClothVertex.dxbc")]
    [InlineData("XivSurface.ClothPixel.dxbc")]
    public void AllCompiledStagesAgreeOnRouteAndWorldLightingConstants(string resource)
    {
        using var stream = typeof(Footprint).Assembly.GetManifestResourceStream(resource);
        Assert.NotNull(stream);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var bytes = memory.ToArray();
        Assert.Equal("DXBC", Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal(bytes.Length, BitConverter.ToInt32(bytes, 24));
        var expected = new Dictionary<string, int>
        {
            ["ViewProjection"] = 0, ["InverseViewProjection"] = 64,
            ["Camera"] = 128, ["View"] = 144, ["Footprint"] = 160, ["Style"] = 176,
            ["MapCoordinates"] = 192, ["MapView"] = 208, ["RouteArea"] = 224, ["Material"] = 240,
            ["RouteColor"] = 256, ["RouteProgress"] = 272, ["RouteGuide"] = 288, ["RouteAhead"] = 304,
            ["Finish"] = 320,
            ["Foot0"] = 336, ["Foot1"] = 352, ["Foot2"] = 368, ["Foot3"] = 384,
            ["LightAmbient"] = 400, ["LightSun"] = 416, ["LightMoon"] = 432, ["LightDirection"] = 448,
            ["Wisp0"] = 464, ["Wisp1"] = 480, ["Wisp2"] = 496,
            ["NativeShadow"] = 512,
        };
        var found = false;
        for (var i = 0; i < BitConverter.ToInt32(bytes, 28); i++)
        {
            var chunk = BitConverter.ToInt32(bytes, 32 + i * 4);
            if (Encoding.ASCII.GetString(bytes, chunk, 4) != "RDEF") continue;
            var data = chunk + 8;
            Assert.Equal(1, BitConverter.ToInt32(bytes, data));
            var buffer = data + BitConverter.ToInt32(bytes, data + 4);
            Assert.Equal("Cloth", NameAt(bytes, data + BitConverter.ToInt32(bytes, buffer)));
            Assert.Equal(528, BitConverter.ToInt32(bytes, buffer + 12));
            var variables = BitConverter.ToInt32(bytes, buffer + 4);
            Assert.Equal(expected.Count, variables);
            var table = data + BitConverter.ToInt32(bytes, buffer + 8);
            for (var v = 0; v < variables; v++)
            {
                var descriptor = table + v * 40;
                var name = NameAt(bytes, data + BitConverter.ToInt32(bytes, descriptor));
                Assert.True(expected.Remove(name, out var offset), $"Unexpected or duplicate field {name}");
                Assert.Equal(offset, BitConverter.ToInt32(bytes, descriptor + 4));
                Assert.Equal(name.EndsWith("ViewProjection", StringComparison.Ordinal) ? 64 : 16,
                    BitConverter.ToInt32(bytes, descriptor + 8));
            }
            Assert.Empty(expected);
            found = true;
        }
        Assert.True(found, "Both cloth stages must carry constant-buffer reflection.");
    }

    private static string NameAt(byte[] bytes, int offset) =>
        Encoding.ASCII.GetString(bytes, offset, Array.IndexOf(bytes, (byte)0, offset) - offset);
}
