using System.Numerics;
using XivSurface.Core;

namespace XivSurface.Core.Tests;

public sealed class RefinedSolverOwnershipTests
{
    [Fact]
    public void RefinedScratchMatchesAllocatingPipelineAcrossGeometryFeetMusicAndClockChanges()
    {
        var actual = new ClothContactSolver();
        var reference = new FrozenAllocatingClothContactSolver();
        double now = 1;
        for (var frame = 0; frame < 100; frame++)
        {
            var center = new Vector2(frame / 8 * .2f, frame / 11 * -.2f);
            var material = center + new Vector2(.07f, -.09f);
            var coarse = Grid(frame % 3 * 2 + 3, center, frame);
            var factor = frame % 4 + 1;
            now += frame % 9 == 0 ? 0 : .016;
            if (frame == 30) now += .5;
            if (frame == 55) now -= .1;
            if (frame == 75) { actual.Reset(); reference.Reset(); }
            ClothFootContact[] feet = frame % 7 < 4 ? [new(center, .02f, .2f)] : [];
            ClothImpulse[] music = frame % 5 < 2 ? [new(center, now - .2, .8f, .5f)] : [];
            var lift = frame % 17 == 0 ? .15f : 0;
            var fine = SupportVisualRefinement.Refine(coarse, factor);
            SupportVisualRefinement.MapMaterial(fine, center, Vector2.One, material, new(.9f));
            var expected = reference.Update(fine, feet, now, lift, music);
            var result = actual.UpdateRefined(coarse, factor, center, Vector2.One, material, new(.9f), feet, now, lift, music);
            Equal(expected, result);
            Assert.Equal(reference.LastSubsteps, actual.LastSubsteps);
        }
    }

    [Fact]
    public void EveryPublishedArraySurvivesLaterFramesFailuresAndReset()
    {
        var solver = new ClothContactSolver();
        var coarse = Grid(7, Vector2.Zero, 0);
        var sourceCopy = Copy(coarse);
        var publications = new List<(ClothMesh Mesh, ClothMesh Copy)>();
        for (var frame = 0; frame < 8; frame++)
        {
            var result = Solve(solver, coarse, frame / 60d);
            Assert.NotSame(coarse.Positions, result.Positions);
            Assert.NotSame(coarse.Normals, result.Normals);
            Assert.NotSame(coarse.UV, result.UV);
            Assert.NotSame(coarse.Indices, result.Indices);
            Assert.NotSame(coarse.GroundMinimum, result.GroundMinimum);
            foreach (var old in publications)
            {
                Equal(old.Copy, old.Mesh);
                Assert.NotSame(old.Mesh.Positions, result.Positions);
                Assert.NotSame(old.Mesh.Normals, result.Normals);
                Assert.NotSame(old.Mesh.UV, result.UV);
                Assert.NotSame(old.Mesh.Indices, result.Indices);
                Assert.NotSame(old.Mesh.GroundMinimum, result.GroundMinimum);
            }
            publications.Add((result, Copy(result)));
        }
        Assert.Throws<ArgumentException>(() => Solve(solver, coarse, double.NaN));
        solver.Reset();
        Solve(solver, Grid(5, new(.3f), 1), .5);
        foreach (var old in publications) Equal(old.Copy, old.Mesh);
        Equal(sourceCopy, coarse);
    }

    [Fact]
    public void MutatingAnOldOutputCannotContaminateTheNextRefinement()
    {
        var actual = new ClothContactSolver(); var reference = new ClothContactSolver();
        var coarse = Grid(7, Vector2.Zero, 0);
        var first = Solve(actual, coarse, 0); Solve(reference, coarse, 0);
        first.Positions[0] = new(float.NaN);
        first.Normals[0] = new(float.NaN);
        first.UV[0] = new(float.NaN);
        first.Indices[0] = int.MaxValue;
        first.GroundMinimum![0] = float.NaN;
        Equal(Solve(reference, coarse, .016), Solve(actual, coarse, .016));
    }

    [Fact]
    public void WarmRefinementSavesTwoTemporaryVectorArraysPerFrame()
    {
        const int frames = 40, fineSize = 19;
        var coarse = Grid(7, Vector2.Zero, 0);
        var reused = new ClothContactSolver(); var allocating = new ClothContactSolver();
        for (var i = 0; i < 30; i++) { Solve(reused, coarse, i / 60d); Allocate(i); }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 30; i < 30 + frames; i++) GC.KeepAlive(Solve(reused, coarse, i / 60d));
        var reusedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 30; i < 30 + frames; i++) GC.KeepAlive(Allocate(i));
        var allocatingBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange((allocatingBytes - reusedBytes) / frames, fineSize * fineSize * 24, fineSize * fineSize * 24 + 256);

        ClothMesh Allocate(int i)
        {
            var fine = SupportVisualRefinement.Refine(coarse, 3);
            SupportVisualRefinement.MapMaterial(fine, Vector2.Zero, Vector2.One, Vector2.Zero, Vector2.One);
            return allocating.Update(fine, [], i / 60d);
        }
    }

    private static ClothMesh Solve(ClothContactSolver solver, ClothMesh coarse, double now)
        => solver.UpdateRefined(coarse, 3, Vector2.Zero, Vector2.One, Vector2.Zero, Vector2.One, [], now);
    private static ClothMesh Grid(int size, Vector2 center, int frame)
    {
        var contacts = new Vector3[size * size];
        for (var z = 0; z < size; z++) for (var x = 0; x < size; x++)
            contacts[z * size + x] = new(center.X - 1 + 2f * x / (size - 1),
                .01f * MathF.Sin(z * size + x + frame), center.Y - 1 + 2f * z / (size - 1));
        return ClothSurface.Build(center, Vector2.One, 0, contacts, size, frame / 60f, true, diagonalParity: frame % 2);
    }
    private static ClothMesh Copy(ClothMesh mesh) => mesh with
    {
        Positions = mesh.Positions.ToArray(), Normals = mesh.Normals.ToArray(), UV = mesh.UV.ToArray(),
        Indices = mesh.Indices.ToArray(), GroundMinimum = mesh.GroundMinimum?.ToArray(),
    };
    private static void Equal(ClothMesh expected, ClothMesh actual)
    {
        Assert.Equal(expected.Positions, actual.Positions); Assert.Equal(expected.Normals, actual.Normals);
        Assert.Equal(expected.UV, actual.UV); Assert.Equal(expected.Indices, actual.Indices);
        Assert.Equal(expected.GroundMinimum, actual.GroundMinimum); Assert.Equal(expected.Size, actual.Size);
    }
}
