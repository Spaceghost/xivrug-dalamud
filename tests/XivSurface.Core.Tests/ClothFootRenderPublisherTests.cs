using System.Numerics;
using XivSurface.Core;

namespace XivSurface.Core.Tests;

public sealed class ClothFootRenderPublisherTests
{
    private static readonly ClothFootCaptureIdentity Identity = new(129, 1234, 0x123400);
    private static ClothFootContact[] Boots(float x = 0) =>
    [
        new(new(x - .15f, -.1f), .004f, .22f), new(new(x - .15f, .1f), .004f, .22f),
        new(new(x + .15f, -.1f), .004f, .22f), new(new(x + .15f, .1f), .004f, .22f),
    ];

    private static ClothFootRenderFrame Publish(ClothFootRenderPublisher publisher, double time = 10)
    {
        publisher.BeginUpdate(Identity);
        return Assert.IsType<ClothFootRenderFrame>(publisher.CompleteUpdate(true, Identity, Identity,
            time, time + .001, Boots()));
    }

    [Theory]
    [InlineData(.008)]
    [InlineData(.016)]
    [InlineData(.030)]
    [InlineData(.100)]
    public void WorkDelayDoesNotAgeTheNewActualReadOrRetimestampPhysics(double workSeconds)
    {
        var physicsContacts = Boots();
        var physicsFrame = new ClothFootRenderFrame(Identity.Zone, 10, physicsContacts);
        var publisher = new ClothFootRenderPublisher();
        publisher.BeginUpdate(Identity);
        var renderContacts = Boots(.25f); // the second read can observe different endpoints
        var render = publisher.CompleteUpdate(true, Identity, Identity,
            10 + workSeconds, 10 + workSeconds + .001, renderContacts);
        Assert.NotNull(render);
        Assert.Equal(10 + workSeconds, render.SampledAt);
        Assert.Equal(renderContacts, render.Contacts.ToArray());
        Assert.Equal(physicsContacts, physicsFrame.Contacts.ToArray());
        Assert.Equal(10, physicsFrame.SampledAt);
        Assert.True(render.CanRender(Identity.Zone, 10 + workSeconds + .008));
        Assert.Equal(workSeconds + .008 <= ClothFootRenderFrame.MaximumAgeSeconds,
            physicsFrame.CanRender(Identity.Zone, 10 + workSeconds + .008));
    }

    [Fact]
    public void FreshEarlyCaptureIsVisibleWhileWorkRunsThenLateReadReplacesIt()
    {
        var publisher = new ClothFootRenderPublisher();
        var old = Publish(publisher, 9);
        publisher.BeginUpdate(Identity);
        var earlyContacts = Boots(2); // same-owner instantaneous reposition
        var early = publisher.PublishEarlyCapture(Identity, Identity, 10, 10.001, earlyContacts);
        Assert.NotNull(early);
        Assert.Same(early, publisher.Current);
        Assert.NotSame(old, early);
        Assert.Equal(earlyContacts, early.Contacts.ToArray());
        Assert.True(early.CanRender(Identity.Zone, 10.020));
        Assert.False(old.CanRender(Identity.Zone, 10.020));
        var lateContacts = Boots(2.1f);
        var late = publisher.CompleteUpdate(true, Identity, Identity, 10.021, 10.022, lateContacts);
        Assert.NotNull(late);
        Assert.NotSame(early, late);
        Assert.Same(late, publisher.Current);
        Assert.Equal(lateContacts, late.Contacts.ToArray());
        Assert.Equal(10, early.SampledAt);
        Assert.Equal(10.021, late.SampledAt);
    }

    [Fact]
    public void BodyFailureClearsEvenSuccessfulEarlyCapture()
    {
        var publisher = new ClothFootRenderPublisher();
        publisher.BeginUpdate(Identity);
        Assert.NotNull(publisher.PublishEarlyCapture(Identity, Identity, 10, 10.001, Boots()));
        Assert.Null(publisher.CompleteUpdate(false, Identity, Identity, 10.010, 10.011, Boots()));
        Assert.Null(publisher.Current);
    }

    [Fact]
    public void LateCaptureFailureClearsSuccessfulEarlyFallbackWithoutReusingIt()
    {
        var publisher = new ClothFootRenderPublisher();
        publisher.BeginUpdate(Identity);
        var early = publisher.PublishEarlyCapture(Identity, Identity, 10, 10.001, Boots());
        Assert.NotNull(early);
        Assert.Null(publisher.CompleteUpdate(true, Identity, Identity, 10.010, 10.011, []));
        Assert.Null(publisher.Current);
        Assert.Equal(10, early.SampledAt);
    }

    [Fact]
    public void EarlyReadFailureClearsOlderRenderFrame()
    {
        var publisher = new ClothFootRenderPublisher();
        Publish(publisher);
        publisher.BeginUpdate(Identity);
        Assert.Null(publisher.PublishEarlyCapture(Identity, Identity, 10.005, 10.006, []));
        Assert.Null(publisher.Current);
    }

    [Fact]
    public void EarlyOwnerChangeCannotReauthorizeTheUpdateByChangingBackLater()
    {
        var publisher = new ClothFootRenderPublisher();
        Publish(publisher);
        publisher.BeginUpdate(Identity);
        var other = Identity with { PlayerId = 9876 };
        Assert.Null(publisher.PublishEarlyCapture(Identity, other, 10.005, 10.006, Boots()));
        Assert.Null(publisher.CompleteUpdate(true, Identity, Identity, 10.010, 10.011, Boots()));
    }

    [Fact]
    public void NewTimestampDoesNotGrantMoreThanExistingCutoffOrReduceAgeSweep()
    {
        var publisher = new ClothFootRenderPublisher();
        publisher.BeginUpdate(Identity);
        var frame = publisher.CompleteUpdate(true, Identity, Identity, 0, .005, Boots())!;
        Assert.Equal(0, frame.SampledAt);
        Span<ClothFootContact> render = stackalloc ClothFootContact[4];
        Assert.True(frame.TryGetClothRenderContacts(Identity.Zone, .016, render, out _));
        Assert.True(render[0].Radius >= .22f + ClothFootRenderFrame.HorizontalSpeedLimit * .016f);
        Assert.True(render[0].FootY <= .004f - ClothFootRenderFrame.DownwardSpeedLimit * .016f);
        Assert.True(frame.CanRender(Identity.Zone, ClothFootRenderFrame.MaximumAgeSeconds));
        Assert.False(frame.CanRender(Identity.Zone, ClothFootRenderFrame.MaximumAgeSeconds + .000001));
    }

    [Theory]
    [InlineData(.034)]
    [InlineData(.100)]
    public void SlowEndpointReadCannotPublishUsingItsCompletionTime(double duration)
    {
        var publisher = new ClothFootRenderPublisher();
        Publish(publisher, 0);
        publisher.BeginUpdate(Identity);
        Assert.Null(publisher.CompleteUpdate(true, Identity, Identity, 1, 1 + duration, Boots()));
        Assert.Null(publisher.Current);
    }

    [Fact]
    public void FailedBodyInvalidatesPreviousClothEvenIfContactsAreOtherwiseFresh()
    {
        var publisher = new ClothFootRenderPublisher();
        var previous = Publish(publisher);
        Assert.Same(previous, publisher.BeginUpdate(Identity));
        Assert.Null(publisher.CompleteUpdate(false, Identity, Identity, 10.005, 10.006, Boots()));
        Assert.Null(publisher.Current);
    }

    [Fact]
    public void ExceptionPathInvalidationClosesTransactionAndRejectsLateCompletion()
    {
        var publisher = new ClothFootRenderPublisher();
        Publish(publisher);
        publisher.BeginUpdate(Identity);
        publisher.Invalidate();
        Assert.Null(publisher.Current);
        Assert.Null(publisher.CompleteUpdate(true, Identity, Identity, 10.005, 10.006, Boots()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void FailedOrPartialReadCannotKeepEarlierCompleteContacts(int count)
    {
        var publisher = new ClothFootRenderPublisher();
        Publish(publisher);
        publisher.BeginUpdate(Identity);
        Assert.Null(publisher.CompleteUpdate(true, Identity, Identity, 10.005, 10.006, Boots().AsSpan(0, count)));
        Assert.Null(publisher.Current);
    }

    [Theory]
    [InlineData(0)] // scene disabled, logout, cutscene, mounting, or no player
    [InlineData(1)] // territory switch
    [InlineData(2)] // a different player at the same address
    [InlineData(3)] // same player reallocated
    public void BeginInvalidatesOnSceneOrOwnerChangeBeforeAnyExpensiveWork(int change)
    {
        var identity = ChangedIdentity(change);
        var publisher = new ClothFootRenderPublisher();
        Publish(publisher);
        Assert.Null(publisher.BeginUpdate(identity));
        Assert.Null(publisher.Current);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public void BeforeAndAfterCaptureMustMatchTheSuccessfulUpdateOwner(int change, bool duringRead)
    {
        var publisher = new ClothFootRenderPublisher();
        Publish(publisher);
        publisher.BeginUpdate(Identity);
        var changed = ChangedIdentity(change);
        Assert.Null(publisher.CompleteUpdate(true, duringRead ? Identity : changed, changed, 10.005, 10.006, Boots()));
    }

    [Fact]
    public void InvalidInitialSceneCannotBecomeAuthorizedOnlyByAValidLateRead()
    {
        var publisher = new ClothFootRenderPublisher();
        publisher.BeginUpdate(default);
        Assert.Null(publisher.CompleteUpdate(true, Identity, Identity, 10, 10.001, Boots()));
    }

    [Fact]
    public void SameOwnerCanUseExistingFreshFrameDuringWorkButItsAgeStillExpires()
    {
        var publisher = new ClothFootRenderPublisher();
        var old = Publish(publisher);
        Assert.Same(old, publisher.BeginUpdate(Identity));
        Assert.True(old.CanRender(Identity.Zone, 10.020));
        Assert.False(old.CanRender(Identity.Zone, 10.034));
        Assert.Equal(10, old.SampledAt);
    }

    [Fact]
    public void UnfinishedUpdateOrDuplicateCompletionDoesNotRenewAuthorization()
    {
        var publisher = new ClothFootRenderPublisher();
        Publish(publisher);
        publisher.BeginUpdate(Identity);
        Assert.Null(publisher.BeginUpdate(Identity));
        Assert.NotNull(publisher.CompleteUpdate(true, Identity, Identity, 10.005, 10.006, Boots()));
        Assert.Null(publisher.CompleteUpdate(true, Identity, Identity, 10.007, 10.008, Boots()));
    }

    [Theory]
    [InlineData(double.NaN, 10)]
    [InlineData(10, double.NaN)]
    [InlineData(double.PositiveInfinity, double.PositiveInfinity)]
    [InlineData(-1, 0)]
    [InlineData(10, 9)]
    public void MalformedOrReversedCaptureClockFailsClosed(double start, double end)
    {
        var publisher = new ClothFootRenderPublisher();
        Publish(publisher);
        publisher.BeginUpdate(Identity);
        Assert.Null(publisher.CompleteUpdate(true, Identity, Identity, start, end, Boots()));
    }

    [Fact]
    public void PublishedValuesStayImmutableWhenObserverBuffersChange()
    {
        var publisher = new ClothFootRenderPublisher();
        publisher.BeginUpdate(Identity);
        var contacts = Boots();
        var frame = publisher.CompleteUpdate(true, Identity, Identity, 10, 10.001, contacts)!;
        contacts[0] = new(Vector2.Zero, 20, .34f);
        Assert.Equal(Boots(), frame.Contacts.ToArray());
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(1, 0, 1)]
    [InlineData(1, 1, 0)]
    public void IdentityNeedsNonzeroZonePlayerAndAddress(uint zone, ulong player, long address)
        => Assert.False(new ClothFootCaptureIdentity(zone, player, (nint)address).Valid);

    private static ClothFootCaptureIdentity ChangedIdentity(int change) => change switch
    {
        0 => default,
        1 => Identity with { Zone = 130 },
        2 => Identity with { PlayerId = 9876 },
        _ => Identity with { Address = 0x432100 },
    };
}
