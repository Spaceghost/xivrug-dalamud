using System.Numerics;

namespace XivSurface.Core.Tests;

public sealed class ClothFootCadenceTests
{
    private static readonly ClothFootCaptureIdentity Owner = new(129, 1234, 0x123400);
    private static ClothFootContact[] Boots() =>
    [
        new(new(-.15f, -.1f), .004f, .22f), new(new(-.15f, .1f), .004f, .22f),
        new(new(.15f, -.1f), .004f, .22f), new(new(.15f, .1f), .004f, .22f),
    ];

    [Fact]
    public void SlowFrameCadenceKeepsClothAt45MillisecondsWithActualAgeSweep()
    {
        var publisher = new ClothFootRenderPublisher();
        PublishCycle(publisher, 0);
        var frame = PublishCycle(publisher, .045);
        Assert.Equal(.0725, frame.ClothMaximumAgeSeconds, 8);
        Span<ClothFootContact> cloth = stackalloc ClothFootContact[4];
        var now = frame.SampledAt + .045;
        Assert.True(frame.TryGetClothRenderContacts(Owner.Zone, now, cloth, out _));
        Assert.True(cloth[0].Radius >= .22 + 20 * .045);
        Assert.True(cloth[0].FootY <= .004 - 20 * .045);
        Assert.Equal(Boots(), frame.Contacts.ToArray());
        Assert.False(frame.TryGetRenderContacts(Owner.Zone, now, cloth, out var legacyGate));
        Assert.Equal(ClothFootRenderGate.Expired, legacyGate);
    }

    [Fact]
    public void LateReadInheritsFrameCadenceRatherThanEarlyToLateWorkDuration()
    {
        var publisher = new ClothFootRenderPublisher();
        PublishCycle(publisher, 0);
        publisher.BeginUpdate(Owner);
        var early = publisher.PublishEarlyCapture(Owner, Owner, .044, .045, Boots())!;
        var late = publisher.CompleteUpdate(true, Owner, Owner, .047, .048, Boots())!;
        Assert.Equal(.071, early.ClothMaximumAgeSeconds, 8);
        Assert.Equal(early.ClothMaximumAgeSeconds, late.ClothMaximumAgeSeconds);
        Assert.Equal(.044, early.SampledAt); Assert.Equal(.047, late.SampledAt);
    }

    [Fact]
    public void PublicationsKeepTheirOwnImmutableExpiryAndNeverExceed100Milliseconds()
    {
        var publisher = new ClothFootRenderPublisher();
        var old = PublishCycle(publisher, 0);
        var frame = PublishCycle(publisher, .2);
        Assert.Equal(ClothFootRenderFrame.MaximumAgeSeconds, old.ClothMaximumAgeSeconds);
        Assert.Equal(.1, frame.ClothMaximumAgeSeconds);
        Span<ClothFootContact> contacts = stackalloc ClothFootContact[4];
        Assert.False(old.TryGetClothRenderContacts(Owner.Zone, .05, contacts, out _));
        Assert.True(frame.TryGetClothRenderContacts(Owner.Zone, frame.SampledAt + .1, contacts, out _));
        Assert.InRange(contacts[0].Radius, 2.22f, 2.23f);
        Assert.True(contacts[0].FootY <= .004 - 2);
        publisher.BeginUpdate(Owner); // No new endpoint read occurred.
        Assert.Same(frame, publisher.Current);
        Assert.False(frame.TryGetClothRenderContacts(Owner.Zone, frame.SampledAt + .100001, contacts, out var gate));
        Assert.Equal(ClothFootRenderGate.Expired, gate);
    }

    [Theory]
    [InlineData(double.NaN, .251)]
    [InlineData(.25, double.NaN)]
    [InlineData(.25, .249)]
    [InlineData(.15, .151)]
    [InlineData(.25, .30)]
    public void MalformedRollbackAndStalledReadsRejectPublicationAndResetCadence(double start, double end)
    {
        var publisher = new ClothFootRenderPublisher();
        PublishCycle(publisher, 0); Assert.Equal(.1, PublishCycle(publisher, .2).ClothMaximumAgeSeconds);
        publisher.BeginUpdate(Owner);
        Assert.Null(publisher.CompleteUpdate(true, Owner, Owner, start, end, Boots()));
        Assert.Null(publisher.Current);
        Assert.Equal(ClothFootRenderFrame.MaximumAgeSeconds, PublishCycle(publisher, .4).ClothMaximumAgeSeconds);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void NewZonePlayerOrAddressCannotInheritPreviousCadence(int change)
    {
        var publisher = new ClothFootRenderPublisher();
        PublishCycle(publisher, 0); PublishCycle(publisher, .2);
        var next = change switch { 0 => Owner with { Zone = 130 }, 1 => Owner with { PlayerId = 4321 }, _ => Owner with { Address = 0x888800 } };
        Assert.Null(publisher.BeginUpdate(next));
        var frame = publisher.CompleteUpdate(true, next, next, .4, .401, Boots());
        Assert.NotNull(frame);
        Assert.Equal(ClothFootRenderFrame.MaximumAgeSeconds, frame.ClothMaximumAgeSeconds);
    }

    [Fact]
    public void FasterConfirmedCaptureCyclesReturnToTheOriginalBound()
    {
        var publisher = new ClothFootRenderPublisher();
        PublishCycle(publisher, 0);
        Assert.Equal(.08, PublishCycle(publisher, .05).ClothMaximumAgeSeconds, 8);
        foreach (var now in new[] { .06, .07, .08 }) PublishCycle(publisher, now);
        Assert.Equal(ClothFootRenderFrame.MaximumAgeSeconds, PublishCycle(publisher, .09).ClothMaximumAgeSeconds);
    }

    [Fact]
    public void DirectlyCreatedFrameRetainsTheOriginalDefaultAndRejectsInvalidLimits()
    {
        var frame = new ClothFootRenderFrame(Owner.Zone, 0, Boots());
        Span<ClothFootContact> contacts = stackalloc ClothFootContact[4];
        Assert.False(frame.TryGetClothRenderContacts(Owner.Zone, .034, contacts, out _));
        foreach (var age in new[] { double.NaN, double.PositiveInfinity, -.1, .02, .100001 })
            Assert.Throws<ArgumentOutOfRangeException>(() => new ClothFootRenderFrame(Owner.Zone, 0, Boots(), clothMaximumAgeSeconds: age));
    }

    [Theory]
    [InlineData(.09)]
    [InlineData(.1)]
    public void LongLivedClothContactsPassFinalRenderConstraintsWithoutLoweringSlopedGround(double age)
    {
        var measured = Boots().Select(foot => foot with { Radius = ClothFootClearance.MaximumBootRadius }).ToArray();
        var frame = new ClothFootRenderFrame(Owner.Zone, 0, measured,
            clothMaximumAgeSeconds: ClothFootRenderFrame.MaximumAdaptiveClothAgeSeconds);
        Span<ClothFootContact> swept = stackalloc ClothFootContact[4];
        Assert.True(frame.TryGetClothRenderContacts(Owner.Zone, age, swept, out var gate));
        Assert.Equal(ClothFootRenderGate.Allowed, gate);
        Assert.All(swept.ToArray(), foot => Assert.InRange(foot.Radius, 2.14f, ClothFootRenderFrame.MaximumClothRenderRadius));

        // A real triangle farther away than the old 1.45y render limit, but
        // inside the actual swept boot core. Its three ground heights differ.
        float[] ground = [-.1f, .2f, 0];
        var mesh = new ClothMesh(
            [new(1.7f, ground[0] + .035f, 0), new(1.8f, ground[1] + .035f, 0), new(1.7f, ground[2] + .035f, .1f)],
            [Vector3.UnitY, Vector3.UnitY, Vector3.UnitY],
            [Vector2.Zero, Vector2.UnitX, Vector2.UnitY], [0, 2, 1], 2, ground);
        var before = mesh.Positions.ToArray();
        var ceilings = ClothContactConstraint.BuildRenderCeilings(mesh, swept, 8);
        var positions = new Vector3[3]; var normals = new Vector3[3];
        ClothRenderPose.Prepare(mesh, ceilings, new(2), false, .25f, 1.2f, true, true, 8, positions, normals);
        for (var i = 0; i < positions.Length; i++)
        {
            Assert.Equal(ground[i] + ClothContactConstraint.Clearance, positions[i].Y, 6);
            Assert.True(float.IsFinite(normals[i].X) && float.IsFinite(normals[i].Y) && float.IsFinite(normals[i].Z));
            Assert.InRange(normals[i].Length(), .99999f, 1.00001f);
        }
        // An interior point keeps the same affine measured floor too.
        Assert.Equal(ground.Average() + ClothContactConstraint.Clearance, positions.Average(p => p.Y), 6);
        Assert.Equal(before, mesh.Positions);
        Assert.Equal(measured, frame.Contacts.ToArray());
        Assert.False(frame.TryGetRenderContacts(Owner.Zone, age, swept, out var legacyGate));
        Assert.Equal(ClothFootRenderGate.Expired, legacyGate);
    }

    private static ClothFootRenderFrame PublishCycle(ClothFootRenderPublisher publisher, double start)
    {
        publisher.BeginUpdate(Owner);
        Assert.NotNull(publisher.PublishEarlyCapture(Owner, Owner, start, start + .001, Boots()));
        return Assert.IsType<ClothFootRenderFrame>(publisher.CompleteUpdate(true, Owner, Owner, start + .003, start + .004, Boots()));
    }
}
