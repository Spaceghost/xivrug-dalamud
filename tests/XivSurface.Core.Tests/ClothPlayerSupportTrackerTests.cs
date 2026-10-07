using System.Numerics;

namespace XivSurface.Core.Tests;

public sealed class ClothPlayerSupportTrackerTests
{
    [Theory]
    [InlineData(.2f)] [InlineData(1.5f)]
    public void JumpBeyondOneYalmReadsOriginalLayerInsteadOfAnOverlappingDeck(float deckHeight)
    {
        var scene = new Scene(); scene.Faces.AddRange(Flat(0)); scene.Faces.AddRange(Flat(deckHeight));
        var tracker = new ClothPlayerSupportTracker(); var discovery = new LayerFloorDiscovery();
        Seed(tracker, discovery, scene);
        var lift = new CarpetLiftController();
        lift.Update(0, 0, 0, true);
        for (var frame = 1; frame <= 120; frame++)
        {
            var now = frame / 60d;
            var player = new Vector3((float)now, 2.2f * MathF.Sin((float)now / 2 * MathF.PI), 0);
            if (frame == 120) player.Y = 0;
            Assert.Equal(ClothPlayerSupportQuery.RetainedLayer, tracker.Plan(1, player, now, true, out var unused));
            Assert.Equal(default, unused);
            Assert.Equal(LayerQueryResult.Success, discovery.Query(new(player.X, player.Z), scene, out var hit));
            Assert.Equal(0, hit.Position.Y);
            Assert.True(tracker.Confirm(hit, discovery.Layer));
            discovery.BeginFrame(hit, now);
            var actualLift = lift.Update(player.Y, hit.Position.Y, now, true);
            if (frame == 60)
            {
                Assert.True(actualLift > 2);
                ClothFloorQueryPolicy.TryPlayerProbe(player, out var oldProbe);
                Assert.False(ClothFloorQueryPolicy.Accept(oldProbe, hit.Position, Vector3.UnitY, out _));
                if (deckHeight > 1)
                    Assert.True(ClothFloorQueryPolicy.Accept(oldProbe, new(player.X, deckHeight, 0), Vector3.UnitY, out _));
            }
        }
        Assert.Equal(ClothPlayerSupportQuery.NearFeet, tracker.Plan(1, new(2, 0, 0), 2.01, false, out var landing));
        Assert.True(scene.TryFloor(landing, out var grounded));
        Assert.True(tracker.Confirm(grounded));
        Assert.Equal(ClothPlayerSupportQuery.RetainedLayer, tracker.Plan(1, new(2, 1.5f, 0), 2.1, true, out _));
    }

    [Fact]
    public void MissingCurrentCollisionNeverBecomesCachedSupport()
    {
        var scene = new Scene(); scene.Faces.AddRange(Flat(0));
        var tracker = new ClothPlayerSupportTracker(); var discovery = new LayerFloorDiscovery();
        Seed(tracker, discovery, scene);
        scene.Faces.Clear();
        Assert.Equal(ClothPlayerSupportQuery.RetainedLayer, tracker.Plan(1, new(0, 2, 0), .1, true, out _));
        Assert.Equal(LayerQueryResult.Unknown, discovery.Query(Vector2.Zero, scene, out var missing));
        Assert.False(tracker.Confirm(missing, discovery.Layer));
        Assert.Equal(ClothPlayerSupportQuery.Unknown, tracker.Plan(1, new(0, 2, 0), .36, true, out _));
    }

    [Fact]
    public void RepeatedFreshAirborneHitsDoNotExtendTheTakeoffWindow()
    {
        var scene = new Scene(); scene.Faces.AddRange(Flat(0));
        var tracker = new ClothPlayerSupportTracker(); var discovery = new LayerFloorDiscovery();
        Seed(tracker, discovery, scene);
        for (var frame = 1; frame <= 31; frame++)
        {
            var now = frame / 10d;
            Assert.Equal(ClothPlayerSupportQuery.RetainedLayer, tracker.Plan(1, new(0, 2, 0), now, true, out _));
            Assert.Equal(LayerQueryResult.Success, discovery.Query(Vector2.Zero, scene, out var hit));
            Assert.True(tracker.Confirm(hit, discovery.Layer));
            discovery.BeginFrame(hit, now);
        }
        Assert.Equal(ClothPlayerSupportQuery.Unknown, tracker.Plan(1, new(0, 2, 0), 3.2, true, out _));
    }

    [Theory]
    [InlineData(2u, 0f, 2f, .1)]
    [InlineData(1u, 17f, 2f, .1)]
    [InlineData(1u, 0f, 9f, .1)]
    [InlineData(1u, 0f, 2f, .5)]
    [InlineData(1u, 0f, 2f, -.1)]
    public void DifferentZoneTeleportExcessHeightPauseAndClockRollbackDiscardTakeoff(uint zone, float x, float y, double now)
    {
        var scene = new Scene(); scene.Faces.AddRange(Flat(0));
        var tracker = new ClothPlayerSupportTracker(); var discovery = new LayerFloorDiscovery();
        Seed(tracker, discovery, scene);
        Assert.Equal(ClothPlayerSupportQuery.Unknown, tracker.Plan(zone, new(x, y, 0), now, true, out _));
        Assert.False(tracker.Confirm(new(Vector3.Zero, scene.Faces[0]), discovery.Layer));
    }

    [Fact]
    public void AirborneWithoutGroundedEvidenceCannotStartFromAnArbitraryFloor()
    {
        var tracker = new ClothPlayerSupportTracker();
        Assert.Equal(ClothPlayerSupportQuery.Unknown, tracker.Plan(1, new(0, 2, 0), 0, true, out _));
        Assert.False(tracker.Confirm(new(Vector3.Zero, Flat(0)[0])));
        Assert.Equal(ClothPlayerSupportQuery.NearFeet, tracker.Plan(1, new(0, .5f, 0), .1, false, out _));
        Assert.True(tracker.Confirm(new(Vector3.Zero, Flat(0)[0])));
        Assert.Equal(ClothPlayerSupportQuery.Unknown, tracker.Plan(1, new(0, 2, 0), .2, true, out _));
    }

    [Fact]
    public void AirborneConfirmRequiresTheLocallyReachedActualHeight()
    {
        var scene = new Scene(); scene.Faces.AddRange(Flat(0));
        var tracker = new ClothPlayerSupportTracker(); var discovery = new LayerFloorDiscovery();
        Seed(tracker, discovery, scene);
        Assert.Equal(ClothPlayerSupportQuery.RetainedLayer, tracker.Plan(1, new(0, 2, 0), .1, true, out _));
        Assert.False(tracker.Confirm(new(new(0, .2f, 0), Flat(.2f)[0]), discovery.Layer));
        Assert.Equal(ClothPlayerSupportQuery.RetainedLayer, tracker.Plan(1, new(0, 2, 0), .2, true, out _));
        Assert.False(tracker.Confirm(new(Vector3.Zero, scene.Faces[0])));
    }

    private static void Seed(ClothPlayerSupportTracker tracker, LayerFloorDiscovery discovery, Scene scene)
    {
        Assert.Equal(ClothPlayerSupportQuery.NearFeet, tracker.Plan(1, Vector3.Zero, 0, false, out var probe));
        Assert.True(scene.TryFloor(probe, out var floor));
        Assert.True(tracker.Confirm(floor)); Assert.True(discovery.Seed(floor));
        Assert.True(discovery.BeginFrame(floor, 0));
    }

    private static LayerTriangle[] Flat(float height)
    {
        var a = new Vector3(-20, height, -20); var b = new Vector3(20, height, -20);
        var c = new Vector3(-20, height, 20); var d = new Vector3(20, height, 20);
        return [new(a, b, c, Vector3.UnitY), new(b, d, c, Vector3.UnitY)];
    }

    private sealed class Scene : ILayerFloorScene
    {
        public readonly List<LayerTriangle> Faces = [];
        public bool CanQuery => true;
        public bool TryFloor(ClothFloorProbe probe, out LayerFloorHit hit)
        {
            hit = default; var highest = float.NegativeInfinity;
            foreach (var triangle in Faces)
            {
                // Exact downward-ray intersection with each actual triangle.
                if (!triangle.TryHeight(probe.Position, out var y) || y < probe.StartY - probe.Length || y > probe.StartY
                    || y <= highest || !triangle.Contains(new(probe.Position.X, y, probe.Position.Y))) continue;
                highest = y; hit = new(new(probe.Position.X, y, probe.Position.Y), triangle);
            }
            return hit.Valid && ClothFloorQueryPolicy.Accept(probe, hit.Position, hit.Triangle.Normal, out _);
        }
        public bool TryWall(Vector3 from, Vector3 to, out LayerTriangle triangle)
        { triangle = default; return false; }
    }
}
