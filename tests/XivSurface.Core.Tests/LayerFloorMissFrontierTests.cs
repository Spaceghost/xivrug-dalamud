using System.Numerics;
using XivSurface.Core;

namespace XivSurface.Core.Tests;

public sealed class LayerFloorMissFrontierTests
{
    [Theory]
    [InlineData(0f, 1.5f, .5f)]
    [InlineData(-1.5f, 0f, .5f)]
    [InlineData(0f, -1.7f, .59f)]
    public void MissedDirectProbeDiscoversRealAdjacentCreaseWithoutChangingAllowance(float oldSlope, float nextSlope, float targetX)
    {
        var scene = new Scene(oldSlope, nextSlope, true);
        var discovery = scene.Seed();
        var wanted = new Vector2(targetX, 0);
        Assert.True(discovery.Layer.TryProbe(wanted, out var original));
        scene.Allow(1);
        Assert.False(scene.TryFloor(original, out _)); // The exact original query misses.
        var result = LayerQueryResult.Unknown; var total = 0; LayerFloorHit hit = default;
        for (var frame = 0; frame < 8 && result != LayerQueryResult.Success; frame++)
        {
            scene.Allow(1);
            result = discovery.Query(wanted, scene, out hit);
            Assert.InRange(scene.Calls, 0, 1); total += scene.Calls;
        }
        Assert.Equal(LayerQueryResult.Success, result);
        Assert.Equal(targetX * nextSlope, hit.Position.Y, 5);
        Assert.Equal(2, discovery.Layer.Count);
        Assert.InRange(total, 3, 4);
        Assert.All(scene.Probes, p =>
        {
            Assert.Equal(1.35f, p.Length);
            Assert.InRange(p.StartY - p.MinimumY, 1.3499f, 1.3501f);
        });
    }

    [Fact]
    public void MissingFrontierEndsTheRequestRatherThanInventingAConnectedFloor()
    {
        var scene = new Scene(0, 1.5f, false);
        var discovery = scene.Seed();
        for (var attempt = 0; attempt < 20; attempt++)
        {
            scene.Allow(100);
            Assert.Equal(LayerQueryResult.Unknown, discovery.Query(new(.5f, 0), scene, out _));
            Assert.InRange(scene.Calls, 1, 2);
            Assert.Equal(0, discovery.Pending); Assert.Equal(1, discovery.Layer.Count);
        }
    }

    [Fact]
    public void DisconnectedUpperFaceRemainsUnknownWithoutJoiningTheLayer()
    {
        var scene = new Scene(0, 0, true, raised: .2f);
        var discovery = scene.Seed();
        for (var attempt = 0; attempt < 20; attempt++)
        {
            scene.Allow(8);
            Assert.NotEqual(LayerQueryResult.Success, discovery.Query(new(.5f, 0), scene, out _));
            Assert.InRange(scene.Calls, 0, 8);
            Assert.Equal(1, discovery.Layer.Count);
        }
    }

    [Fact]
    public void DirectMissCannotUseARealFrontierHitToJumpOntoADisconnectedDeck()
    {
        var scene = new Scene(0, 1.5f, true, raised: .2f);
        var discovery = scene.Seed();
        Assert.True(discovery.Layer.TryProbe(new(.5f, 0), out var direct));
        scene.Allow(1); Assert.False(scene.TryFloor(direct, out _));
        Assert.True(discovery.Layer.TryFrontier(new(.5f, 0), out var frontier));
        scene.Allow(1); Assert.True(scene.TryFloor(frontier, out var realDeck));
        Assert.True(realDeck.Position.Y > .2f);
        for (var attempt = 0; attempt < 20; attempt++)
        {
            scene.Allow(8);
            Assert.Equal(LayerQueryResult.Unknown, discovery.Query(new(.5f, 0), scene, out _));
            Assert.InRange(scene.Calls, 2, 8);
            Assert.Equal(1, discovery.Layer.Count); Assert.Equal(0, discovery.Pending);
        }
    }

    [Fact]
    public void NonadvancingFrontierHitCannotKeepTheSameRequestPendingForever()
    {
        var scene = new Scene(0, 1.5f, true) { ReturnOldEdgeAtFrontier = true };
        var discovery = scene.Seed();
        scene.Allow(100);
        Assert.Equal(LayerQueryResult.Unknown, discovery.Query(new(.5f, 0), scene, out _));
        Assert.InRange(scene.Calls, 2, 3);
        Assert.Equal(0, discovery.Pending); Assert.Equal(1, discovery.Layer.Count);
    }

    [Fact]
    public void DeferredFrontierSurvivesZeroBudgetAndLongerThanCandidateFreshness()
    {
        var scene = new Scene(0, 1.5f, true); var discovery = scene.Seed();
        scene.Allow(1);
        Assert.Equal(LayerQueryResult.Pending, discovery.Query(new(.5f, 0), scene, out _));
        Assert.True(discovery.BeginFrame(scene.Root, .2));
        scene.Allow(0);
        Assert.Equal(LayerQueryResult.Pending, discovery.Query(new(.5f, 0), scene, out _));
        Assert.Equal(0, scene.Calls);
        scene.Allow(3);
        Assert.Equal(LayerQueryResult.Success, discovery.Query(new(.5f, 0), scene, out _));
        Assert.InRange(scene.Calls, 2, 3);
    }

    private sealed class Scene : ILayerFloorScene
    {
        private readonly LayerTriangle root, next;
        private readonly bool hasNext;
        private int limit;
        public int Calls { get; private set; }
        public List<ClothFloorProbe> Probes { get; } = [];
        public bool ReturnOldEdgeAtFrontier { get; init; }
        public bool CanQuery => Calls < limit;
        public LayerFloorHit Root => new(new(-.25f, -.25f * slope, 0), root);
        private readonly float slope;
        public Scene(float oldSlope, float nextSlope, bool hasNext, float raised = 0)
        {
            slope = oldSlope; this.hasNext = hasNext;
            root = Triangle(new(-1, -oldSlope, 0), new(0, 0, 1), new(0, 0, -1));
            next = Triangle(new(0, raised, -1), new(0, raised, 1), new(1, nextSlope + raised, 0));
            Assert.True(root.Walkable); Assert.True(next.Walkable);
        }
        public LayerFloorDiscovery Seed()
        {
            var result = new LayerFloorDiscovery(); Assert.True(result.Seed(Root)); return result;
        }
        public void Allow(int value) { Calls = 0; limit = value; }
        public bool TryFloor(ClothFloorProbe probe, out LayerFloorHit hit)
        {
            Assert.True(CanQuery); Calls++; Probes.Add(probe); hit = default;
            if (ReturnOldEdgeAtFrontier && probe.Position.X is > 0 and < .01f)
            { hit = new(Vector3.Zero, root); return true; }
            var highest = float.NegativeInfinity;
            foreach (var triangle in hasNext ? new[] { root, next } : new[] { root })
            {
                if (!triangle.TryHeight(probe.Position, out var y) || y > probe.StartY || y < probe.MinimumY
                    || y <= highest || !triangle.Contains(new(probe.Position.X, y, probe.Position.Y))) continue;
                highest = y; hit = new(new(probe.Position.X, y, probe.Position.Y), triangle);
            }
            return hit.Valid;
        }
        public bool TryWall(Vector3 from, Vector3 to, out LayerTriangle triangle)
        { Assert.True(CanQuery); Calls++; triangle = default; return false; }
        private static LayerTriangle Triangle(Vector3 a, Vector3 b, Vector3 c) =>
            new(a, b, c, Vector3.Normalize(Vector3.Cross(b - a, c - a)));
    }
}
