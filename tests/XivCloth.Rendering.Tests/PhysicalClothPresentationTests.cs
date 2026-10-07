using System.Numerics;
using XivCloth.Core;
using XivRug.Rendering;

namespace XivCloth.Rendering.Tests;

public sealed class PhysicalClothPresentationTests
{
    private static readonly FootProxyIdentity Owner = new(129, 42, 7, 1);
    private static readonly PhysicalClothChart Chart = new(new(3, 4), new(1, 1), true, 0);

    private static FootProxyPose Feet(long sequence, double time, FootProxyIdentity? identity = null)
    {
        FootMarkerEnvelope[] markers = [new(new(-.1f, 0), .1f, .1f), new(new(.1f, 0), .1f, .1f),
            new(new(-.1f, 1.7f), .1f, .1f), new(new(.1f, 1.7f), .1f, .1f)];
        Assert.True(FootProxyPose.TryCreate(identity ?? Owner, sequence, time, markers, out var pose));
        return pose!;
    }

    private static (XpbdCloth Solver, FootProxyPose Accepted, FootProxyPose Late, XpbdFootCapture Capture) Ready()
    {
        var solver = new XpbdCloth(new([new(-1, .04f, -1), new(0, .04f, 1), new(1, .04f, -1)],
            [new(0, 0), new(.5f, 1), new(1, 0)], [0, 1, 2], [1, 1, 1],
            [new(0, 1, 0), new(1, 2, 0), new(2, 0, 0)]), new() { Gravity = new(0, -1, 0) });
        var early = Feet(1, 10); var accepted = Feet(2, 10 + 1d / 80);
        Assert.True(FootProxyMotion.TryCreate(early, accepted, accepted.SampledAt, out var motion));
        Assert.Equal(XpbdStatus.Ready,
            solver.AdvanceWithFeet(motion!.Duration, MeasuredTriangleScene.Empty, motion, accepted.SampledAt).Status);
        var late = Feet(3, accepted.SampledAt + 1d / 240);
        var capture = solver.CaptureForFeet(accepted, late, late.SampledAt);
        Assert.Equal(XpbdStatus.Ready, capture.Status);
        return (solver, accepted, late, capture);
    }

    [Fact]
    public void ActualFootCheckedPhysicsReachesOwnedUploadWithoutChartTranslationOrDeformation()
    {
        var (solver, accepted, late, capture) = Ready();
        var presenter = new PhysicalClothPresentation();
        var observation = presenter.BeginObservation(accepted, late, solver.SceneGeneration, late.SampledAt);
        Assert.True(presenter.TryPublish(observation, capture, Chart, late.SampledAt));
        Assert.True(presenter.TryGet(Owner.Zone, late.SampledAt, out var packet));
        Assert.Equal(Chart, packet!.Chart);
        Assert.Same(capture.Frame!.FootBinding, packet.Binding);
        Assert.Equal(capture.Frame.Positions.ToArray(), packet.Pose.Positions.ToArray());
        Assert.Equal(capture.Frame.UV.ToArray(), packet.Pose.UV.ToArray());
        Assert.Equal(capture.Frame.Indices.ToArray(), packet.Pose.Indices.ToArray());
        var saved = packet.Pose.Positions.ToArray();
        solver.Reset();
        Assert.Equal(saved, packet.Pose.Positions.ToArray());
        // The owner must revoke before reset: immutable records do not inspect
        // a mutable solver behind the compositor's back.
        presenter.Invalidate();
        Assert.False(presenter.TryGet(Owner.Zone, late.SampledAt, out _));
        Assert.False(presenter.TryPublish(observation, capture, Chart, late.SampledAt));
    }

    [Fact]
    public void FreshEquivalentFootObjectsCannotAuthorizeOldGeometry()
    {
        var (solver, accepted, late, capture) = Ready();
        var presenter = new PhysicalClothPresentation();
        var observation = presenter.BeginObservation(Feet(accepted.Sequence, accepted.SampledAt),
            Feet(late.Sequence, late.SampledAt), solver.SceneGeneration, late.SampledAt);
        Assert.NotNull(observation);
        Assert.False(presenter.TryPublish(observation, capture, Chart, late.SampledAt));
        Assert.False(presenter.TryGet(Owner.Zone, late.SampledAt, out _));
    }

    [Fact]
    public void StartingNewObservationImmediatelyRevokesOldFrameAndOldWorkCannotReplaceNewFrame()
    {
        var (solver, accepted, late, capture) = Ready();
        var presenter = new PhysicalClothPresentation();
        var old = presenter.BeginObservation(accepted, late, solver.SceneGeneration, late.SampledAt);
        Assert.True(presenter.TryPublish(old, capture, Chart, late.SampledAt));
        var current = presenter.BeginObservation(accepted, late, solver.SceneGeneration, late.SampledAt);
        Assert.False(presenter.IsCurrent(old));
        Assert.True(presenter.IsCurrent(current));
        Assert.False(presenter.TryGet(Owner.Zone, late.SampledAt, out _));
        Assert.False(presenter.TryPublish(old, capture, Chart, late.SampledAt));
        Assert.True(presenter.TryPublish(current, capture, Chart, late.SampledAt));
        Assert.False(presenter.TryPublish(old, capture, Chart, late.SampledAt));
        Assert.True(presenter.TryGet(Owner.Zone, late.SampledAt, out _));
    }

    [Fact]
    public void RawCaptureOrFailedCaptureNeverFallsBackToLastAcceptedFrame()
    {
        var (solver, accepted, late, capture) = Ready();
        var presenter = new PhysicalClothPresentation();
        var observation = presenter.BeginObservation(accepted, late, solver.SceneGeneration, late.SampledAt);
        Assert.True(presenter.TryPublish(observation, capture, Chart, late.SampledAt));
        Assert.False(presenter.TryPublish(observation, new(XpbdStatus.Ready, solver.Capture(), 0, 0), Chart, late.SampledAt));
        Assert.False(presenter.TryGet(Owner.Zone, late.SampledAt, out _));
        Assert.True(presenter.TryPublish(observation, capture, Chart, late.SampledAt));
        Assert.False(presenter.TryPublish(observation, capture with { Status = XpbdStatus.CollisionUnproven }, Chart, late.SampledAt));
        Assert.False(presenter.TryGet(Owner.Zone, late.SampledAt, out _));
    }

    [Fact]
    public void SceneChangeOrWrongZoneCannotReusePacket()
    {
        var (solver, accepted, late, capture) = Ready();
        var presenter = new PhysicalClothPresentation();
        var observation = presenter.BeginObservation(accepted, late, solver.SceneGeneration + 1, late.SampledAt);
        Assert.False(presenter.TryPublish(observation, capture, Chart, late.SampledAt));
        observation = presenter.BeginObservation(accepted, late, solver.SceneGeneration, late.SampledAt);
        Assert.True(presenter.TryPublish(observation, capture, Chart, late.SampledAt));
        Assert.False(presenter.TryGet(Owner.Zone + 1, late.SampledAt, out _));
        Assert.False(presenter.TryGet(Owner.Zone, late.SampledAt, out _));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void OwnerOrModelChangeRevokesExistingMaterial(int field)
    {
        var (solver, accepted, late, capture) = Ready();
        var changed = field switch
        { 0 => Owner with { Actor = 43 }, 1 => Owner with { Zone = 128 }, 2 => Owner with { Instance = 8 }, _ => Owner with { ModelGeneration = 2 } };
        var presenter = new PhysicalClothPresentation();
        var observation = presenter.BeginObservation(accepted, late, solver.SceneGeneration, late.SampledAt);
        Assert.True(presenter.TryPublish(observation, capture, Chart, late.SampledAt));
        Assert.Null(presenter.BeginObservation(accepted, Feet(3, late.SampledAt, changed), solver.SceneGeneration, late.SampledAt));
        Assert.False(presenter.TryGet(Owner.Zone, late.SampledAt, out _));
    }

    [Fact]
    public void ExpiryAndClockRollbackCannotRetimestampOrResurrectAnOldPacket()
    {
        var (solver, accepted, late, capture) = Ready();
        var presenter = new PhysicalClothPresentation();
        var observation = presenter.BeginObservation(accepted, late, solver.SceneGeneration, late.SampledAt);
        Assert.True(presenter.TryPublish(observation, capture, Chart, late.SampledAt));
        var expiry = capture.Frame!.FootBinding!.ExpiresAt;
        Assert.True(presenter.TryGet(Owner.Zone, expiry, out _));
        Assert.False(presenter.TryGet(Owner.Zone, expiry + .000001, out _));
        Assert.False(presenter.TryGet(Owner.Zone, late.SampledAt, out _));
        Assert.False(presenter.TryPublish(observation, capture, Chart, late.SampledAt));
        Assert.Null(presenter.BeginObservation(accepted, late, solver.SceneGeneration, expiry + .001));
    }

    [Fact]
    public void PreparationCrossingTheDeadlineCannotSubmitAnEntryTimeValidPacket()
    {
        var (solver, accepted, late, capture) = Ready();
        var presenter = new PhysicalClothPresentation();
        var now = late.SampledAt;
        var observation = presenter.BeginObservation(accepted, late, solver.SceneGeneration, now);
        Assert.True(presenter.TryPublish(observation, capture, Chart, now));
        Assert.True(presenter.TryGet(Owner.Zone, now, out var packet));
        Func<bool> canSubmit = () => presenter.CanSubmit(packet, Owner.Zone, now);
        Assert.True(canSubmit());
        // An injected preparation stall models resource/Map/IPC time. The
        // production callback samples this clock again immediately before Draw.
        now = capture.Frame!.FootBinding!.ExpiresAt + .000001;
        Assert.False(canSubmit());
        Assert.False(presenter.TryGet(Owner.Zone, now, out _));
    }

    [Fact]
    public void SubmissionRejectsReplacedPacketEvenWhenItsGeometryAndBindingAreEqual()
    {
        var (solver, accepted, late, capture) = Ready();
        var presenter = new PhysicalClothPresentation();
        var observation = presenter.BeginObservation(accepted, late, solver.SceneGeneration, late.SampledAt);
        Assert.True(presenter.TryPublish(observation, capture, Chart, late.SampledAt));
        Assert.True(presenter.TryGet(Owner.Zone, late.SampledAt, out var old));
        Assert.True(presenter.TryPublish(observation, capture, Chart, late.SampledAt));
        Assert.True(presenter.TryGet(Owner.Zone, late.SampledAt, out var current));
        Assert.False(presenter.CanSubmit(old, Owner.Zone, late.SampledAt));
        Assert.True(presenter.CanSubmit(current, Owner.Zone, late.SampledAt));
        presenter.Invalidate(); // also the actual caller action BEFORE any late native capture
        Assert.False(presenter.CanSubmit(current, Owner.Zone, late.SampledAt));
    }

    [Fact]
    public void WarmSubmissionPredicateAllocatesNoManagedGeometryOrClosures()
    {
        var (solver, accepted, late, capture) = Ready();
        var presenter = new PhysicalClothPresentation();
        var observation = presenter.BeginObservation(accepted, late, solver.SceneGeneration, late.SampledAt);
        Assert.True(presenter.TryPublish(observation, capture, Chart, late.SampledAt));
        Assert.True(presenter.TryGet(Owner.Zone, late.SampledAt, out var packet));
        for (var i = 0; i < 256; i++) _ = presenter.CanSubmit(packet, Owner.Zone, late.SampledAt);
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var allowed = true;
        for (var i = 0; i < 1024; i++) allowed &= presenter.CanSubmit(packet, Owner.Zone, late.SampledAt);
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.True(allowed);
        Assert.Equal(0, allocated);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void InvalidMaterialChartDoesNotMoveOrAcceptGeometry(int mode)
    {
        var (solver, accepted, late, capture) = Ready();
        var invalid = mode switch
        {
            0 => Chart with { Center = new(float.NaN, 0) },
            1 => Chart with { HalfSize = new(0, 1) },
            2 => Chart with { HalfSize = new(1, 2) },
            3 => Chart with { Corner = float.NaN },
            4 => Chart with { Corner = 2 },
            _ => Chart with { Center = new(10001, 0) },
        };
        var presenter = new PhysicalClothPresentation();
        var observation = presenter.BeginObservation(accepted, late, solver.SceneGeneration, late.SampledAt);
        Assert.False(presenter.TryPublish(observation, capture, invalid, late.SampledAt));
        Assert.False(presenter.TryGet(Owner.Zone, late.SampledAt, out _));
    }
}
