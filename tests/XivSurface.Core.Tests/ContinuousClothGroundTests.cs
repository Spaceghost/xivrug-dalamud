using System.Numerics;

namespace XivSurface.Core.Tests;

public sealed class ContinuousClothGroundTests
{
    [Fact]
    public void ActualPlanarEvidencePreservesRampInsteadOfHighestCornerPlateau()
    {
        Vector3[] contacts = [new(0, 0, 0), new(.4f, .14f, 0), new(0, 0, .4f), new(.4f, .14f, .4f)];
        var mesh = ClothSurface.Build(new(.2f), new(.2f), 0, contacts, 2, 0, false,
            [.14f], planarCells: [true]);
        Assert.NotNull(mesh.GroundMinimum);
        for (var i = 0; i < contacts.Length; i++)
        {
            Assert.Equal(contacts[i].Y, mesh.GroundMinimum[i], 6);
            Assert.Equal(contacts[i].Y + ClothSurface.Clearance, mesh.Positions[i].Y, 6);
        }
        var unknown = ClothSurface.Build(new(.2f), new(.2f), 0, contacts, 2, 0, false, [.14f]);
        Assert.All(unknown.GroundMinimum!, y => Assert.Equal(.14f, y, 6));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void RefinedGroundUsesTheSameAffineTriangleAsTheFabric(int parity)
    {
        Vector3[] contacts = [new(0, 0, 0), new(.4f, .14f, 0), new(0, .08f, .4f), new(.4f, .22f, .4f)];
        var coarse = ClothSurface.Build(new(.2f), new(.2f), 0, contacts, 2, 0, false,
            [.22f], diagonalParity: parity, planarCells: [true]);
        var fine = SupportVisualRefinement.Refine(coarse, 3);
        Assert.NotNull(fine.GroundMinimum);
        for (var i = 0; i < fine.Positions.Length; i++)
        {
            var p = fine.Positions[i];
            Assert.Equal(.35f * p.X + .2f * p.Z, fine.GroundMinimum[i], 6);
            Assert.Equal(ClothSurface.Clearance, p.Y - fine.GroundMinimum[i], 6);
        }
        Assert.Equal(54, fine.Indices.Length);
    }

    [Fact]
    public void MalformedGroundBoundsCannotReachTheRenderer()
    {
        var mesh = ClothSurface.Build(Vector2.Zero, Vector2.One, 0, new float[4], 2, 0, false);
        Assert.Throws<ArgumentException>(() => SupportVisualRefinement.Refine(mesh with { GroundMinimum = [0] }, 3));
        Assert.Throws<ArgumentException>(() => SupportVisualRefinement.Refine(mesh with { GroundMinimum = [0, 0, float.NaN, 0] }, 3));
        Assert.Throws<ArgumentException>(() => ClothSurface.Build(Vector2.Zero, Vector2.One, 0,
            new Vector3[4], 2, 0, false, planarCells: [true]));
    }

    [Fact]
    public void ClothUsesMeasuredBootsWithoutTheLegacyExpandingTransparencyEnvelope()
    {
        ClothFootContact[] measured = [new(new(-.15f,-.1f),.004f,.22f),new(new(-.15f,.1f),.004f,.22f),
            new(new(.15f,-.1f),.004f,.22f),new(new(.15f,.1f),.004f,.22f)];
        var frame = new ClothFootRenderFrame(129, 10, measured);
        var actual = new ClothFootContact[4];
        Assert.True(frame.TryGetClothContacts(129, 10.02, actual, out var gate));
        Assert.Equal(ClothFootRenderGate.Allowed, gate);
        Assert.Equal(measured, actual);
        Assert.True(frame.TryGetClothRenderContacts(129, 10.02, actual, out _));
        Assert.InRange(actual[0].Radius, .62f, .621f);
        Assert.InRange(actual[0].FootY, -.397f, -.395f);
        // Flattened fabric fills this swept area; there is no pixel exclusion.
        Assert.True(actual[0].Radius < .7f);
        Assert.True(frame.TryGetRenderContacts(129, 10.02, actual, out _));
        Assert.True(actual[0].Radius > .8f);
        Assert.False(frame.TryGetClothContacts(130, 10.02, actual, out gate));
        Assert.Equal(ClothFootRenderGate.WrongZone, gate);
        Assert.All(actual, value => Assert.Equal(default, value));
        Assert.False(frame.TryGetClothContacts(129, 11, actual, out gate));
        Assert.Equal(ClothFootRenderGate.Expired, gate);
    }
}
