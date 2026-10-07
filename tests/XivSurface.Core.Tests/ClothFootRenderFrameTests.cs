using System.Numerics;
using XivSurface.Core;

namespace XivSurface.Core.Tests;

public sealed class ClothFootRenderFrameTests
{
    private static ClothFootContact[] Boots() =>
    [
        new(new(-.15f,-.1f), .004f, .22f), new(new(-.15f,.1f), .004f, .22f),
        new(new(.15f,-.1f), .004f, .22f), new(new(.15f,.1f), .004f, .22f),
    ];

    [Fact]
    public void CompleteBootsCanRenderOnlyInTheirCurrentZoneAndFreshTimeWindow()
    {
        var frame = new ClothFootRenderFrame(100, 10, Boots());
        Assert.Equal(100u, frame.Zone); Assert.Equal(10d, frame.SampledAt); Assert.False(frame.Suppressed);
        Assert.True(frame.CanRender(100, 10)); Assert.True(frame.CanRender(100, 10 + ClothFootRenderFrame.MaximumAgeSeconds));
        Assert.False(frame.CanRender(100, 10 + ClothFootRenderFrame.MaximumAgeSeconds + .000001)); Assert.False(frame.CanRender(100, 9.999));
        Assert.False(frame.CanRender(0, 10)); Assert.False(frame.CanRender(101, 10));
        Assert.False(new ClothFootRenderFrame(0, 10, Boots()).CanRender(0, 10));
    }

    [Fact]
    public void CallerMutationCannotChangePublishedContactsOrCachedProtection()
    {
        var source = Boots(); var original = (ClothFootContact[])source.Clone();
        var frame = new ClothFootRenderFrame(100, 0, source);
        Array.Clear(source);
        Assert.Equal(original, frame.Contacts.ToArray()); Assert.True(frame.CanRender(100, .01));
        var missing = new ClothFootContact[4]; var rejected = new ClothFootRenderFrame(100, 0, missing);
        Boots().CopyTo(missing, 0);
        Assert.False(rejected.CanRender(100, .01));
    }

    [Fact]
    public void ContactBudgetIsExactlyTheFourSlotsConsumedByBothShaders()
    {
        var tooMany = Boots().Append(new ClothFootContact(new(float.NaN), 0, .2f)).ToArray();
        var frame = new ClothFootRenderFrame(100, 0, tooMany);
        Assert.Equal(4, frame.Contacts.Length); Assert.True(frame.CanRender(100, .01));
        var guardBeyondBudget = new ClothFootContact[5];
        guardBeyondBudget[4] = new(Vector2.Zero, -2, ClothFootClearance.FallbackRadius);
        Assert.False(new ClothFootRenderFrame(100, 0, guardBeyondBudget).CanRender(100, .01));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1d)]
    public void InvalidSampleOrRenderClockFailsClosed(double invalid)
    {
        Assert.False(new ClothFootRenderFrame(100, invalid, Boots()).CanRender(100, 0));
        Assert.False(new ClothFootRenderFrame(100, 0, Boots()).CanRender(100, invalid));
    }

    [Fact]
    public void SuppressionAlwaysWinsEvenWithCompleteBootsOrFullFallback()
    {
        Assert.False(new ClothFootRenderFrame(100, 0, Boots(), suppressed: true).CanRender(100, .01));
        Assert.False(new ClothFootRenderFrame(100, 0, ClothFootClearance.WithFallback([], Vector3.Zero), true).CanRender(100, .01));
    }

    [Fact]
    public void MissingPoseFallbackIsRetainedAsRawEvidenceButCannotAuthorizeAgedDrawing()
    {
        var fallback = ClothFootClearance.WithFallback([], new Vector3(2, 3, 4));
        Assert.Single(fallback, foot => foot.Valid);
        var frame = new ClothFootRenderFrame(100, 0, fallback);
        Assert.False(frame.CanRender(100, 0)); Assert.False(frame.CanRender(100, .01));
        Assert.Equal(fallback, frame.Contacts.ToArray());
        var partial = ClothFootClearance.WithFallback(Boots().AsSpan(0, 2), Vector3.Zero);
        Assert.False(new ClothFootRenderFrame(100, 0, partial).CanRender(100, .01));
    }

    [Fact]
    public void MissingOrIncompleteBootsDoNotCountAsProtectionForBothFeet()
    {
        Assert.False(new ClothFootRenderFrame(100, 0, []).CanRender(100, 0));
        Assert.False(new ClothFootRenderFrame(100, 0, new ClothFootContact[4]).CanRender(100, 0));
        for (var count = 1; count < 4; count++)
            Assert.False(new ClothFootRenderFrame(100, 0, Boots().AsSpan(0, count)).CanRender(100, .01));
        var missingToe = Boots(); missingToe[3] = default;
        Assert.False(new ClothFootRenderFrame(100, 0, missingToe).CanRender(100, .01));
    }

    [Theory]
    [InlineData(0)] // unlike heel/toe heights
    [InlineData(1)] // unlike radii
    [InlineData(2)] // implausibly long heel/toe span
    [InlineData(3)] // oversized measured foot, but not a full conservative fallback
    public void MalformedMeasuredPairsCannotPassAsCompleteBoots(int kind)
    {
        var contacts = Boots();
        if (kind == 0) contacts[1] = contacts[1] with { FootY = .1f };
        if (kind == 1) contacts[1] = contacts[1] with { Radius = .23f };
        if (kind == 2) contacts[1] = contacts[1] with { Center = new(-.15f, .7f) };
        if (kind == 3)
        { contacts[0] = contacts[0] with { Radius = .35f }; contacts[1] = contacts[1] with { Radius = .35f }; }
        Assert.False(new ClothFootRenderFrame(100, 0, contacts).CanRender(100, .01));
    }

    [Fact]
    public void SmallerLargeDiscIsNotMistakenForTheFullFallbackEnvelope()
    {
        ClothFootContact[] contacts = [new(Vector2.Zero, -2, ClothFootClearance.FallbackRadius - .01f), default, default, default];
        Assert.False(new ClothFootRenderFrame(100, 0, contacts).CanRender(100, .01));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void MalformedNonDefaultContactInvalidatesEvenAnOtherwiseFullFallback(int kind)
    {
        var contacts = ClothFootClearance.WithFallback([], Vector3.Zero);
        contacts[1] = kind switch
        {
            0 => new(new(float.NaN), 0, .2f),
            1 => new(Vector2.Zero, float.PositiveInfinity, .2f),
            2 => new(Vector2.Zero, 0, 10),
            _ => new(Vector2.One, 1, 0),
        };
        Assert.False(new ClothFootRenderFrame(100, 0, contacts).CanRender(100, .01));
    }

    [Theory]
    [InlineData(ClothFootRenderGate.Allowed)]
    [InlineData(ClothFootRenderGate.ShortOutput)]
    [InlineData(ClothFootRenderGate.Suppressed)]
    [InlineData(ClothFootRenderGate.IncompleteTracking)]
    [InlineData(ClothFootRenderGate.WrongZone)]
    [InlineData(ClothFootRenderGate.InvalidClock)]
    [InlineData(ClothFootRenderGate.Expired)]
    [InlineData(ClothFootRenderGate.UnsupportedEnvelope)]
    public void DiagnosticReasonPreservesTheExactLegacyGateAndClearsRejectedOutput(ClothFootRenderGate expected)
    {
        var contacts = Boots();
        if (expected == ClothFootRenderGate.IncompleteTracking) contacts[3] = default;
        if (expected == ClothFootRenderGate.UnsupportedEnvelope)
            for (var i = 0; i < contacts.Length; i++) contacts[i] = contacts[i] with { FootY = -1_000_000 };
        var frame = new ClothFootRenderFrame(100, 10, contacts, expected == ClothFootRenderGate.Suppressed);
        var zone = expected == ClothFootRenderGate.WrongZone ? 101u : 100u;
        var now = expected switch
        {
            ClothFootRenderGate.InvalidClock => double.NaN,
            ClothFootRenderGate.Expired => 10 + ClothFootRenderFrame.MaximumAgeSeconds + .001,
            _ => 10.01,
        };
        var output = Boots(); var legacyOutput = Boots();
        var size = expected == ClothFootRenderGate.ShortOutput ? 3 : 4;
        var accepted = frame.TryGetRenderContacts(zone, now, output.AsSpan(0, size), out var actual);
        Assert.Equal(expected, actual);
        Assert.Equal(expected == ClothFootRenderGate.Allowed, accepted);
        Assert.Equal(frame.TryGetRenderContacts(zone, now, legacyOutput.AsSpan(0, size)), accepted);
        Assert.Equal(legacyOutput, output);
        if (!accepted) Assert.All(output.Take(size), contact => Assert.Equal(default, contact));
    }

    [Fact]
    public void MissingTrackingIsReportedBeforeAgeSoOldMissingPoseIsNotMisdiagnosedAsSlowRendering()
    {
        var frame = new ClothFootRenderFrame(100, 10, []);
        var output = Boots();
        Assert.False(frame.TryGetRenderContacts(100, 100, output, out var gate));
        Assert.Equal(ClothFootRenderGate.IncompleteTracking, gate);
        Assert.All(output, contact => Assert.Equal(default, contact));
    }
}
