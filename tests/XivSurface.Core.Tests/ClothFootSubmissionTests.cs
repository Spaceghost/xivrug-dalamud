using XivSurface.Core;

namespace XivSurface.Core.Tests;

public sealed class ClothFootSubmissionTests
{
    private static ClothFootRenderFrame Frame(double at = 10, bool suppressed = false, bool adaptive = false) =>
        new(128, at, [new(new(-.15f, -.1f), .004f, .22f), new(new(-.15f, .1f), .004f, .22f),
            new(new(.15f, -.1f), .004f, .22f), new(new(.15f, .1f), .004f, .22f)],
            suppressed, adaptive ? .1 : ClothFootRenderFrame.MaximumAgeSeconds);

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void UploadedEnvelopeCoversTheWholePermittedPreparationInterval(bool cloth)
    {
        var frame = Frame(); var started = 10.01;
        Span<ClothFootContact> uploaded = stackalloc ClothFootContact[4];
        Span<ClothFootContact> measuredAtSubmission = stackalloc ClothFootContact[4];
        Assert.True(ClothFootSubmission.TryPrepare(frame, 128, started, cloth, uploaded, out var draw, out var gate));
        Assert.Equal(ClothFootRenderGate.Allowed, gate);
        Assert.Equal(started + ClothFootSubmission.MaximumPreparationSeconds, draw.CoveredUntil);
        for (var step = 0; step <= 8; step++)
        {
            var now = started + step * .001;
            Assert.True(draw.CanSubmit(frame, 128, now));
            Assert.True(cloth ? frame.TryGetClothRenderContacts(128, now, measuredAtSubmission, out _)
                : frame.TryGetRenderContacts(128, now, measuredAtSubmission));
            for (var i = 0; i < 4; i++)
            {
                Assert.Equal(frame.Contacts[i].Center, uploaded[i].Center);
                Assert.True(uploaded[i].Radius >= measuredAtSubmission[i].Radius);
                Assert.True(uploaded[i].FootY <= measuredAtSubmission[i].FootY);
            }
        }
        Assert.False(draw.CanSubmit(frame, 128, draw.CoveredUntil + .000001));
        // A later caller cannot renew the same already-prepared GPU data.
        Assert.Equal(started + ClothFootSubmission.MaximumPreparationSeconds, draw.CoveredUntil);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(true, true)]
    public void PreparationNeverExtendsOriginalCaptureLifetime(bool cloth, bool adaptive)
    {
        var frame = Frame(adaptive: adaptive);
        var expiry = frame.SampledAt + (cloth ? frame.ClothMaximumAgeSeconds : ClothFootRenderFrame.MaximumAgeSeconds);
        Span<ClothFootContact> feet = stackalloc ClothFootContact[4];
        Assert.True(ClothFootSubmission.TryPrepare(frame, 128, expiry - .002, cloth, feet, out var draw, out _));
        Assert.Equal(expiry, draw.CoveredUntil);
        Assert.True(draw.CanSubmit(frame, 128, expiry));
        Assert.False(draw.CanSubmit(frame, 128, expiry + .000001));
        Assert.False(ClothFootSubmission.TryPrepare(frame, 128, expiry + .000001, cloth, feet, out _, out _));
    }

    [Fact]
    public void ReplacedMissingWrongZoneAndClockRollbackCannotAuthorizePreparedData()
    {
        var frame = Frame(); Span<ClothFootContact> feet = stackalloc ClothFootContact[4];
        Assert.True(ClothFootSubmission.TryPrepare(frame, 128, 10.01, true, feet, out var draw, out _));
        Assert.False(draw.CanSubmit(Frame(), 128, 10.011));
        Assert.False(draw.CanSubmit(null, 128, 10.011));
        Assert.False(draw.CanSubmit(frame, 129, 10.011));
        Assert.False(draw.CanSubmit(frame, 128, 10.009));
        Assert.False(draw.CanSubmit(frame, 128, double.NaN));
        Assert.False(draw.CanSubmit(frame, 128, double.PositiveInfinity));
        Assert.False(default(ClothFootSubmission).CanSubmit(null, 0, 0));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void InvalidPreparationLeavesNoReusableTokenOrFootContacts(int reason)
    {
        ClothFootRenderFrame? frame = reason == 0 ? null : Frame(suppressed: reason == 1);
        Span<ClothFootContact> feet = stackalloc ClothFootContact[4];
        feet.Fill(new(new(1, 1), 1, .2f));
        var zone = reason == 2 ? 129u : 128u;
        var now = reason == 3 ? double.NaN : reason == 4 ? 9 : 10.01;
        var output = reason == 5 ? feet[..2] : feet;
        Assert.False(ClothFootSubmission.TryPrepare(frame, zone, now, true, output, out var draw, out _));
        Assert.False(draw.CanSubmit(frame, zone, now));
        foreach (var foot in output) Assert.Equal(default, foot);
    }

    [Fact]
    public void SlowColdPreparationRejectsOnceAndFreshNextFrameRecovers()
    {
        var old = Frame(); Span<ClothFootContact> feet = stackalloc ClothFootContact[4];
        Assert.True(ClothFootSubmission.TryPrepare(old, 128, 10.001, true, feet, out var draw, out _));
        Assert.False(draw.CanSubmit(old, 128, 10.1));
        var next = Frame(10.1);
        Assert.True(ClothFootSubmission.TryPrepare(next, 128, 10.101, true, feet, out var nextDraw, out _));
        Assert.True(nextDraw.CanSubmit(next, 128, 10.102));
        Assert.False(draw.CanSubmit(next, 128, 10.102));
    }
}
