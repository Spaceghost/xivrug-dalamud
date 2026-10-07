using System.Numerics;

namespace XivSurface.Core.Tests;

public sealed class ClothContactSolverTests
{
    private static ClothFootContact[] Boot(float sole = .004f, float x = 0) =>
        [new(new(x, -.15f), sole, .22f), new(new(x, .15f), sole, .22f)];

    [Fact]
    public void FullBootCoreAndHeelToeBridgePressToThinContinuousContact()
    {
        var feet = Boot();
        foreach (var at in new[] { new Vector3(0, .2f, 0), new Vector3(.20f, .2f, 0), new Vector3(0, .2f, .3f) })
        {
            var sample = ClothContactConstraint.ProjectHeight(at, 0, feet);
            Assert.Equal(.001f, sample.Height, 5); Assert.Equal(1, sample.Weight);
        }
        var outer = ClothContactConstraint.ProjectHeight(new(.275f, .2f, 0), 0, feet);
        Assert.Equal(.5f, outer.Weight, 4); Assert.Equal(.1005f, outer.Height, 4);
        Assert.Equal(.2f, ClothContactConstraint.ProjectHeight(new(.5f, .2f, 0), 0, feet).Height);
    }

    [Theory] [InlineData(-.15f)] [InlineData(-.02f)] [InlineData(0)]
    public void ConservativeSoleBelowFloorMeansThinContactNotExclusion(float sole)
    {
        var sample = ClothContactConstraint.ProjectHeight(new(0, .25f, 0), 0, Boot(sole));
        Assert.Equal(ClothContactConstraint.Clearance, sample.Height); Assert.Equal(1, sample.Weight);
    }

    [Fact]
    public void RaisedFootDoesNotPressGroundButLiftedCarpetCanMeetIt()
    {
        var feet = Boot(1.004f);
        Assert.Equal(.2f, ClothContactConstraint.ProjectHeight(new(0, .2f, 0), 0, feet).Height);
        var raised = ClothContactConstraint.ProjectHeight(new(0, 1.08f, 0), 0, feet, 1);
        Assert.Equal(1.001f, raised.Height, 5); Assert.Equal(1, raised.Weight);
        Assert.True(raised.Height >= ClothContactConstraint.Clearance);
    }

    [Fact]
    public void OverlappingCapsulesAreOrderIndependentAndNeverLowerThroughGround()
    {
        var a = Boot(.04f, -.1f); var b = Boot(-.02f, .1f);
        var one = ClothContactConstraint.ProjectHeight(new(0, .3f, 0), .02f, [.. a, .. b]);
        var two = ClothContactConstraint.ProjectHeight(new(0, .3f, 0), .02f, [.. b, .. a]);
        Assert.Equal(one, two); Assert.Equal(.021f, one.Height, 5);
    }

    [Fact]
    public void TrianglePaddingProtectsBarycentricInteriorWithoutRemovingFaces()
    {
        var vertices = new[] { new Vector3(0, .035f, 0), new Vector3(.133f, .035f, 0), new Vector3(0, .035f, .133f) };
        var mesh = new ClothMesh(vertices, new Vector3[3], new Vector2[3], [0, 1, 2], 2, new float[3]);
        ClothFootContact[] feet = [new(Vector2.Zero, 0, .11f)];
        const float bx = .10f / .133f, bz = .01f / .133f;
        var old = vertices.Select(v => ClothContactConstraint.ProjectHeight(v, 0, feet).Height).ToArray();
        var oldInterior = old[0] * (1 - bx - bz) + old[1] * bx + old[2] * bz;
        Assert.True(oldInterior > .005f); // concrete under-foot interpolation defect
        var padding = ClothContactConstraint.RequiredContactPadding(mesh);
        Assert.InRange(padding, MathF.Sqrt(2 * .133f * .133f), .189f);
        var corrected = vertices.Select(v => ClothContactConstraint.ProjectHeight(v, 0, feet, contactPadding: padding).Height).ToArray();
        var interior = corrected[0] * (1 - bx - bz) + corrected[1] * bx + corrected[2] * bz;
        Assert.InRange(interior, ClothContactConstraint.Clearance - .0000001f, ClothContactConstraint.Clearance + .0000001f);
        Assert.All(corrected, y => Assert.Equal(ClothContactConstraint.Clearance, y));
        Assert.Equal(new[] { 0, 1, 2 }, mesh.Indices);
        Assert.All(mesh.Positions, p => Assert.Equal(.035f, p.Y));
    }

    [Fact]
    public void PaddingUsesActualDiagonalAndRejectsUnboundedOrInvalidTriangles()
    {
        var target = Grid();
        Assert.Equal(MathF.Sqrt(2 * .125f * .125f), ClothContactConstraint.RequiredContactPadding(target), 6);
        Assert.Throws<ArgumentException>(() => ClothContactConstraint.RequiredContactPadding(target with { Indices = [0, 1, 9999] }));
        Assert.Throws<ArgumentException>(() => ClothContactConstraint.RequiredContactPadding(target with { Indices = [0, 1] }));
        var invalid = target.Positions.ToArray(); invalid[0] = new(float.NaN);
        Assert.Throws<ArgumentException>(() => ClothContactConstraint.RequiredContactPadding(target with { Positions = invalid }));
        invalid[0] = new(1000, 0, 0);
        Assert.Throws<ArgumentException>(() => ClothContactConstraint.RequiredContactPadding(target with { Positions = invalid }));
        Assert.Throws<ArgumentException>(() => ClothContactConstraint.ProjectHeight(Vector3.Zero, 0, [], contactPadding: float.NaN));
        Assert.Throws<ArgumentException>(() => ClothContactConstraint.ProjectHeight(Vector3.Zero, 0, [], contactPadding: -1));
    }

    [Fact]
    public void SlopedFullContactFollowsAffineFloorNotSoleHeightPlateau()
    {
        var mesh = Triangle([0, -.2f, 0]);
        ClothFootContact[] feet = [new(new(.09975f, .009975f), -.15f, .11f)];
        var ceilings = ClothContactConstraint.BuildRenderCeilings(mesh, feet, 0);
        var weights = new Vector3(.175f, .75f, .075f);
        var old = mesh.GroundMinimum!.Select(g => Math.Max(g + .001f, feet[0].FootY - .001f)).ToArray();
        Assert.InRange(Blend(old, weights) - feet[0].FootY, .036f, .038f); // former3.7cm plateau defect
        Assert.Equal(-.149f, Blend(ceilings, weights), 5); // actual floor(-.15)+thin1mm
        for (var i = 0; i < 3; i++)
            Assert.Equal(mesh.GroundMinimum![i] + ClothContactConstraint.Clearance, ceilings[i], 6);
        var padded = ClothContactConstraint.RequiredContactPadding(mesh);
        for (var i = 0; i < 3; i++)
            Assert.Equal(mesh.GroundMinimum![i] + .001f,
                ClothContactConstraint.ProjectHeight(mesh.Positions[i], mesh.GroundMinimum[i], feet, contactPadding: padded).Height, 6);
    }

    [Fact]
    public void TriangleHeightEligibilityIsUniformAcrossSteeperFloorAndRaisedFolds()
    {
        var slope = Triangle([0, -.5f, 0]);
        ClothFootContact[] feet = [new(new(.04f, .02f), -.15f, .11f)];
        // Local low-corner eligibility alone would miss this corner even
        // though the foot meets the same triangle's sloped interior.
        Assert.Equal(0, ClothContactConstraint.ProjectHeight(slope.Positions[1], -.5f, feet).Weight);
        var ceilings = ClothContactConstraint.BuildRenderCeilings(slope, feet, 0);
        Assert.Equal(-.499f, ceilings[1], 6);
        var flat = Triangle([0, 0, 0]);
        ClothFootContact[] raised = [new(new(.04f, .02f), .2f, .11f)];
        Assert.All(ClothContactConstraint.BuildRenderCeilings(flat, raised, 0), y => Assert.Equal(float.MaxValue, y));
        var folds = flat.Positions.ToArray(); folds[0].Y = .25f;
        Assert.All(ClothContactConstraint.BuildRenderCeilings(flat with { Positions = folds }, raised, 0),
            y => Assert.Equal(.001f, y, 6));
    }

    [Fact]
    public void SharedEdgeUsesOneReducedCeilingWhenOnlyOneTriangleHasRaisedFold()
    {
        var mesh = new ClothMesh([new(0, .3f, 0), new(.133f, .035f, 0), new(0, .035f, .133f), new(.133f, .035f, .133f)],
            new Vector3[4], new Vector2[4], [0, 1, 2, 1, 3, 2], 2, new float[4]);
        ClothFootContact[] feet = [new(new(.04f, .02f), .2f, .11f)];
        var ceilings = ClothContactConstraint.BuildRenderCeilings(mesh, feet, 0);
        Assert.Equal(.001f, ceilings[1], 6); Assert.Equal(.001f, ceilings[2], 6);
        Assert.Equal(float.MaxValue, ceilings[3]);
        var reordered = ClothContactConstraint.BuildRenderCeilings(mesh with { Indices = [1, 3, 2, 0, 1, 2] }, feet, 0);
        Assert.Equal(ceilings, reordered);
        for (var i = 0; i <= 10; i++)
        {
            var t = i / 10f;
            var firstEdge = ceilings[mesh.Indices[1]] * (1 - t) + ceilings[mesh.Indices[2]] * t;
            var secondEdge = ceilings[mesh.Indices[3]] * (1 - t) + ceilings[mesh.Indices[5]] * t;
            Assert.Equal(firstEdge, secondEdge);
        }
    }

    [Fact]
    public void LiftedSlopeUsesOneSafeOffsetAndPreservesAllGeometricMinima()
    {
        var mesh = Triangle([0, -.2f, 0]);
        ClothFootContact[] feet = [new(new(.04f, .02f), .85f, .11f)];
        var ceilings = ClothContactConstraint.BuildRenderCeilings(mesh, feet, 1);
        for (var i = 0; i < ceilings.Length; i++)
        {
            Assert.Equal(.849f, ceilings[i] - mesh.GroundMinimum![i], 5);
            Assert.True(ceilings[i] >= mesh.GroundMinimum[i] + .001f);
            Assert.True(ceilings[i] <= feet[0].FootY - .001f + .000001f);
        }
    }

    [Fact]
    public void RenderBuilderAcceptsSweptCapsulesButRawPressureStaysPhysical()
    {
        ClothFootContact[] boots = [.. Boot(), .. Boot(.004f, .5f)];
        var frame = new ClothFootRenderFrame(7, 1, boots);
        Span<ClothFootContact> swept = stackalloc ClothFootContact[4];
        Assert.True(frame.TryGetClothRenderContacts(7, 1.03, swept, out _));
        Assert.True(swept[0].Radius > ClothFootClearance.MaximumBootRadius);
        var mesh = Triangle([0, 0, 0]);
        var moved = mesh.Positions.Select(p => p + new Vector3(.65f, 0, 0)).ToArray();
        mesh = mesh with { Positions = moved };
        var ceiling = ClothContactConstraint.BuildRenderCeilings(mesh, swept, 0);
        Assert.All(ceiling, y => Assert.Equal(.001f, y, 6));
        Assert.Equal(0, ClothContactConstraint.ProjectHeight(mesh.Positions[0], 0, swept).Weight);
        Assert.Throws<ArgumentException>(() => ClothContactConstraint.BuildRenderCeilings(mesh,
            [new(Vector2.Zero, 0, ClothFootRenderFrame.MaximumClothRenderRadius + .01f)], 0));
        // A long heel-to-toe boot protects its middle, not only its end discs.
        ClothFootContact[] capsule = [new(new(0, -.36f), 0, .11f), new(new(0, .36f), 0, .11f)];
        var tiny = Triangle([0, 0, 0]);
        tiny = tiny with { Positions = tiny.Positions.Select(p => new Vector3(p.X * .1f, p.Y, p.Z * .1f)).ToArray() };
        Assert.All(ClothContactConstraint.BuildRenderCeilings(tiny, capsule, 0), y => Assert.Equal(.001f, y, 6));
    }

    [Fact]
    public void OptimizedRenderBoundsMatchFrozenAlgorithmOnRandomizedGeometryAndMalformedPairs()
    {
        var random = new Random(724911);
        for (var sample = 0; sample < 80; sample++)
        {
            var mesh = Grid();
            var translation = sample % 4 == 0 ? new Vector2(10000, -10000) : Vector2.Zero;
            for (var i = 0; i < mesh.Positions.Length; i++)
            {
                var position = mesh.Positions[i];
                var ground = -.2f + .1f * position.X + .15f * position.Z;
                mesh.GroundMinimum![i] = ground;
                mesh.Positions[i] = new(position.X + translation.X, ground + .01f + (float)random.NextDouble() * .4f,
                    position.Z + translation.Y);
            }
            var feet = new ClothFootContact[4];
            for (var pair = 0; pair < 4; pair += 2)
            {
                var center = new Vector2((float)random.NextDouble() * 3 - 1.5f, (float)random.NextDouble() * 3 - 1.5f) + translation;
                var foot = new ClothFootContact(center, (float)random.NextDouble() * .8f - .4f,
                    .11f + (float)random.NextDouble() * 1.1f);
                feet[pair] = foot;
                feet[pair + 1] = foot with { Center = center + new Vector2(.06f, .25f) };
                switch (sample % 6)
                {
                    case 1: feet[pair + 1] = feet[pair + 1] with { Radius = foot.Radius + .00005f, FootY = foot.FootY + .00005f }; break;
                    case 2: feet[pair + 1] = feet[pair + 1] with { Center = center + Vector2.One }; break;
                    case 3: feet[pair + 1] = default; break;
                    case 4: feet[pair + 1] = feet[pair + 1] with { FootY = foot.FootY + .1f }; break;
                    case 5: feet[pair + 1] = feet[pair + 1] with { Center = center + new Vector2(.0005f, .0005f) }; break;
                }
            }
            var lift = (float)random.NextDouble() * .8f;
            var expected = FrozenRenderCeilings(mesh, feet, lift);
            var actual = ClothContactConstraint.BuildRenderCeilings(mesh, feet, lift);
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void RawEndpointGatherRingsRemainEvenWhenCapsuleRingIsZero()
    {
        ClothFootContact[] feet = [new(new(0, -.29f), 0, .22f), new(new(0, .29f), 0, .22f)];
        var projection = ClothContactConstraint.ProjectHeight(new(.33f, .2f, 0), 0, feet);
        Assert.Equal(0, projection.Weight);
        Assert.True(projection.GatherWeight > .99f);
    }

    [Fact]
    public void SolverKeepsEveryFaceUvAndWallConstrainedHorizontalPosition()
    {
        var target = Grid(); var saved = target.Positions.ToArray(); var solver = new ClothContactSolver();
        var mesh = solver.Update(target, Boot(), 0);
        Assert.Same(target.Indices, mesh.Indices); Assert.Same(target.UV, mesh.UV);
        Assert.Same(target.GroundMinimum, mesh.GroundMinimum); Assert.Equal(saved, target.Positions);
        Assert.All(mesh.Positions, p => Assert.True(p.Y >= ClothContactConstraint.Clearance));
        for (var i = 0; i < mesh.Positions.Length; i++)
        {
            Assert.Equal(target.Positions[i].X, mesh.Positions[i].X); Assert.Equal(target.Positions[i].Z, mesh.Positions[i].Z);
            Assert.InRange(mesh.Normals[i].Length(), .9999f, 1.0001f);
        }
        Assert.Equal(.001f, mesh.Positions[Center(mesh)].Y, 5);
        Assert.Equal(target.Positions[0], mesh.Positions[0]);
    }

    [Fact]
    public void PressedFoldGathersOutsideTheFootAndReleasesSmoothly()
    {
        var target = Grid(); var solver = new ClothContactSolver(); var mesh = solver.Update(target, Boot(), 0);
        for (var i = 1; i <= 60; i++) mesh = solver.Update(target, Boot(), i / 120d);
        Assert.Equal(.001f, mesh.Positions[Center(mesh)].Y, 5);
        Assert.True(mesh.Positions.Max(p => p.Y) > .04f); // actual neighbouring vertical gather, not deleted triangles
        Assert.True(mesh.Positions.Max(p => p.Y) < .035f + ClothContactSolver.MaximumGatherHeight + .005f);
        var released = solver.Update(target, [], .5 + 1d / 120);
        Assert.InRange(released.Positions[Center(mesh)].Y, .001f, .015f); // no immediate return snap
        for (var i = 2; i <= 300; i++) released = solver.Update(target, [], .5 + i / 120d);
        Assert.InRange(released.Positions[Center(mesh)].Y, .034f, .036f);
    }

    [Fact]
    public void FloorBoundsWinEvenWhenCellEvidenceCannotFitBelowTheFoot()
    {
        var target = Grid(); Array.Fill(target.GroundMinimum!, .14f);
        var mesh = new ClothContactSolver().Update(target, Boot(), 0);
        Assert.All(mesh.Positions, p => Assert.True(p.Y >= .141f - .000001f));
        // This exposes the upstream cell-ceiling plateau; the solver must not
        // pretend the14cm supplied bound is the actual low end of a ramp.
        Assert.Equal(.141f, mesh.Positions[Center(mesh)].Y, 5);
    }

    [Fact]
    public void TimestepsAreBoundedNoTimeBankAndSameTimeDoesNotAdvance()
    {
        var target = Grid(); var solver = new ClothContactSolver(); solver.Update(target, Boot(), 0);
        var mesh = solver.Update(target, Boot(), .2);
        Assert.Equal(ClothContactSolver.MaximumSubsteps, solver.LastSubsteps);
        var frozen = solver.Update(target, Boot(), .2);
        Assert.Equal(0, solver.LastSubsteps); Assert.Equal(mesh.Positions, frozen.Positions);
        var resumed = solver.Update(target, [], 100);
        Assert.Equal(0, solver.LastSubsteps); Assert.All(resumed.Positions, p => Assert.True(float.IsFinite(p.Y)));
        var backward = solver.Update(target, Boot(), 99);
        Assert.Equal(0, solver.LastSubsteps); Assert.Equal(.001f, backward.Positions[Center(backward)].Y, 5);
    }

    [Fact]
    public void RegularFrameRatesProduceSimilarPersistentContactResponse()
    {
        var a = Run(30); var b = Run(60); var c = Run(144);
        Assert.True(a.Positions.Zip(b.Positions, Vector3.Distance).Max() < .001f);
        Assert.True(a.Positions.Zip(c.Positions, Vector3.Distance).Max() < .002f);
        static ClothMesh Run(int fps)
        {
            var target = Grid(); var solver = new ClothContactSolver(); var mesh = solver.Update(target, Boot(), 0);
            for (var i = 1; i <= fps; i++) mesh = solver.Update(target, i < fps / 2 ? Boot() : [], i / (double)fps);
            return mesh;
        }
    }

    [Fact]
    public void MovingWindowReusesOnlyExactValidatedNodesAndDoesNotMoveThemLaterally()
    {
        var target = Grid(); var solver = new ClothContactSolver(); solver.Update(target, Boot(), 0);
        var shifted = Grid();
        for (var i = 0; i < shifted.Positions.Length; i++) shifted.Positions[i].X += .125f;
        var mesh = solver.Update(shifted, Boot(), 1d / 60);
        for (var i = 0; i < mesh.Positions.Length; i++)
        { Assert.Equal(shifted.Positions[i].X, mesh.Positions[i].X); Assert.Equal(shifted.Positions[i].Z, mesh.Positions[i].Z); }
    }

    [Fact]
    public void InvalidSupportIsRejectedRatherThanInventingAFlatFloor()
    {
        var solver = new ClothContactSolver(); var target = Grid();
        Assert.Throws<ArgumentException>(() => solver.Update(target with { GroundMinimum = null }, Boot(), 0));
        Assert.Throws<ArgumentException>(() => solver.Update(target with { GroundMinimum = [0] }, Boot(), 0));
        target.GroundMinimum![0] = float.NaN;
        Assert.Throws<ArgumentException>(() => solver.Update(target, Boot(), 0));
        Assert.Throws<ArgumentException>(() => ClothContactConstraint.ProjectHeight(Vector3.Zero, float.NaN, []));
    }

    [Fact]
    public void ActualAudioEventsExciteTravelingClothButFeetStayInsideThinContactLayerAtPeak()
    {
        var target = Grid(); var active = new ClothContactSolver(); var quiet = new ClothContactSolver();
        ClothImpulse[] music = [new(Vector2.Zero, 0, 1, .3f), new(new(.2f, 0), .1, 1, .8f)];
        var mesh = active.Update(target, Boot(), 0); quiet.Update(target, Boot(), 0);
        var largest = 0f;
        for (var i = 1; i <= 120; i++)
        {
            mesh = active.Update(target, Boot(), i / 120d, impulses: music);
            var baseline = quiet.Update(target, Boot(), i / 120d);
            largest = Math.Max(largest, mesh.Positions.Zip(baseline.Positions, Vector3.Distance).Max());
            // Contact lies on the same thin affine floor at every music peak.
            Assert.Equal(ClothContactConstraint.Clearance, mesh.Positions[Center(mesh)].Y, 6);
            Assert.All(mesh.Positions, p => Assert.True(p.Y >= ClothContactConstraint.Clearance));
        }
        Assert.True(largest > .001f);
        Assert.Equal(mesh.Positions, active.Update(target, Boot(), 1, impulses: music).Positions);
    }

    [Fact]
    public void NoEventsMeansNoMusicAndMalformedFutureExpiredOrExcessEventsAreBounded()
    {
        Assert.Equal(0, ClothImpulse.Displacement(Vector2.Zero, 100, []));
        ClothImpulse[] bad = [new(Vector2.Zero, 2, 1, .5f), new(Vector2.Zero, 0, float.NaN, .5f),
            new(Vector2.Zero, 0, 1, 2), new(new(float.NaN), 0, 1, .5f)];
        Assert.Equal(0, ClothImpulse.Displacement(Vector2.Zero, 1, bad));
        Assert.Equal(0, ClothImpulse.Displacement(Vector2.Zero, 10, [new(Vector2.Zero, 0, 1, .5f)]));
        var flood = Enumerable.Repeat(new ClothImpulse(Vector2.Zero, 0, 1, .5f), 1000).ToArray();
        Assert.Equal(ClothImpulse.Displacement(Vector2.Zero, .1, flood.AsSpan(0, 16)), ClothImpulse.Displacement(Vector2.Zero, .1, flood));
        Assert.InRange(ClothImpulse.Displacement(Vector2.Zero, .1, flood), -ClothImpulse.MaximumDisplacement, ClothImpulse.MaximumDisplacement);
    }

    private static int Center(ClothMesh mesh) => mesh.Size / 2 * mesh.Size + mesh.Size / 2;
    private static float Blend(float[] values, Vector3 weights) => values[0] * weights.X + values[1] * weights.Y + values[2] * weights.Z;
    // Frozen d343b243 pre-optimization render algorithm. Deliberately retains
    // all four endpoint discs plus both valid bridges and has no broad phase.
    private static float[] FrozenRenderCeilings(ClothMesh mesh, ReadOnlySpan<ClothFootContact> feet, float lift)
    {
        const float flutter = .012f, gap = .001f, clearance = .001f;
        var ground = mesh.GroundMinimum!;
        var result = Enumerable.Repeat(float.MaxValue, mesh.Positions.Length).ToArray();
        for (var i = 0; i < mesh.Indices.Length; i += 3)
        {
            var a = mesh.Indices[i]; var b = mesh.Indices[i + 1]; var c = mesh.Indices[i + 2];
            var pa = mesh.Positions[a]; var pb = mesh.Positions[b]; var pc = mesh.Positions[c];
            var groundMax = Math.Max(ground[a], Math.Max(ground[b], ground[c]));
            var freeMax = Math.Max(pa.Y, Math.Max(pb.Y, pc.Y));
            var diameterSquared = Math.Max(Edge(pa, pb), Math.Max(Edge(pb, pc), Edge(pc, pa)));
            var diameter = (float)Math.Sqrt(diameterSquared);
            if ((double)diameter * diameter < diameterSquared) diameter = MathF.BitIncrement(diameter);
            foreach (var foot in feet) if (foot.Valid) Apply(foot, foot.Center, foot.Center);
            for (var pair = 0; pair + 1 < feet.Length; pair += 2)
            {
                var first = feet[pair]; var second = feet[pair + 1];
                if (!first.Valid || !second.Valid || first.Radius > ClothFootRenderFrame.MaximumRenderRadius
                    || second.Radius > ClothFootRenderFrame.MaximumRenderRadius || Math.Abs(first.FootY - second.FootY) > .0001f
                    || Math.Abs(first.Radius - second.Radius) > .0001f || Vector2.DistanceSquared(first.Center, second.Center) > .75f * .75f) continue;
                Apply(first with { FootY = Math.Min(first.FootY, second.FootY), Radius = Math.Min(first.Radius, second.Radius) }, first.Center, second.Center);
            }
            void Apply(ClothFootContact foot, Vector2 start, Vector2 end)
            {
                if (foot.FootY > groundMax + lift + .06f && freeMax + lift + flutter < foot.FootY - gap) return;
                var offset = Math.Clamp(foot.FootY - gap - (groundMax + clearance), 0, lift);
                var radius = foot.Radius + diameter;
                Corner(a); Corner(b); Corner(c);
                void Corner(int index)
                {
                    var vertex = mesh.Positions[index]; var at = new Vector2(vertex.X, vertex.Z);
                    var segment = end - start; var length = segment.LengthSquared();
                    var t = length > .000001f ? Math.Clamp(Vector2.Dot(at - start, segment) / length, 0, 1) : 0;
                    var distance = Vector2.Distance(at, start + t * segment) / radius;
                    var value = Math.Clamp((1.5f - distance) / .5f, 0, 1); var weight = value * value * (3 - 2 * value);
                    if (weight <= 0) return;
                    var floor = ground[index] + clearance;
                    var original = Math.Max(floor, vertex.Y + lift + flutter);
                    var bounded = Math.Min(original, floor + offset);
                    var candidate = weight >= 1 ? bounded : original + (bounded - original) * weight;
                    result[index] = Math.Min(result[index], Math.Max(floor, candidate));
                }
            }
        }
        return result;
        static double Edge(Vector3 x, Vector3 y)
        { var dx = (double)x.X - y.X; var dz = (double)x.Z - y.Z; return dx * dx + dz * dz; }
    }
    private static ClothMesh Triangle(float[] ground) => new(
        [new(0, ground[0] + .035f, 0), new(.133f, ground[1] + .035f, 0), new(0, ground[2] + .035f, .133f)],
        new Vector3[3], new Vector2[3], [0, 1, 2], 2, ground);
    private static ClothMesh Grid()
    {
        const int size = 17;
        var mesh = ClothSurface.Build(Vector2.Zero, Vector2.One, 0, new float[size * size], size, 0, false);
        return mesh with { GroundMinimum = new float[size * size] };
    }
}
