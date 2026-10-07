using System.Numerics;
using XivSurface.Core;

namespace XivSurface.Core.Tests;

public sealed class FootPlantCalibrationTests
{
    private static readonly RawFootIdentity Identity = new(new(3, 12, 34, 2), 56, 78, Vector3.One, 1);
    private static RawFootMarker[] Markers(float y = .04f, Quaternion? rotation = null) =>
    [new(new(-.15f, y, -.1f), rotation ?? Quaternion.Identity), new(new(-.15f, y, .1f), rotation ?? Quaternion.Identity),
     new(new(.15f, y, -.1f), rotation ?? Quaternion.Identity), new(new(.15f, y, .1f), rotation ?? Quaternion.Identity)];
    private static RawFootFrame Frame(long sequence, double time, float y = .04f,
        RawFootIdentity? identity = null, Quaternion? rotation = null, Vector3? player = null)
    {
        var id = identity ?? Identity;
        Assert.True(RawFootFrame.TryCapture(id, id, sequence, time, time + .0001,
            player ?? Vector3.Zero, Markers(y, rotation), out var frame));
        return frame!;
    }
    private static FloorTriangle Floor(float y) => new(new(-10, y, -10), new(-10, y, 20), new(20, y, -10));
    private static FootSupportObservation Support(RawFootFrame frame, float y = 0, long generation = 1)
    {
        Assert.True(FootSupportObservation.TryCreate(frame, generation, [Floor(y), Floor(y), Floor(y), Floor(y)], out var support));
        return support!;
    }
    private static FootSupportObservation[] Samples(float floor = 0, float joint = .04f) =>
        [Support(Frame(1, 1, joint, player: new(0, floor, 0)), floor),
         Support(Frame(2, 1.02, joint, player: new(0, floor, 0)), floor),
         Support(Frame(3, 1.04, joint, player: new(0, floor, 0)), floor)];
    private static FootPlantCalibration Calibration()
    { Assert.True(FootPlantCalibration.TryCreate(Samples(), 1.041, out var result)); return result!; }

    [Fact] public void RealFloorExampleDoesNotReuseTheBelowFloorRenderCeiling()
    {
        const float ground = 18.98386f;
        var samples = Samples(ground, 18.997f);
        Assert.True(FootPlantCalibration.TryCreate(samples, 1.041, out var calibration));
        Assert.True(calibration!.TryEvaluate(samples[^1].Frame, samples[^1], 1.041, out var pose));
        Assert.Equal(FootPlantState.CompressedPlant, pose!.State);
        Assert.InRange(pose.Capsules[0].A.Y - pose.Capsules[0].Radius, ground - .00001f, ground + .00001f);
        Assert.True(ClothFootClearance.TryBoot(new(-.15f, 18.997f, -.1f), new(-.15f, 18.997f, .1f),
            new(0, 18.984f, 0), 1, out var legacy, out _));
        Assert.True(legacy.FootY < ground - .03f);
        // Deliberately does not assert that positive-thickness cloth fits here.
        Assert.InRange(pose.MinimumSupportGap, -.00001f, .00001f);
    }

    [Fact] public void LiftFollowsActualMarkersWithoutPlayerYCapOrGroundSnap()
    {
        var c = Calibration(); var frame = Frame(4, 1.06, .44f); var support = Support(frame);
        Assert.True(c.TryEvaluate(frame, support, 1.061, out var pose));
        Assert.Equal(FootPlantState.Lifted, pose!.State);
        Assert.InRange(pose.MinimumSupportGap, .39999f, .40001f);
        Assert.Same(frame, pose.Source); Assert.Same(c, pose.Calibration); Assert.Same(support, pose.Support);
    }

    [Fact] public void FootLocalCalibrationFollowsActualOrientation()
    {
        var c = Calibration(); var q = Quaternion.CreateFromAxisAngle(Vector3.UnitX, .4f);
        var frame = Frame(4, 1.06, .4f, rotation: q);
        Assert.True(c.TryEvaluate(frame, Support(frame), 1.061, out var pose));
        var expected = frame.Markers[0].Position + Vector3.Transform(new Vector3(0, .18f, 0), q);
        Assert.InRange(Vector3.Distance(expected, pose!.Capsules[0].A), 0, .000001f);
        Assert.NotEqual(frame.Markers[0].Position.Z, pose.Capsules[0].A.Z);
    }

    [Fact] public void LandingOntoHigherSupportReportsConflictWithoutMovingProxy()
    {
        var c = Calibration(); var frame = Frame(4, 1.06);
        Assert.True(c.TryEvaluate(frame, Support(frame, .03f), 1.061, out var pose));
        Assert.Equal(FootPlantState.SupportConflict, pose!.State);
        Assert.InRange(pose.MinimumSupportGap, -.03001f, -.02999f);
        Assert.InRange(pose.Capsules[0].A.Y, .21999f, .22001f);
    }

    [Fact] public void CalibrationRemainsFrozenAfterAConflict()
    {
        var c = Calibration(); var conflict = Frame(4, 1.06);
        Assert.True(c.TryEvaluate(conflict, Support(conflict, .03f), 1.061, out _));
        var lifted = Frame(5, 1.08, .24f);
        Assert.True(c.TryEvaluate(lifted, Support(lifted), 1.081, out var pose));
        Assert.InRange(pose!.MinimumSupportGap, .19999f, .20001f);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void ChangedOwnerModelResourceScaleOrCaptureEpochRequiresNewCalibration(int field)
    {
        var id = field switch { 0 => Identity with { Actor = Identity.Actor with { PlayerId = 13 } },
            1 => Identity with { Actor = Identity.Actor with { ModelGeneration = 3 } },
            2 => Identity with { FeetModel = 57 }, 3 => Identity with { FeetResource = 79 },
            4 => Identity with { Scale = new(1.1f) }, 5 => Identity with { CaptureGeneration = 2 },
            6 => Identity with { EnabledAttributes = 1 }, _ => Identity with { EnabledShapes = 1 } };
        var frame = Frame(4, 1.06, identity: id);
        Assert.False(Calibration().TryEvaluate(frame, Support(frame), 1.061, out _));
    }

    [Fact] public void StaleBackwardsOrRelabeledSupportRefuses()
    {
        var c = Calibration(); var frame = Frame(4, 1.06); var support = Support(frame);
        Assert.False(c.TryEvaluate(frame, support, 1.1, out _));
        Assert.False(c.TryEvaluate(frame, support, 1.059, out _));
        Assert.False(c.TryEvaluate(frame, Support(Frame(4, 1.06)), 1.061, out _));
        var old = Frame(2, 1.02);
        Assert.False(c.TryEvaluate(old, Support(old), 1.021, out _));
        var relabeled = Frame(3, 1.041);
        Assert.False(c.TryEvaluate(relabeled, Support(relabeled), 1.042, out _));
    }

    [Fact] public void MovingOrChangingSupportCannotEstablishStandingCalibration()
    {
        var moving = Samples(); moving[2] = Support(Frame(3, 1.04, .07f));
        Assert.False(FootPlantCalibration.TryCreate(moving, 1.041, out _));
        var changing = Samples(); changing[2] = Support(changing[2].Frame, .005f);
        Assert.False(FootPlantCalibration.TryCreate(changing, 1.041, out _));
        var turning = Samples(); turning[2] = Support(Frame(3, 1.04, rotation: Quaternion.CreateFromAxisAngle(Vector3.UnitX, .2f)));
        Assert.False(FootPlantCalibration.TryCreate(turning, 1.041, out _));
    }

    [Theory] [InlineData(.15f)] [InlineData(-.3f)]
    public void WrongVerticalLayerRefusesCalibration(float floor)
    { Assert.False(FootPlantCalibration.TryCreate(Samples(floor, .04f), 1.041, out _)); }

    [Fact] public void DistinctTreadsForSeparateFeetAreAllowedButOneBootAcrossRiserRefuses()
    {
        var samples = new FootSupportObservation[3];
        for (var s = 0; s < 3; s++)
        {
            var markers = Markers(); markers[2] = markers[2] with { Position = markers[2].Position + Vector3.UnitY * .15f };
            markers[3] = markers[3] with { Position = markers[3].Position + Vector3.UnitY * .15f };
            Assert.True(RawFootFrame.TryCapture(Identity, Identity, s + 1, 1 + .02 * s, 1 + .02 * s + .0001,
                Vector3.Zero, markers, out var frame));
            Assert.True(FootSupportObservation.TryCreate(frame!, 1, [Floor(0), Floor(0), Floor(.15f), Floor(.15f)], out var support));
            samples[s] = support!;
        }
        Assert.True(FootPlantCalibration.TryCreate(samples, 1.041, out var c));
        Assert.True(c!.TryEvaluate(samples[^1].Frame, samples[^1], 1.041, out var pose));
        Assert.InRange(pose!.Capsules[1].A.Y - pose.Capsules[0].A.Y, .14999f, .15001f);
        for (var s = 0; s < 3; s++)
        {
            Assert.True(FootSupportObservation.TryCreate(samples[s].Frame, 1,
                [Floor(0), Floor(.02f), Floor(.15f), Floor(.15f)], out var split)); samples[s] = split!;
        }
        Assert.False(FootPlantCalibration.TryCreate(samples, 1.041, out _));
    }

    [Fact] public void MissingSamplesWrongGenerationAndSequenceGapsRefuse()
    {
        Assert.False(FootPlantCalibration.TryCreate(Samples().AsSpan(0, 2), 1.041, out _));
        var samples = Samples(); samples[2] = Support(Frame(4, 1.04));
        Assert.False(FootPlantCalibration.TryCreate(samples, 1.041, out _));
        samples = Samples(); samples[2] = Support(samples[2].Frame, generation: 2);
        Assert.False(FootPlantCalibration.TryCreate(samples, 1.041, out _));
        samples = Samples(); samples[1] = null!;
        Assert.False(FootPlantCalibration.TryCreate(samples, 1.041, out _));
    }

    [Fact] public void UnsafeScaleRefusesInsteadOfShrinkingTheProxy()
    {
        foreach (var scale in new[] { new Vector3(1, 1.2f, 1), new Vector3(2) })
        {
            var id = Identity with { Scale = scale };
            var samples = new[] { Support(Frame(1, 1, identity: id)), Support(Frame(2, 1.02, identity: id)), Support(Frame(3, 1.04, identity: id)) };
            Assert.False(FootPlantCalibration.TryCreate(samples, 1.041, out _));
        }
    }

    [Fact] public void CapturesOwnTheirArraysAndValidateTransaction()
    {
        var markers = Markers();
        Assert.True(RawFootFrame.TryCapture(Identity, Identity, 1, 1, 1.001, Vector3.Zero, markers, out var frame));
        markers[0] = default;
        Assert.NotEqual(default, frame!.Markers[0]);
        Assert.False(RawFootFrame.TryCapture(Identity, Identity with { FeetResource = 79 }, 1, 1, 1.001, Vector3.Zero, Markers(), out _));
        Assert.False(RawFootFrame.TryCapture(Identity, Identity, 1, 1, 1.04, Vector3.Zero, Markers(), out _));
        Assert.False(RawFootFrame.TryCapture(Identity, Identity, 1, 1, 1.001, Vector3.Zero, markers, out _));
        Assert.False(RawFootFrame.TryCapture(Identity with { CaptureGeneration = 0 }, Identity, 1, 1, 1.001, Vector3.Zero, Markers(), out _));
    }

    [Fact] public void InvalidOutsideOrRiserSupportRefuses()
    {
        var frame = Frame(1, 1); var wall = new FloorTriangle(new(0, 0, 0), new(0, 1, 0), new(0, 0, 1));
        Assert.False(FootSupportObservation.TryCreate(frame, 1, [wall, wall, wall, wall], out _));
        var far = new FloorTriangle(new(20, 0, 20), new(20, 0, 21), new(21, 0, 20));
        Assert.False(FootSupportObservation.TryCreate(frame, 1, [far, far, far, far], out _));
        Assert.False(FootSupportObservation.TryCreate(frame, 0, [Floor(0), Floor(0), Floor(0), Floor(0)], out _));
        Assert.False(FootSupportObservation.TryCreate(frame, 1, [Floor(0)], out _));
    }

    [Fact] public void MildCrossSlopeUsesNormalRadiusNotVerticalRadius()
    {
        var t = new FloorTriangle(new(-10, -1, -10), new(-10, -1, 20), new(20, 2, -10));
        var samples = new FootSupportObservation[3];
        for (var s = 0; s < 3; s++)
        {
            var markers = Markers();
            for (var i = 0; i < 4; i++) markers[i] = markers[i] with { Position = markers[i].Position
                + Vector3.UnitY * markers[i].Position.X * .1f };
            Assert.True(RawFootFrame.TryCapture(Identity, Identity, s + 1, 1 + .02 * s, 1 + .02 * s + .0001,
                Vector3.Zero, markers, out var frame));
            Assert.True(FootSupportObservation.TryCreate(frame!, 1, [t, t, t, t], out var support)); samples[s] = support!;
        }
        Assert.True(FootPlantCalibration.TryCreate(samples, 1.041, out var c));
        Assert.True(c!.TryEvaluate(samples[^1].Frame, samples[^1], 1.041, out var pose));
        Assert.Equal(FootPlantState.CompressedPlant, pose!.State);
        Assert.InRange(pose.MinimumSupportGap, -.00001f, .00001f);
        Assert.True(pose.Capsules[0].A.X < samples[^1].Frame.Markers[0].Position.X);
    }

    [Fact] public void MalformedMarkersAndCalibrationBoundsRefuseWithoutThrowing()
    {
        var invalid = Markers(); invalid[0] = invalid[0] with { Position = new(float.NaN, 0, 0) };
        Assert.False(RawFootFrame.TryCapture(Identity, Identity, 1, 1, 1.001, Vector3.Zero, invalid, out _));
        Assert.False(FootPlantCalibration.TryCreate([], 1, out _));
        Assert.False(FootPlantCalibration.TryCreate(new FootSupportObservation[9], 1, out _));
        Assert.False(FootPlantCalibration.TryCreate(Samples(), double.NaN, out _));
        Assert.False(FootPlantCalibration.TryCreate(Samples(), 1.1, out _));
        var samples = Samples(); samples[2] = Support(Frame(3, 1.3));
        Assert.False(FootPlantCalibration.TryCreate(samples, 1.301, out _));
    }

    [Fact] public void BoneLocalScaleIsObservedAndRefusesCalibrationOrSubsequentApplication()
    {
        var samples = Samples();
        var markers = Markers(); markers[0] = markers[0] with { LocalScale = new(1, 1.2f, 1) };
        Assert.True(RawFootFrame.TryCapture(Identity, Identity, 3, 1.04, 1.0401, Vector3.Zero, markers, out var changed));
        Assert.Equal(new Vector3(1, 1.2f, 1), changed!.Markers[0].LocalScale);
        samples[2] = Support(changed);
        Assert.False(FootPlantCalibration.TryCreate(samples, 1.041, out _));
        Assert.True(RawFootFrame.TryCapture(Identity, Identity, 4, 1.06, 1.0601, Vector3.Zero, markers, out changed));
        Assert.False(Calibration().TryEvaluate(changed!, Support(changed!), 1.061, out _));
        markers[0] = markers[0] with { LocalScale = new(float.NaN) };
        Assert.False(RawFootFrame.TryCapture(Identity, Identity, 4, 1.06, 1.0601, Vector3.Zero, markers, out _));
    }
}
