using System.Numerics;

namespace XivSurface.Core.Tests;

public sealed class ConformalClothSupportTests
{
    [Fact]
    public void MeasuredSlopeLiftKeepsIndividualFloorHeightsInsteadOfAFlatHighestCorner()
    {
        Vector3[] contacts = [new(0, 0, 0), new(1, .5f, 0), new(0, .25f, 1), new(1, .75f, 1)];
        var faces = new[] { Tri(contacts[0], contacts[1], contacts[2]), Tri(contacts[1], contacts[3], contacts[2]) };
        var center = new LayerFloorHit(new(.5f, .375f, .5f), faces[0]); var proof = new ClothCellCeiling();
        Assert.Equal(LayerQueryResult.Success, proof.Measure(center, contacts[0], contacts[1], contacts[2], contacts[3], faces, out var ceiling));
        var cloth = ClothSurface.Build(new(.5f), new(.5f), .375f, contacts, 2, 0, false, [ceiling], cellLifts: [proof.LastLift]);
        for (var i = 0; i < contacts.Length; i++) Assert.Equal(contacts[i].Y, cloth.GroundMinimum![i], 5);
        var legacy = ClothSurface.Build(new(.5f), new(.5f), .375f, contacts, 2, 0, false, [ceiling]);
        Assert.Equal(.75f, legacy.GroundMinimum![0], 5);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void RealOffCenterPeakResidualKeepsEitherClothDiagonalAboveTheRock(int parity)
    {
        Vector3[] contacts = [new(0, 0, 0), new(1, 0, 0), new(0, 0, 1), new(1, 0, 1)];
        var peak = new Vector3(.25f, .1f, .25f);
        LayerTriangle[] faces = [Tri(contacts[0], contacts[1], peak), Tri(contacts[1], contacts[3], peak),
            Tri(contacts[3], contacts[2], peak), Tri(contacts[2], contacts[0], peak)];
        var face = faces.First(candidate => candidate.TryHeight(new(.5f), out var y) && candidate.Contains(new(.5f, y, .5f)));
        Assert.True(face.TryHeight(new(.5f), out var centerY)); var center = new LayerFloorHit(new(.5f, centerY, .5f), face);
        var proof = new ClothCellCeiling();
        Assert.Equal(LayerQueryResult.Success, proof.Measure(center, contacts[0], contacts[1], contacts[2], contacts[3], faces, out var ceiling));
        Assert.Equal(.1f, proof.LastLift, 6);
        var cloth = ClothSurface.Build(new(.5f), new(.5f), centerY, contacts, 2, 0, false, [ceiling],
            diagonalParity: parity, cellLifts: [proof.LastLift]);
        Assert.All(cloth.Positions, point => Assert.True(point.Y >= peak.Y + ClothSurface.Clearance));
        Assert.All(cloth.GroundMinimum!, minimum => Assert.True(minimum >= peak.Y));
    }

    [Fact]
    public void SharedVerticesKeepTheLargestAdjacentLiftAndMissingProofKeepsItsCeiling()
    {
        var contacts = Grid();
        var cloth = ClothSurface.Build(Vector2.Zero, Vector2.One, 0, contacts, 3, 0, false,
            [1, 1, 1, .9f], cellLifts: [.1f, .2f, .3f, float.NaN]);
        Assert.Equal(.1f, cloth.GroundMinimum![0], 5);
        Assert.Equal(.2f, cloth.GroundMinimum[2], 5);
        Assert.Equal(.3f, cloth.GroundMinimum[6], 5);
        Assert.Equal(.9f, cloth.GroundMinimum[4], 5);
        Assert.Equal(.9f, cloth.GroundMinimum[8], 5);
    }

    [Theory]
    [InlineData(-.1f)]
    [InlineData(float.PositiveInfinity)]
    public void InvalidLiftCannotLowerOrMakeNonfiniteCloth(float lift)
    {
        Assert.Throws<ArgumentException>(() => ClothSurface.Build(Vector2.Zero, Vector2.One, 0,
            Grid(), 3, 0, false, [1, 1, 1, 1], cellLifts: [lift, 0, 0, 0]));
    }

    [Fact]
    public void RollingAndCompactSnapshotsPreserveOnlyFreshConformalProof()
    {
        var cache = new RollingClothSupport(); var queries = new Queries();
        var identity = new SupportQueryIdentity(1, 1, 0, new(0, .18f, 0), .35f, 1.35f);
        for (var frame = 0; frame < 90; frame++) cache.Update(identity, Vector2.Zero, frame / 30d, 100, queries);
        Assert.True(cache.TrySnapshot(Vector2.Zero, 89 / 30d, out var full));
        Assert.All(full!.CellLifts!, lift => Assert.Equal(.125f, lift));
        Assert.True(cache.TrySupportedSnapshot(Vector2.Zero, 89 / 30d, out var compact));
        Assert.Equal(full.CellLifts, compact!.CellLifts);
        queries.Lift = float.NaN;
        for (var frame = 90; frame < 210; frame++) cache.Update(identity, Vector2.Zero, frame / 30d, 100, queries);
        Assert.True(cache.TrySnapshot(Vector2.Zero, 209 / 30d, out var refreshed));
        Assert.Null(refreshed!.CellLifts);
        Assert.All(refreshed.CellCeilings, ceiling => Assert.Equal(.75f, ceiling));
        Assert.False(cache.TrySupportedSnapshot(Vector2.Zero, 10, out _));
    }

    private sealed class Queries : IClothSupportQueries, IConformalClothSupportQueries
    {
        public float Lift = .125f;
        public float LastCellLift => Lift;
        public bool TryVertex(SupportQueryIdentity identity, Vector2 nominal, out Vector3 contact)
        { contact = new(nominal.X, 0, nominal.Y); return true; }
        public bool TryCell(SupportQueryIdentity identity, Vector3 a, Vector3 b, Vector3 c, Vector3 d, out float ceiling)
        { ceiling = .75f; return true; }
    }

    private static Vector3[] Grid() => Enumerable.Range(0, 9).Select(i => new Vector3(i % 3 - 1, 0, i / 3 - 1)).ToArray();
    private static LayerTriangle Tri(Vector3 a, Vector3 b, Vector3 c)
    {
        var normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
        if (normal.Y < 0) normal = -normal;
        return new(a, b, c, normal);
    }
}
