using System.Numerics;
using XivSurface.Core;

namespace XivSurface.Core.Tests;

public sealed class StaticRenderDescriptorTests
{
    [Theory]
    [InlineData("bg/ffxiv/test.mdl", true)]
    [InlineData("bg/ex1/a/b/0000.mdl", true)]
    [InlineData("bg/../chara/a.mdl", false)]
    [InlineData("chara/a.mdl", false)]
    [InlineData("bg/a.pcb", false)]
    [InlineData("bg/a.mdl\0", false)]
    [InlineData("bg/a|b.mdl", false)]
    [InlineData("bg/a//b.mdl", false)]
    public void OnlyBoundedActualBackgroundMdlPaths(string path, bool accepted) =>
        Assert.Equal(accepted, StaticRenderDescriptorPolicy.ValidPath(System.Text.Encoding.UTF8.GetBytes(path)));

    [Fact]
    public void TransformMatchesRowVectorScaleRotationThenTranslation()
    {
        Assert.True(StaticRenderDescriptorPolicy.TryTransform(new(10,20,30), Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI/2), new(2,3,4), out var world));
        Assert.True(Vector3.Distance(new(10,20,28), Vector3.Transform(Vector3.UnitX, world)) < .00001);
        Assert.True(StaticRenderDescriptorPolicy.Near(new(-1), new(1), world, new(10,20,30), .1f));
        Assert.False(StaticRenderDescriptorPolicy.Near(new(-1), new(1), world, new(100,20,30), 8));
    }

    [Fact]
    public void RejectMalformedTransformBoundsAndBudgetInputs()
    {
        Assert.False(StaticRenderDescriptorPolicy.TryTransform(Vector3.Zero, default, Vector3.One, out _));
        Assert.False(StaticRenderDescriptorPolicy.TryTransform(Vector3.Zero, Quaternion.Identity, new(-1,1,1), out _));
        Assert.False(StaticRenderDescriptorPolicy.TryTransform(new(float.NaN,0,0), Quaternion.Identity, Vector3.One, out _));
        Assert.False(StaticRenderDescriptorPolicy.Near(Vector3.One, Vector3.Zero, Matrix4x4.Identity, Vector3.Zero, 8));
        Assert.False(StaticRenderDescriptorPolicy.Near(Vector3.Zero, Vector3.One, Matrix4x4.Identity, Vector3.Zero, 31));
        Assert.False(StaticRenderDescriptorPolicy.ValidPath(new byte[513]));
    }

    [Fact]
    public void QuaternionSignDoesNotInventTransformMismatch()
    {
        var q = Quaternion.CreateFromAxisAngle(Vector3.UnitY, .7f);
        Assert.True(StaticRenderDescriptorPolicy.TryTransform(new(1,2,3), q, Vector3.One, out var a));
        Assert.True(StaticRenderDescriptorPolicy.TryTransform(new(1,2,3), -q, Vector3.One, out var b));
        Assert.True(StaticRenderDescriptorPolicy.Equivalent(a,b));
        Assert.False(StaticRenderDescriptorPolicy.Equivalent(a, Matrix4x4.Identity));
    }

    [Fact]
    public void DiagnosticNeverAuthorizesPhysics()
    {
        var descriptor = new StaticRenderDescriptor(StaticRenderKind.TerrainPlate, 1, "bg/a.mdl", 1,
            Matrix4x4.Identity, Vector3.Zero, Vector3.One, StaticRenderIssue.None);
        Assert.False(descriptor.PhysicsAuthorized);
    }

    [Fact]
    public void InvalidBoundsAreNotMisreportedAsOutsideInterest()
    {
        Assert.Equal(StaticRenderBoundsDisposition.Invalid,
            StaticRenderDescriptorPolicy.ClassifyBounds(Vector3.One, Vector3.Zero, Matrix4x4.Identity, new(100), 3));
        Assert.Equal(StaticRenderBoundsDisposition.Invalid,
            StaticRenderDescriptorPolicy.ClassifyBounds(new(float.NaN), Vector3.One, Matrix4x4.Identity, new(100), 3));
        Assert.Equal(StaticRenderBoundsDisposition.OutsideInterest,
            StaticRenderDescriptorPolicy.ClassifyBounds(Vector3.Zero, Vector3.One, Matrix4x4.Identity, new(100), 3));
    }

    [Fact]
    public void BoundsTouchingInterestRemainIncluded()
    {
        Assert.Equal(StaticRenderBoundsDisposition.Near,
            StaticRenderDescriptorPolicy.ClassifyBounds(Vector3.Zero, Vector3.One, Matrix4x4.Identity, new(4, 1, 1), 3));
        Assert.Equal(StaticRenderBoundsDisposition.OutsideInterest,
            StaticRenderDescriptorPolicy.ClassifyBounds(Vector3.Zero, Vector3.One, Matrix4x4.Identity, new(4.01f, 1, 1), 3));
    }

    [Fact]
    public void IgnoredProjectiveOrNonfiniteTransformTermsAreUnresolved()
    {
        foreach (var value in new[] { .1f, float.NaN, float.PositiveInfinity })
        {
            var matrix = Matrix4x4.Identity; matrix.M14 = value;
            Assert.Equal(StaticRenderBoundsDisposition.Invalid,
                StaticRenderDescriptorPolicy.ClassifyBounds(Vector3.Zero, Vector3.One, matrix, new(100), 3));
            matrix = Matrix4x4.Identity; matrix.M44 = value;
            Assert.Equal(StaticRenderBoundsDisposition.Invalid,
                StaticRenderDescriptorPolicy.ClassifyBounds(Vector3.Zero, Vector3.One, matrix, new(100), 3));
        }
    }
}
