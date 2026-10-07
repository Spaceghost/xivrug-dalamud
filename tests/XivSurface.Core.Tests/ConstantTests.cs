using System.Numerics;
using System.Runtime.InteropServices;
using XivSurface.Core;

namespace XivSurface.Core.Tests;

public class ConstantTests
{
    [Fact]
    public void CompiledShaderDeclaresMatchingConstantBuffer()
    {
        // Read DXBC reflection, not merely a duplicate size in the HLSL source.
        var bytes = SurfaceShader.ReadBytecode();
        Assert.Equal("DXBC", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal(bytes.Length, BitConverter.ToInt32(bytes, 24));
        var chunks = BitConverter.ToInt32(bytes, 28);
        var found = false;
        for (var i = 0; i < chunks; i++)
        {
            var chunk = BitConverter.ToInt32(bytes, 32 + i * 4);
            if (System.Text.Encoding.ASCII.GetString(bytes, chunk, 4) != "RDEF") continue;
            var data = chunk + 8;
            var count = BitConverter.ToInt32(bytes, data);
            var buffers = data + BitConverter.ToInt32(bytes, data + 4);
            Assert.Equal(1, count);
            var name = data + BitConverter.ToInt32(bytes, buffers);
            var end = Array.IndexOf(bytes, (byte)0, name);
            Assert.Equal("Surface", System.Text.Encoding.ASCII.GetString(bytes, name, end - name));
            Assert.Equal(Marshal.SizeOf<SurfaceConstants>(), BitConverter.ToInt32(bytes, buffers + 12));
            var expected = new Dictionary<string, int>
            {
                ["InverseViewProjection"] = 0, ["ViewOrigin"] = 64, ["ViewSize"] = 72,
                ["DepthOrigin"] = 80, ["DepthSize"] = 88, ["FloorMaskSize"] = 96,
                ["FrameValidated"] = 104, ["Shape"] = 108, ["CenterXZ"] = 112,
                ["HalfSize"] = 120, ["RotationCS"] = 128, ["CornerRadius"] = 136,
                ["Feather"] = 140, ["ClearDepth"] = 144, ["FloorTolerance"] = 148,
                ["MinimumNormalY"] = 152, ["Opacity"] = 156,
                ["RugGeometry"] = 160, ["RugMaterial"] = 176,
            };
            var variables = BitConverter.ToInt32(bytes, buffers + 4);
            Assert.Equal(expected.Count, variables);
            var variableTable = data + BitConverter.ToInt32(bytes, buffers + 8);
            for (var v = 0; v < variables; v++)
            {
                var descriptor = variableTable + v * 40; // shader-model-5 RDEF variable record
                var variableName = data + BitConverter.ToInt32(bytes, descriptor);
                var variableEnd = Array.IndexOf(bytes, (byte)0, variableName);
                var key = System.Text.Encoding.ASCII.GetString(bytes, variableName, variableEnd - variableName);
                Assert.True(expected.Remove(key, out var offset), $"Unexpected or duplicated shader variable {key}");
                Assert.Equal(offset, BitConverter.ToInt32(bytes, descriptor + 4));
            }
            Assert.Empty(expected);
            found = true;
        }
        Assert.True(found, "DXBC must carry constant-buffer reflection.");
    }

    [Fact]
    public void LayoutMatchesShaderRegisters()
    {
        Assert.Equal(192, Marshal.SizeOf<SurfaceConstants>());
        Assert.Equal(64, (int)Marshal.OffsetOf<SurfaceConstants>(nameof(SurfaceConstants.ViewOrigin)));
        Assert.Equal(80, (int)Marshal.OffsetOf<SurfaceConstants>(nameof(SurfaceConstants.DepthOriginX)));
        Assert.Equal(96, (int)Marshal.OffsetOf<SurfaceConstants>(nameof(SurfaceConstants.FloorMaskWidth)));
        Assert.Equal(104, (int)Marshal.OffsetOf<SurfaceConstants>(nameof(SurfaceConstants.FrameValidated)));
        Assert.Equal(112, (int)Marshal.OffsetOf<SurfaceConstants>(nameof(SurfaceConstants.CenterXZ)));
        Assert.Equal(128, (int)Marshal.OffsetOf<SurfaceConstants>(nameof(SurfaceConstants.RotationCS)));
        Assert.Equal(144, (int)Marshal.OffsetOf<SurfaceConstants>(nameof(SurfaceConstants.ClearDepth)));
    }

    [Fact]
    public void ConstantsValidateStageAndRejectInvalidMatrices()
    {
        Assert.True(Create(Matrix4x4.Identity, CompositeStage.BeforeGameUi, out var c));
        Assert.Equal(1u, c.FrameValidated);
        Assert.Equal(1280u, c.DepthWidth);
        Assert.Equal(new Vector2(1, 0), c.RotationCS);
        Assert.False(Create(Matrix4x4.Identity, CompositeStage.AfterGameUi, out c));
        Assert.Equal(0u, c.FrameValidated);
        Assert.False(Create(default, CompositeStage.BeforeGameUi, out _));
        var bad = Matrix4x4.Identity;
        bad.M21 = float.NaN;
        Assert.False(Create(bad, CompositeStage.BeforeGameUi, out _));
    }

    private static bool Create(Matrix4x4 inverse, CompositeStage stage, out SurfaceConstants c) =>
        SurfaceConstants.TryCreate(new(1, 10, 1, stage, true, true),
            new(FootprintShape.Circle, Vector2.Zero, new(5), 0, 0, 1), inverse,
            Vector2.Zero, new(1920, 1080), new(1280, 720, 1920, 1080), 256, 256,
            0, 0.03f, 0.5f, 0.8f, out c);
}
