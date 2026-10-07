using System.Numerics;
using XivSurface.Core;

namespace XivSurface.Core.Tests;

public class SurfaceTests
{
    [Theory]
    [InlineData(1280u, 720u, 1920u, 1080u, true)]
    [InlineData(1920u, 1080u, 1920u, 1080u, true)]
    [InlineData(0u, 720u, 1920u, 1080u, false)]
    [InlineData(1921u, 1080u, 1920u, 1080u, false)]
    [InlineData(1920u, 1081u, 1920u, 1080u, false)]
    [InlineData(1920u, 1080u, uint.MaxValue, 1080u, false)]
    public void DepthAllocationMustContainRenderedViewport(uint w, uint h, uint aw, uint ah, bool valid) =>
        Assert.Equal(valid, new DepthDimensions(w, h, aw, ah).IsValid);

    private static readonly Footprint Circle = new(FootprintShape.Circle, Vector2.Zero, new(5), 0, 0, 1);
    private static readonly SurfaceFrame Frame = new(42, 10, 3, CompositeStage.BeforeGameUi, true, true);
    private sealed class Mask : IFloorMask
    {
        public FloorMatch Match = new(Vector3.Zero, 7, 10, 3, true);
        public bool Available = true;
        public bool TryMatch(Vector3 position, out FloorMatch match) { match = Match; return Available; }
    }

    [Fact]
    public void CircleHasSoftBoundaryAndNoSquareCorners()
    {
        Assert.Equal(1, Circle.Coverage(Vector3.Zero));
        Assert.Equal(0.5f, Circle.Coverage(new(4.5f, 0, 0)), 5);
        Assert.Equal(0, Circle.Coverage(new(5, 0, 0)));
        Assert.Equal(0, Circle.Coverage(new(4, 0, 4)));
    }

    [Fact]
    public void RoundedRectangleClipsCornersAndSupportsRotation()
    {
        var shape = new Footprint(FootprintShape.RoundedRectangle, Vector2.Zero, new(4, 2), 0, 1, 0.25f);
        Assert.Equal(1, shape.Coverage(new(3, 0, 0)));
        Assert.Equal(0, shape.Coverage(new(4, 0, 2)));
        var rotated = shape with { Rotation = MathF.PI / 2 };
        Assert.Equal(1, rotated.Coverage(new(0, 0, 3)));
        Assert.Equal(0, rotated.Coverage(new(3, 0, 0)));
    }

    [Fact]
    public void MalformedFootprintsFailClosed()
    {
        foreach (var shape in new[] {
            Circle with { HalfSize = Vector2.Zero }, Circle with { Feather = -1 },
            Circle with { Rotation = float.NaN }, Circle with { Shape = (FootprintShape)55 },
            Circle with { HalfSize = new(5, 3) }, Circle with { CornerRadius = 6 } })
            Assert.Equal(0, shape.Coverage(Vector3.Zero));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DepthReconstructionRoundTripsUnevenSurface(bool reversed)
    {
        var view = Matrix4x4.CreateLookAt(new(0, 10, 12), Vector3.Zero, Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(1, 1.5f, 0.1f, 100);
        if (reversed)
        {
            var reverseZ = Matrix4x4.Identity;
            reverseZ.M33 = -1; reverseZ.M43 = 1;
            projection *= reverseZ;
        }
        var vp = view * projection;
        Assert.True(Matrix4x4.Invert(vp, out var inverse));
        foreach (var p in new[] { new Vector3(-1, -0.7f, -1), new Vector3(0, 1.3f, 0), new Vector3(2, 0.2f, 1) })
        {
            var clip = Vector4.Transform(new Vector4(p, 1), vp);
            var uv = new Vector2((clip.X / clip.W + 1) / 2, (1 - clip.Y / clip.W) / 2);
            Assert.True(DepthProjection.TryReconstruct(uv, clip.Z / clip.W, reversed ? 0 : 1, inverse, out var actual));
            Assert.InRange(Vector3.Distance(p, actual), 0, 0.002f);
        }
    }

    [Fact]
    public void InvalidAndClearDepthNeverCreateAPlane()
    {
        foreach (var z in new[] { 1f, -0.1f, 1.1f, float.NaN })
            Assert.False(DepthProjection.TryReconstruct(new(0.5f), z, 1, Matrix4x4.Identity, out _));
        Assert.False(DepthProjection.TryReconstruct(new(0.5f), 0.5f, 1, default, out _));
    }

    [Fact]
    public void SurfaceKeepsSceneHeightRatherThanNavmeshHeight()
    {
        var mask = new Mask();
        foreach (var p in new[] { new Vector3(-1, -2, 0), new Vector3(0, 0.5f, 0), new Vector3(1, 3, 0) })
        {
            mask.Match = mask.Match with { Position = p + new Vector3(0, 0.01f, 0) };
            Assert.True(SurfaceDecal.TryProject(Frame, new(p, Vector3.UnitY, true), mask, 7, Circle, 0.03f, 0.5f, out var decal));
            Assert.Equal(p, decal.Position);
        }
    }

    [Fact]
    public void LateCompositionAndMissingFrameDataFailClosed()
    {
        foreach (var frame in new[] { Frame with { Stage = CompositeStage.AfterGameUi },
            Frame with { Stage = CompositeStage.Unavailable }, Frame with { DepthValid = false },
            Frame with { FloorClassificationValid = false }, Frame with { TerritoryId = 0 } })
            Assert.False(Project(frame, new Mask()));
    }

    [Fact]
    public void BridgesStaleMeshesHolesAndUnreachableFloorsAreRejected()
    {
        var mask = new Mask();
        foreach (var match in new[] { mask.Match with { LayerId = 8 }, mask.Match with { TerritoryId = 11 },
            mask.Match with { GeometryEpoch = 2 }, mask.Match with { Reachable = false },
            mask.Match with { Position = new(0, -5, 0) }, mask.Match with { Position = new(float.NaN) } })
            Assert.False(Project(Frame, new Mask { Match = match }));
        Assert.False(Project(Frame, new Mask { Available = false }));
    }

    [Fact]
    public void WallsAndUpwardFacingNonFloorObjectsAreRejected()
    {
        Assert.False(SurfaceDecal.TryProject(Frame, new(Vector3.Zero, Vector3.UnitX, true), new Mask(), 7, Circle, 0.03f, 0.5f, out _));
        Assert.False(SurfaceDecal.TryProject(Frame, new(Vector3.Zero, Vector3.UnitY, false), new Mask(), 7, Circle, 0.03f, 0.5f, out _));
        Assert.False(SurfaceDecal.TryProject(Frame, new(Vector3.Zero, Vector3.Zero, true), new Mask(), 7, Circle, 0.03f, 0.5f, out _));
    }

    private static bool Project(SurfaceFrame frame, Mask mask) =>
        SurfaceDecal.TryProject(frame, new(Vector3.Zero, Vector3.UnitY, true), mask, 7, Circle, 0.03f, 0.5f, out _);
}
