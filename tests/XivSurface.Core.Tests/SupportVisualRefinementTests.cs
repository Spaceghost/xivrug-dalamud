using System.Numerics;
using XivSurface.Core;
using Xunit;

namespace RugSupportPrototype;

public sealed class SupportVisualRefinementTests
{
    [Theory]
    [InlineData(2, false)] [InlineData(3, false)] [InlineData(4, false)]
    [InlineData(2, true)] [InlineData(3, true)] [InlineData(4, true)]
    public void EveryFineTriangleStaysOnItsOriginalFoldPlane(int factor, bool main)
    {
        var coarse = new ClothMesh([new(0, 0, 0), new(1, 0, 0), new(0, 0, 1), new(1, 1, 1)],
            Enumerable.Repeat(Vector3.UnitY, 4).ToArray(), [new(0, 0), new(1, 0), new(0, 1), new(1, 1)],
            main ? [0, 3, 1, 0, 2, 3] : [0, 2, 1, 1, 2, 3], 2);
        var fine = SupportVisualRefinement.Refine(coarse, factor);
        Assert.Equal((factor + 1) * (factor + 1), fine.Positions.Length);
        Assert.Equal(factor * factor * 6, fine.Indices.Length);
        foreach (var p in fine.Positions) Assert.InRange(Math.Abs(p.Y - Height(p.X, p.Z)), 0, 1e-6f);
        for (var i = 0; i < fine.Indices.Length; i += 3)
        {
            var a = fine.Positions[fine.Indices[i]]; var b = fine.Positions[fine.Indices[i + 1]]; var c = fine.Positions[fine.Indices[i + 2]];
            var center = (a + b + c) / 3;
            Assert.InRange(Math.Abs(center.Y - Height(center.X, center.Z)), 0, 1e-6f);
            // Centroid and each edge midpoint are on the SAME original folded
            // surface, not a new interpolating plane through its two halves.
            foreach (var p in new[] { (a + b) * .5f, (a + c) * .5f, (b + c) * .5f })
                Assert.InRange(Math.Abs(p.Y - Height(p.X, p.Z)), 0, 1e-6f);
        }
        float Height(float x, float z) => main ? Math.Min(x, z) : Math.Max(0, x + z - 1);
    }

    [Fact]
    public void FineVisualDensityDoesNotAddAnyCollisionCalls()
    {
        var coarse = ClothSurface.Build(Vector2.Zero, new(2), 0, new float[13 * 13], 13, 0, false);
        var fine = SupportVisualRefinement.Refine(coarse, 3);
        Assert.Equal(37, fine.Size); Assert.Equal(2592, fine.Indices.Length / 3);
        Assert.All(fine.Positions, p => Assert.Equal(ClothSurface.Clearance, p.Y, 5));
    }

    [Fact]
    public void InvalidTopologyCannotBeSilentlyReplaced()
    {
        var coarse = ClothSurface.Build(Vector2.Zero, Vector2.One, 0, new float[4], 2, 0, false);
        coarse.Indices[0] = 3;
        Assert.Throws<ArgumentException>(() => SupportVisualRefinement.Refine(coarse, 2));
    }

    [Fact]
    public void MovingMaterialDoesNotMoveCollisionVerticesAndPreservesCompressedRestUv()
    {
        var mesh = ClothSurface.Build(Vector2.Zero, new(2.4f), 0, new float[13 * 13], 13, 0, false);
        mesh.Positions[0] = new(-1, .035f, -1); // compressed nominal(-2.4,-2.4)
        var fine = SupportVisualRefinement.Refine(mesh, 3); var original = fine.Positions.ToArray();
        SupportVisualRefinement.MapMaterial(fine, Vector2.Zero, new(2.4f), new(.2f, .1f), new(2));
        Assert.Equal(original, fine.Positions);
        Assert.Equal(new Vector2(-.15f, -.125f).X, fine.UV[0].X, 5);
        Assert.Equal(new Vector2(-.15f, -.125f).Y, fine.UV[0].Y, 5);
    }

    [Fact]
    public void GlobalParityAndMovingPhaseKeepMeasuredXZAndFloorClearance()
    {
        var contacts = new Vector3[5 * 5];
        for (var z = 0; z < 5; z++) for (var x = 0; x < 5; x++) contacts[z * 5 + x] = new(x * .4f, x == 2 ? .1f : 0, z * .4f);
        var before = contacts.ToArray();
        var mesh = ClothSurface.Build(new(.8f), new(.8f), 0, contacts, 5, 1, true, diagonalParity: 1,
            materialCenter: new(.9f), materialHalf: new(.6f));
        Assert.Equal(new[] { 0, 6, 1, 0, 5, 6 }, mesh.Indices[..6]);
        Assert.Equal(before, contacts);
        for (var i = 0; i < contacts.Length; i++)
        {
            Assert.Equal(contacts[i].X, mesh.Positions[i].X); Assert.Equal(contacts[i].Z, mesh.Positions[i].Z);
            Assert.True(mesh.Positions[i].Y >= contacts[i].Y + ClothSurface.Clearance);
        }
        var fine = SupportVisualRefinement.Refine(mesh, 3);
        Assert.All(fine.Positions, p => Assert.True(float.IsFinite(p.Y)));
    }
}
