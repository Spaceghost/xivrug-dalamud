using System.Numerics;

namespace XivSurface.Core.Tests;

public sealed class AdaptiveClothFootEnvelopeTests
{
    private static ClothFootContact[] Feet(float radius) =>
    [
        new(new(-.15f,-.15f),.004f,radius),new(new(-.15f,.15f),.004f,radius),
        new(new(.15f,-.15f),.004f,radius),new(new(.15f,.15f),.004f,radius),
    ];
    private static ClothMesh Mesh() => new(
        [new(-.1f,.08f,-.1f),new(.1f,.08f,-.1f),new(0,.08f,.1f)],
        [Vector3.UnitY,Vector3.UnitY,Vector3.UnitY],new Vector2[3],[0,2,1],2,new float[3]);

    [Theory]
    [InlineData(0d)][InlineData(.03d)][InlineData(.055d)][InlineData(.075d)][InlineData(.1d)]
    public void EveryAdaptiveProducerEnvelopeIsAcceptedByActualRenderConsumer(double age)
    {
        foreach(var sampledAt in new[]{0d,1000d,1479225d})
        foreach(var radius in new[]{.11f,ClothFootClearance.MaximumBootRadius})
        {
            var measured=Feet(radius);
            var frame=new ClothFootRenderFrame(129,sampledAt,measured,
                clothMaximumAgeSeconds:ClothFootRenderFrame.MaximumAdaptiveClothAgeSeconds);
            var contacts=new ClothFootContact[4];
            Assert.True(frame.TryGetClothRenderContacts(129,sampledAt+age,contacts,out var gate));
            Assert.Equal(ClothFootRenderGate.Allowed,gate);
            Assert.All(contacts,c=>Assert.InRange(c.Radius,radius,ClothFootRenderFrame.MaximumClothRenderRadius));
            Assert.All(ClothContactConstraint.BuildRenderCeilings(Mesh(),contacts,0),
                y=>Assert.Equal(ClothContactConstraint.Clearance,y));
            Assert.Equal(measured,frame.Contacts.ToArray());
        }
    }

    [Fact]
    public void AdaptiveBoundDoesNotWidenTheIndependentMaskFreshnessGate()
    {
        var frame=new ClothFootRenderFrame(129,10,Feet(.34f),clothMaximumAgeSeconds:.1);
        var contacts=new ClothFootContact[4];
        Assert.False(frame.TryGetRenderContacts(129,10.075,contacts,out var oldGate));
        Assert.Equal(ClothFootRenderGate.Expired,oldGate);
        Assert.All(contacts,c=>Assert.Equal(default,c));
        Assert.True(frame.TryGetClothRenderContacts(129,10.075,contacts,out _));
        Assert.True(contacts[0].Radius>ClothFootRenderFrame.MaximumRenderRadius);
        Assert.All(ClothContactConstraint.BuildRenderCeilings(Mesh(),contacts,0),
            y=>Assert.Equal(ClothContactConstraint.Clearance,y));
    }

    [Fact]
    public void ConsumerStillRejectsAnythingBeyondDerivedClothBoundOrFallback()
    {
        foreach(var radius in new[]{MathF.BitIncrement(ClothFootRenderFrame.MaximumClothRenderRadius),ClothFootClearance.FallbackRadius})
            Assert.Throws<ArgumentException>(()=>ClothContactConstraint.BuildRenderCeilings(Mesh(),[new(Vector2.Zero,0,radius)],0));
    }

    [Fact]
    public void LongUptimeEndpointNeverPublishesAnOutOfRangeEnvelope()
    {
        const double sampledAt=16777216d;
        var frame=new ClothFootRenderFrame(129,sampledAt,Feet(.34f),clothMaximumAgeSeconds:.1);
        var contacts=new ClothFootContact[4];
        Assert.True(frame.TryGetClothContacts(129,sampledAt+.1,contacts,out _));
        // Addition passes the existing inclusive expiry policy, but subtracting
        // this large timestamp produces an age slightly greater than .1.
        Assert.True((sampledAt+.1)-sampledAt>.1);
        Assert.False(frame.TryGetClothRenderContacts(129,sampledAt+.1,contacts,out var gate));
        Assert.Equal(ClothFootRenderGate.UnsupportedEnvelope,gate);
        Assert.All(contacts,c=>Assert.Equal(default,c));
        // An ordinary slightly younger sample still produces a usable clamp.
        Assert.True(frame.TryGetClothRenderContacts(129,sampledAt+.099,contacts,out _));
        Assert.All(ClothContactConstraint.BuildRenderCeilings(Mesh(),contacts,0),
            y=>Assert.Equal(ClothContactConstraint.Clearance,y));
    }

    [Fact]
    public void ExpiredAdaptiveFrameClearsOutputRatherThanPassingItToRenderer()
    {
        var frame=new ClothFootRenderFrame(129,10,Feet(.34f),clothMaximumAgeSeconds:.1);
        var contacts=new ClothFootContact[4];
        Assert.True(frame.TryGetClothRenderContacts(129,10.075,contacts,out _));
        Assert.False(frame.TryGetClothRenderContacts(129,10.10001,contacts,out var gate));
        Assert.Equal(ClothFootRenderGate.Expired,gate);
        Assert.All(contacts,c=>Assert.Equal(default,c));
    }
}
