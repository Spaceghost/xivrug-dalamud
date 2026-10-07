using System.Text;
using XivSurface.Core;

namespace XivSurface.Core.Tests;

public class LiveRugShaderTests
{
    [Fact]
    public void CompiledLiveShaderCarriesEdgeAndTravelSettingsAtExpectedOffsets()
    {
        using var stream = typeof(Footprint).Assembly.GetManifestResourceStream("XivSurface.LiveRug.dxbc");
        Assert.NotNull(stream);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var bytes = memory.ToArray();
        Assert.Equal("DXBC", Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal(bytes.Length, BitConverter.ToInt32(bytes, 24));

        // The live background shader has a distinct ABI from SurfaceDecal.
        // Check the shipped bytecode, so forgetting to rebuild it after adding
        // the settings vector cannot silently leave the controls ineffective.
        var expected = new Dictionary<string, int>
        {
            ["InverseViewProjection"] = 0,
            ["View"] = 64,
            ["Footprint"] = 80,
            ["Style"] = 96,
            ["Material"] = 112,
            ["Edges"] = 128,
            ["Ground"] = 144,
            ["MapCoordinates"] = 160,
            ["MapView"] = 176,
            ["RouteArea"] = 192,
            ["Travel"] = 208,
            ["RouteColor"] = 224,
            ["RouteProgress"] = 240,
            ["RouteGuide"] = 256,
            ["RouteAhead"] = 272,
            ["Finish"] = 288,
            ["Foot0"] = 304, ["Foot1"] = 320, ["Foot2"] = 336, ["Foot3"] = 352,
        };
        var found = false;
        var chunks = BitConverter.ToInt32(bytes, 28);
        for (var i = 0; i < chunks; i++)
        {
            var chunk = BitConverter.ToInt32(bytes, 32 + i * 4);
            if (Encoding.ASCII.GetString(bytes, chunk, 4) != "RDEF") continue;
            var data = chunk + 8;
            Assert.Equal(1, BitConverter.ToInt32(bytes, data));
            var buffer = data + BitConverter.ToInt32(bytes, data + 4);
            Assert.Equal("Rug", NameAt(bytes, data + BitConverter.ToInt32(bytes, buffer)));
            Assert.Equal(368, BitConverter.ToInt32(bytes, buffer + 12));
            var variables = BitConverter.ToInt32(bytes, buffer + 4);
            Assert.Equal(expected.Count, variables);
            var table = data + BitConverter.ToInt32(bytes, buffer + 8);
            for (var v = 0; v < variables; v++)
            {
                var descriptor = table + v * 40;
                var name = NameAt(bytes, data + BitConverter.ToInt32(bytes, descriptor));
                Assert.True(expected.Remove(name, out var offset), $"Unexpected or duplicated field {name}");
                Assert.Equal(offset, BitConverter.ToInt32(bytes, descriptor + 4));
                Assert.Equal(name == "InverseViewProjection" ? 64 : 16, BitConverter.ToInt32(bytes, descriptor + 8));
            }
            Assert.Empty(expected);
            found = true;
        }
        Assert.True(found, "LiveRug DXBC must carry constant-buffer reflection.");
    }

    private static string NameAt(byte[] bytes, int offset) =>
        Encoding.ASCII.GetString(bytes, offset, Array.IndexOf(bytes, (byte)0, offset) - offset);
}
