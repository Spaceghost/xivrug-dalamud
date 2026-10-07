using System.Numerics;

namespace XivSurface.Core.Tests;

public sealed class ClothFootPresentationFrameTests
{
    private static readonly ClothFootCaptureIdentity Owner = new(129, 42, 0x1234, 1);
    private static readonly ClothFootPresentationEpoch Epoch = new(0x8888, 33, 200);
    private static ClothFootContact[] Contacts() =>
    [
        new(new(-.1f, -.1f), .004f, .22f), new(new(-.1f, .1f), .004f, .22f),
        new(new(.1f, -.1f), .004f, .22f), new(new(.1f, .1f), .004f, .22f),
    ];
    private static ClothFootRenderFrame Capture(ClothFootCaptureIdentity? owner = null)
    {
        var identity = owner ?? Owner;
        Assert.True(ClothFootRenderFrame.TryCaptureBound(identity, identity, 10, 10.001, Contacts(), out var frame));
        return frame!;
    }
    private static void Finalize(ClothFootPresentationFrame policy)
    {
        policy.BeginFramework(Owner); policy.CompleteFramework(true);
        var ticket = policy.BeginFinalization(Epoch, true);
        Assert.NotEqual(0, ticket);
        Assert.True(policy.CompleteFinalization(ticket, Epoch, Capture(), 10.001));
    }

    [Fact]
    public void DelayedSameNativeSceneUsesFinalizedPoseWithoutRenewingItsWallTimestamp()
    {
        using var policy = new ClothFootPresentationFrame(); Finalize(policy);
        Assert.True(policy.TryAcquire(129, Epoch, 10.343, out var lease));
        using (lease)
        {
            Span<ClothFootContact> into = stackalloc ClothFootContact[4];
            Assert.True(lease!.TryCopyContacts(Epoch, into));
            Assert.Equal(Contacts(), into.ToArray());
            Assert.Equal(10, lease.Frame.SampledAt);
            Assert.False(lease.Frame.TryGetClothRenderContacts(129, 10.343, into, out var ordinary));
            Assert.Equal(ClothFootRenderGate.Expired, ordinary);
            Assert.True(lease.TryCopyContacts(Epoch, into));
            var mesh = ClothSurface.Build(Vector2.Zero, Vector2.One, 0, new float[9], 3, 0, false)
                with { GroundMinimum = new float[9] };
            var ceilings = ClothContactConstraint.BuildRenderCeilings(mesh, into, 0);
            var positions = new Vector3[9]; var normals = new Vector3[9];
            ClothRenderPose.Prepare(mesh, ceilings, Vector2.One, false, .25f, 0, true, true, 0, positions, normals);
            Assert.All(positions, point => Assert.True(point.Y >= .001f));
            Assert.True(lease.IsCurrent(Epoch));
        }
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void GameFramePresentationOrSwapChainMismatchCannotUseFrozenPose(int changed)
    {
        using var policy = new ClothFootPresentationFrame(); Finalize(policy);
        var other = changed switch
        {
            0 => Epoch with { FrameworkFrame = 201 },
            1 => Epoch with { PresentCount = 34 },
            _ => Epoch with { SwapChain = 0x9999 },
        };
        Assert.False(policy.TryAcquire(129, other, 10.343, out _));
        Assert.True(policy.TryAcquire(129, Epoch, 10.343, out var lease));
        using (lease) Assert.False(lease!.IsCurrent(other));
    }

    [Fact]
    public void FrameworkCaptureAndUnknownNativePhaseCannotAttestRenderedScene()
    {
        using var policy = new ClothFootPresentationFrame();
        policy.BeginFramework(Owner);
        Assert.False(policy.TryAcquire(129, Epoch, 10.001, out _));
        Assert.False(policy.CompleteFinalization(1, Epoch, Capture(), 10.001));
        policy.CompleteFramework(true);
        Assert.False(policy.TryAcquire(129, Epoch, 10.001, out _));
        Assert.Equal(0, policy.BeginFinalization(Epoch, false)); // Render/worker thread is unverified.
        Assert.False(policy.TryAcquire(129, Epoch, 10.001, out _));
    }

    [Fact]
    public void EveryNextFrameworkCycleRevokesEvenWhenGameCountersRepeat()
    {
        using var policy = new ClothFootPresentationFrame(); Finalize(policy);
        policy.BeginFramework(Owner);
        Assert.False(policy.TryAcquire(129, Epoch, 10.343, out _));
        policy.CompleteFramework(true);
        Assert.False(policy.TryAcquire(129, Epoch, 10.343, out _));
    }

    [Fact]
    public void ASecondNativePassInvalidatesInsteadOfReusingItsFirstPose()
    {
        using var policy = new ClothFootPresentationFrame(); Finalize(policy);
        Assert.Equal(0, policy.BeginFinalization(Epoch, true));
        Assert.False(policy.TryAcquire(129, Epoch, 10.343, out _));
    }

    [Fact]
    public void OneObservationCannotBeReusedForMultipleRenderAttempts()
    {
        using var policy = new ClothFootPresentationFrame(); Finalize(policy);
        Assert.True(policy.TryAcquire(129, Epoch, 10.343, out var lease));
        lease!.Dispose();
        Assert.False(lease.IsCurrent(Epoch));
        Assert.False(policy.TryAcquire(129, Epoch, 10.344, out _));
    }

    [Fact]
    public void RepeatedInspectionReportsDelayedEligibilityWithoutAcquiringOrConsuming()
    {
        using var policy = new ClothFootPresentationFrame(); Finalize(policy);
        for (var i = 0; i < 10; i++)
        {
            var state = policy.Inspect(Epoch, 10.343);
            Assert.Equal("Finalized", state.Phase);
            Assert.True(state.HasCapture && state.EpochMatches && state.ThreadMatches && state.Eligible);
            Assert.Equal(.343, state.SampleAge, 8);
        }
        Assert.True(policy.TryAcquire(129, Epoch, 10.343, out var lease));
        using (lease)
        {
            var state = policy.Inspect(Epoch, 10.343);
            Assert.Equal("Consumed", state.Phase);
            Assert.False(state.Eligible);
            Assert.True(lease!.IsCurrent(Epoch));
        }
    }

    [Fact]
    public void InspectionDistinguishesUnfinishedFrameworkAndWrongEpochWithoutMutations()
    {
        using var policy = new ClothFootPresentationFrame();
        policy.BeginFramework(Owner);
        var early = policy.Inspect(Epoch, 10);
        Assert.Equal("Updating", early.Phase); Assert.False(early.HasCapture || early.Eligible);
        policy.CompleteFramework(true);
        Assert.Equal("Ready", policy.Inspect(Epoch, 10).Phase);
        Finalize(policy);
        var wrong = policy.Inspect(Epoch with { PresentCount = 34 }, 10.343);
        Assert.True(wrong.ThreadMatches); Assert.False(wrong.EpochMatches || wrong.Eligible);
        Assert.False(policy.Inspect(Epoch, double.NaN).Eligible);
        Assert.True(policy.TryAcquire(129, Epoch, 10.343, out var lease)); lease!.Dispose();
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void ReplacedModelActorAddressOrSceneRejectsFinalization(int changed)
    {
        using var policy = new ClothFootPresentationFrame();
        policy.BeginFramework(Owner); policy.CompleteFramework(true);
        var ticket = policy.BeginFinalization(Epoch, true);
        var other = changed switch
        {
            0 => Owner with { ModelGeneration = 2 }, 1 => Owner with { PlayerId = 43 },
            2 => Owner with { Address = 0x2222 }, _ => Owner with { Zone = 130 },
        };
        Assert.False(policy.CompleteFinalization(ticket, Epoch, Capture(other), 10.001));
        Assert.False(policy.TryAcquire(129, Epoch, 10.343, out _));
    }

    [Theory]
    [InlineData(double.NaN)] [InlineData(9.999)] [InlineData(10.034)]
    public void MalformedOrStalledNativeReadCannotUseFrameAssociation(double completed)
    {
        using var policy = new ClothFootPresentationFrame();
        policy.BeginFramework(Owner); policy.CompleteFramework(true);
        var ticket = policy.BeginFinalization(Epoch, true);
        Assert.False(policy.CompleteFinalization(ticket, Epoch, Capture(), completed));
    }

    [Fact]
    public void NativeEpochChangingDuringReadFailsAndOldCallbackCannotRevokeNewCycle()
    {
        using var policy = new ClothFootPresentationFrame();
        policy.BeginFramework(Owner); policy.CompleteFramework(true);
        var ticket = policy.BeginFinalization(Epoch, true);
        Assert.False(policy.CompleteFinalization(ticket, Epoch with { PresentCount = 34 }, Capture(), 10.001));
        Finalize(policy);
        Assert.False(policy.CompleteFinalization(ticket, Epoch, Capture(), 10.001));
        Assert.True(policy.TryAcquire(129, Epoch, 10.343, out var lease)); lease!.Dispose();
    }

    [Fact]
    public void FailedUpdatesDisposalAndWrongZoneOrClockKeepOrdinaryGuardRequired()
    {
        var policy = new ClothFootPresentationFrame();
        policy.BeginFramework(Owner); policy.CompleteFramework(false);
        Assert.Equal(0, policy.BeginFinalization(Epoch, true));
        Finalize(policy);
        Assert.False(policy.TryAcquire(130, Epoch, 10.343, out _));
        Assert.False(policy.TryAcquire(129, Epoch, double.NaN, out _));
        Assert.False(policy.TryAcquire(129, Epoch, 10, out _));
        policy.Dispose();
        Assert.False(policy.TryAcquire(129, Epoch, 10.343, out _));
    }

    [Fact]
    public async Task DifferentRenderThreadCannotUseGlobalCountersAsACommandBufferFrameToken()
    {
        using var policy = new ClothFootPresentationFrame(); Finalize(policy);
        // Both counters may name the CPU's new frame while this worker still
        // consumes the preceding queued scene. Counter equality is no proof.
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try
            {
                var result = policy.TryAcquire(129, Epoch, 10.343, out var lease);
                lease?.Dispose();
                completion.SetResult(result);
            }
            catch (Exception error) { completion.SetException(error); }
        });
        worker.Start();
        Assert.False(await completion.Task.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task NextFrameworkCannotAdvanceWhileSynchronousSubmissionOwnsSnapshot()
    {
        using var policy = new ClothFootPresentationFrame(); Finalize(policy);
        Assert.True(policy.TryAcquire(129, Epoch, 10.343, out var lease));
        using var entering = new ManualResetEventSlim(); using var finished = new ManualResetEventSlim();
        var worker = Task.Run(() => { entering.Set(); policy.BeginFramework(Owner); finished.Set(); });
        try
        {
            Assert.True(entering.Wait(TimeSpan.FromSeconds(2)));
            Assert.False(finished.Wait(TimeSpan.FromMilliseconds(20)));
            Assert.True(lease!.IsCurrent(Epoch));
        }
        finally { lease!.Dispose(); }
        await worker.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(finished.IsSet);
        Assert.False(policy.TryAcquire(129, Epoch, 10.4, out _));
    }
}
