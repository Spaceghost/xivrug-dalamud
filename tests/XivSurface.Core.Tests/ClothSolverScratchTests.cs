using System.Numerics;

namespace XivSurface.Core.Tests;

public sealed class ClothSolverScratchTests
{
    [Fact]
    public void ScratchReuseMatchesFrozenPhysicsAcrossSizeFootMusicFloorAndClockChanges()
    {
        var actual = new ClothContactSolver();
        var expected = new FrozenAllocatingClothContactSolver();
        double now = 1;
        for (var frame = 0; frame < 120; frame++)
        {
            var size = frame % 30 < 10 ? 9 : frame % 30 < 20 ? 7 : 11;
            var center = new Vector2((frame / 12) * .25f, (frame / 18) * -.25f);
            var target = Grid(size, center);
            for (var i = 0; i < target.Positions.Length; i++)
            {
                var point = target.Positions[i];
                var floor = point.X * .04f + (frame / 15) * .005f;
                target.GroundMinimum![i] = floor;
                target.Positions[i] = point with { Y = floor + .035f + .03f * MathF.Sin(i * .7f) };
            }
            now += frame % 7 == 0 ? 0 : frame % 9 == 0 ? .04 : 1d / 120;
            if (frame == 39) now += 1; // long-gap reset
            if (frame == 67) now -= .1; // backward clock
            if (frame == 88) { actual.Reset(); expected.Reset(); }
            ClothFootContact[] feet = frame % 12 < 7
                ? [new(center + new Vector2(0, -.15f), .004f, .22f),
                   new(center + new Vector2(0, .15f), .004f, .22f)] : [];
            ClothImpulse[] impulses = frame % 10 < 5
                ? [new(center, Math.Max(0, now - .3), .8f, .5f)] : [];
            var lift = frame % 17 == 0 ? .2f : 0;
            var reference = expected.Update(target, feet, now, lift, impulses);
            var result = actual.Update(target, feet, now, lift, impulses);
            Assert.Equal(reference.Positions, result.Positions);
            Assert.Equal(reference.Normals, result.Normals);
            Assert.Equal(expected.LastSubsteps, actual.LastSubsteps);
            Assert.Same(target.UV, result.UV);
            Assert.Same(target.Indices, result.Indices);
            Assert.Same(target.GroundMinimum, result.GroundMinimum);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WarmAllocationContainsImmutableOutputNotThreeNewScratchArrays(bool withFeet)
    {
        const int size = 34, iterations = 40;
        var target = Grid(size, Vector2.Zero);
        var solver = new ClothContactSolver();
        ClothFootContact[] feet = withFeet
            ? [new(new(0, -.15f), .004f, .22f), new(new(0, .15f), .004f, .22f)] : [];
        for (var i = 0; i < 20; i++) solver.Update(target, feet, i / 120d);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 20; i < iterations + 20; i++)
            GC.KeepAlive(solver.Update(target, feet, i / 120d));
        var perUpdate = (GC.GetAllocatedBytesForCurrentThread() - before) / iterations;
        // Fresh positions and normals remain required immutable publications.
        // The margin covers array/record headers; three float[count] scratch
        // arrays would exceed this budget by roughly13.5KiB at the default mesh.
        Assert.InRange(perUpdate, size * size * 24, size * size * 24 + 1024);
    }

    [Fact]
    public void SizeChangesAndResetNeverMutatePreviouslyPublishedGeometry()
    {
        var solver = new ClothContactSolver();
        var target = Grid(9, Vector2.Zero);
        var originalTarget = target.Positions.ToArray();
        var first = solver.Update(target, [], 0);
        var savedPositions = first.Positions.ToArray();
        var savedNormals = first.Normals.ToArray();
        foreach (var size in new[] { 7, 11, 9, 5, 12 })
        {
            var next = solver.Update(Grid(size, new(.2f, 0)), [new(Vector2.Zero, 0, .3f)], .1);
            Assert.NotSame(first.Positions, next.Positions);
            Assert.NotSame(first.Normals, next.Normals);
        }
        solver.Reset();
        solver.Update(target, [new(Vector2.Zero, 0, .3f)], .2);
        Assert.Equal(originalTarget, target.Positions);
        Assert.Equal(savedPositions, first.Positions);
        Assert.Equal(savedNormals, first.Normals);
    }

    private static ClothMesh Grid(int size, Vector2 center) =>
        ClothSurface.Build(center, Vector2.One, 0, new float[size * size], size, 0, false)
            with { GroundMinimum = new float[size * size] };
}
