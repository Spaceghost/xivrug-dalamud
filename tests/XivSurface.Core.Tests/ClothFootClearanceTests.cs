using System.Numerics;
using XivSurface.Core;

namespace XivSurface.Core.Tests;

public sealed class ClothFootClearanceTests
{
    [Fact]
    public void FootCoreLowersOnlyToSoleCeilingAndRemovesLocalFlourishes()
    {
        ClothFootContact[] feet = [new(Vector2.Zero, .12f, .2f)];
        var result = ClothFootClearance.Evaluate(new(.03f, .3f, 0), .035f, feet);
        Assert.Equal(.12f - ClothFootClearance.SoleGap, result.Height, 5);
        Assert.Equal(0, result.FlourishScale); Assert.Equal(0, result.ExclusionWeight);
    }

    [Fact]
    public void FloorConflictNeverPushesClothThroughFloorInsteadRequestsExclusion()
    {
        ClothFootContact[] feet = [new(Vector2.Zero, .004f, .2f)];
        var result = ClothFootClearance.Evaluate(new(0, .3f, 0), .035f, feet);
        Assert.Equal(.035f, result.Height); Assert.Equal(1, result.ExclusionWeight);
        Assert.Equal(0, result.FlourishScale);
    }

    [Fact]
    public void RaisedFeetDoNotForceFictitiousFloorContacts()
    {
        Assert.True(ClothFootClearance.TryBoot(new(0,.55f,0), new(0,.5f,.2f), Vector3.Zero, 1, out var heel, out var toe));
        Assert.True(toe.FootY > .4f); Assert.Equal(heel.FootY, toe.FootY);
        var result = ClothFootClearance.Evaluate(new(0,.08f,.2f), .035f, [heel, toe]);
        Assert.Equal(.08f, result.Height); Assert.Equal(0, result.ExclusionWeight);
    }

    [Fact]
    public void NormalLowWalkingAndStandingFeetAreProtectedWithoutStompHistory()
    {
        Assert.True(ClothFootClearance.TryBoot(new(.1f,.14f,0), new(.1f,.08f,.2f), Vector3.Zero, 1, out var heel, out var toe));
        Assert.Equal(new Vector2(.1f,0), heel.Center); Assert.Equal(new Vector2(.1f,.2f), toe.Center);
        Assert.True(toe.FootY <= .004f); Assert.Equal(heel.FootY, toe.FootY);
        Assert.Equal(1, ClothFootClearance.Exclusion(new(.1f,.035f,.2f), [heel, toe]));
    }

    [Fact]
    public void ShaderLiftIsIncludedInCapAndExclusionWithoutChangingFloorLowerBound()
    {
        ClothFootContact[] feet = [new(Vector2.Zero, .4f, .2f)];
        var fitted = ClothFootClearance.Evaluate(new(0,.3f,0), .035f, feet, .2f);
        Assert.Equal(.4f - ClothFootClearance.SoleGap - .2f, fitted.Height, 5);
        Assert.Equal(0, fitted.ExclusionWeight);
        var conflict = ClothFootClearance.Evaluate(new(0,.3f,0), .035f, feet, .5f);
        Assert.Equal(.035f, conflict.Height); Assert.Equal(1, conflict.ExclusionWeight);
    }

    [Fact]
    public void OuterFeatherIsSmoothAndOutsideFootprintIsUnchanged()
    {
        var foot = new ClothFootContact(Vector2.Zero, 0, .2f);
        Assert.Equal(1, foot.Influence(new(.14f,0)), 5);
        Assert.Equal(.5f, foot.Influence(new(.17f,0)), 5);
        Assert.Equal(0, foot.Influence(new(.2f,0)), 5);
        var outside = ClothFootClearance.Evaluate(new(.21f,.3f,0), .035f, [foot]);
        Assert.Equal(.3f, outside.Height); Assert.Equal(1, outside.FlourishScale); Assert.Equal(0, outside.ExclusionWeight);
    }

    [Fact]
    public void OverlappingToeHeelConstraintsAreOrderIndependent()
    {
        var heel = new ClothFootContact(new(-.07f,0), .2f, .2f);
        var toe = new ClothFootContact(new(.07f,0), .1f, .2f);
        var first = ClothFootClearance.Evaluate(new(0,.3f,0), .035f, [heel,toe]);
        var second = ClothFootClearance.Evaluate(new(0,.3f,0), .035f, [toe,heel]);
        Assert.Equal(first, second); Assert.Equal(.1f - ClothFootClearance.SoleGap, first.Height, 5);
    }

    [Fact]
    public void GPUEncodingIsBoundedAndUsesWorldXZWithAbsoluteSoleHeight()
    {
        ClothFootContact[] feet = [new(new(3,5), 7, .2f), new(new(float.NaN), 0, .2f),
            new(Vector2.Zero, 0, 10), new(Vector2.Zero, float.PositiveInfinity, .2f), new(Vector2.Zero, 0, .2f)];
        Assert.Equal(new Vector4(3,7,5,.2f), ClothFootClearance.Pack(feet,0));
        foreach (var index in new[] {-1,1,2,3,4,5}) Assert.Equal(Vector4.Zero, ClothFootClearance.Pack(feet,index));
    }

    [Fact]
    public void CPUConstraintsUseTheSameFourContactBudgetAsTheRenderer()
    {
        ClothFootContact[] feet = [default, default, default, default, new(Vector2.Zero, 0, .2f)];
        var result = ClothFootClearance.Evaluate(new(0,.3f,0), .035f, feet);
        Assert.Equal(.3f, result.Height); Assert.Equal(1, result.FlourishScale); Assert.Equal(0, result.ExclusionWeight);
    }

    [Fact]
    public void MissingOrMalformedBoneSamplesNeverGuessFootPositions()
    {
        Assert.False(ClothFootClearance.TryBoot(Vector3.Zero, Vector3.Zero, Vector3.Zero, 1, out _, out _));
        Assert.False(ClothFootClearance.TryBoot(new(float.NaN), new(0,0,.2f), Vector3.Zero, 1, out _, out _));
        Assert.False(ClothFootClearance.TryBoot(new(10,0,0), new(10,0,.2f), Vector3.Zero, 1, out _, out _));
        Assert.False(ClothFootClearance.TryBoot(Vector3.Zero, new(0,0,.2f), Vector3.Zero, float.NaN, out _, out _));
        Assert.False(ClothFootClearance.TryBoot(Vector3.Zero, new(0,0,.2f), Vector3.Zero, 8, out _, out _));
        var result = ClothFootClearance.Evaluate(new(0,.3f,0), .035f, [new(Vector2.Zero, float.NaN, .2f)]);
        Assert.Equal(.3f, result.Height); Assert.Equal(1, result.FlourishScale); Assert.Equal(0, result.ExclusionWeight);
    }

    [Fact]
    public void BadFloorOrLiftFailsRatherThanReturningInvalidCloth()
    {
        Assert.Throws<ArgumentException>(() => ClothFootClearance.Evaluate(new(float.NaN), 0, []));
        Assert.Throws<ArgumentException>(() => ClothFootClearance.Evaluate(Vector3.Zero, float.NaN, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => ClothFootClearance.Evaluate(Vector3.Zero, 0, [], float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => ClothFootClearance.Evaluate(Vector3.Zero, 0, [], -1));
        Assert.Equal(1, ClothFootClearance.Exclusion(new(float.NaN), []));
    }

    [Fact]
    public void ClearanceNeverLowersBelowMeasuredFloorAcrossContactAndLiftRange()
    {
        for (var footY = -.1f; footY < .5f; footY += .05f)
        for (var x = -.3f; x < .3f; x += .01f)
        for (var lift = 0f; lift < .4f; lift += .05f)
        {
            var result = ClothFootClearance.Evaluate(new(x,.3f,0), .035f, [new(Vector2.Zero,footY,.2f)], lift);
            Assert.InRange(result.Height, .035f, .3f);
            Assert.InRange(result.ExclusionWeight, 0, 1); Assert.InRange(result.FlourishScale, 0, 1);
        }
    }

    [Fact]
    public void LongBootHasContinuousProtectionBetweenHeelAndToe()
    {
        Assert.True(ClothFootClearance.TryBoot(new(0,.12f,-.3f), new(0,.08f,.3f), Vector3.Zero, 1, out var heel, out var toe));
        Assert.Equal(0, heel.Influence(Vector2.Zero));
        Assert.Equal(0, toe.Influence(Vector2.Zero));
        for (var z = -.3f; z <= .3f; z += .025f)
        {
            Assert.Equal(1, ClothFootClearance.Exclusion(new(0,.035f,z), [heel,toe]));
            Assert.Equal(1, ClothFootClearance.Evaluate(new(0,.2f,z), .035f, [heel,toe]).ExclusionWeight);
        }
    }

    [Fact]
    public void IdenticalSoleHeightsNeverConnectLeftAndRightBoots()
    {
        ClothFootContact[] feet = [new(new(-.35f,-.1f),0,.15f), new(new(-.35f,.1f),0,.15f),
            new(new(.35f,-.1f),0,.15f), new(new(.35f,.1f),0,.15f)];
        Assert.Equal(0, ClothFootClearance.Exclusion(new(0,.1f,0), feet));
        Assert.Equal(1, ClothFootClearance.Exclusion(new(-.35f,.1f,0), feet));
        Assert.Equal(1, ClothFootClearance.Exclusion(new(.35f,.1f,0), feet));
    }

    [Theory]
    [InlineData(.8f, 0, .15f)]
    [InlineData(.6f, .1f, .15f)]
    [InlineData(.6f, 0, .16f)]
    public void MalformedPairsDoNotInventConnectingCapsules(float span, float otherY, float otherRadius)
    {
        ClothFootContact[] feet = [new(new(-span/2,0),0,.15f),new(new(span/2,0),otherY,otherRadius)];
        Assert.Equal(0, ClothFootClearance.Exclusion(new(0,.5f,0), feet));
    }

    [Fact]
    public void MissingPoseGetsOneRenderOnlyGuardAtCurrentPlayerPosition()
    {
        var guard = ClothFootClearance.WithFallback([], new Vector3(12,3,7));
        var foot = Assert.Single(guard, f => f.Valid);
        Assert.Equal(new Vector2(12,7), foot.Center);
        Assert.Equal(3 - ClothFootClearance.FallbackSoleInset, foot.FootY);
        Assert.Equal(1, ClothFootClearance.Exclusion(new(12.5f,3.03f,7), guard));
        Assert.Equal(0, ClothFootClearance.Exclusion(new(12,1,7), guard));
        Assert.Equal(0, ClothFootClearance.Exclusion(new(16,3.03f,7), guard));
        var moved = ClothFootClearance.WithFallback([], new Vector3(20,4,5));
        Assert.Equal(0, ClothFootClearance.Exclusion(new(12,4.1f,7), moved));
        Assert.Equal(1, ClothFootClearance.Exclusion(new(20,4.1f,5), moved));
        Assert.DoesNotContain(ClothFootClearance.WithFallback([], null), f => f.Valid);
    }

    [Fact]
    public void PartialPoseKeepsCompleteBootAndAddsUnpairedFallback()
    {
        ClothFootContact[] measured = [new(new(1,0),.1f,.2f),new(new(1,.2f),.1f,.2f)];
        var guarded = ClothFootClearance.WithFallback(measured, Vector3.Zero);
        Assert.Equal(measured[0], guarded[0]); Assert.Equal(measured[1], guarded[1]);
        Assert.True(guarded[2].Valid); Assert.False(guarded[3].Valid);
        Assert.Equal(2, measured.Length);
        var complete = measured.Concat(measured.Select(f => f with { Center = f.Center + new Vector2(-2,0) })).ToArray();
        Assert.Equal(complete, ClothFootClearance.WithFallback(complete, Vector3.Zero));
    }

    [Fact]
    public void InvalidOrIncompleteSamplesCannotDisableFallbackOrJoinItToMeasuredFoot()
    {
        ClothFootContact[] measured = [new(new(1,0),0,.2f),default,default,default];
        var guarded = ClothFootClearance.WithFallback(measured, Vector3.Zero);
        Assert.Single(guarded, f => f.Valid);
        Assert.Equal(Vector2.Zero, guarded[0].Center); Assert.False(guarded[1].Valid);
        Assert.Equal(1, ClothFootClearance.Exclusion(new(1,.1f,0), guarded));
        foreach (var position in new[] { new Vector3(float.NaN), new Vector3(float.PositiveInfinity), new Vector3(1_000_001,0,0) })
            Assert.DoesNotContain(ClothFootClearance.WithFallback([], position), f => f.Valid);
    }

    [Fact]
    public void PoseLossOnLowerStairDoesNotExposeThePlantedBoot()
    {
        var player = new Vector3(0,1,0);
        Assert.True(ClothFootClearance.TryBoot(new(0,.85f,-.1f), new(0,.8f,.1f), player, 1,
            out var heel, out var toe));
        var cloth = new Vector3(0,.835f,0);
        Assert.Equal(1, ClothFootClearance.Exclusion(cloth, [heel,toe]));
        var missing = ClothFootClearance.WithFallback([], player);
        Assert.Equal(1, ClothFootClearance.Exclusion(cloth, missing));
        Assert.True(ClothFootClearance.Pack(missing,0).W > 0);
    }

    [Fact]
    public void MissingPoseGuardCoversTheEntireAdmittedFootEnvelope()
    {
        var player = new Vector3(5,7,9);
        var missing = ClothFootClearance.WithFallback([], player);
        Assert.Single(missing, foot => foot.Valid);
        // Check far/lower joints, maximum footwear margin, and rotated strides.
        // The guard must cover the soft boot edge with its fully excluded core.
        for (var i = 0; i < 32; i++)
        {
            var direction = new Vector2(MathF.Cos(i*MathF.Tau/32), MathF.Sin(i*MathF.Tau/32));
            var foot = player + new Vector3(direction.X*1.95f,-1.49f,direction.Y*1.95f);
            var toe = player + new Vector3(direction.X*1.75f,-1.49f,direction.Y*1.75f);
            Assert.True(ClothFootClearance.TryBoot(foot,toe,player,
                ClothFootClearance.MaximumBootRadius / ClothFootClearance.NominalBootRadius,
                out var heel, out var tip));
            for (var sample = 0; sample < 100; sample++)
            {
                // Stay strictly inside the feather; repeated float addition
                // can accidentally sample its fully transparent outer boundary.
                var offset = sample / 100f * heel.Radius;
                var cloth = new Vector3(foot.X+direction.X*offset, heel.FootY+.02f, foot.Z+direction.Y*offset);
                Assert.True(ClothFootClearance.Exclusion(cloth, [heel,tip]) > 0);
                Assert.Equal(1, ClothFootClearance.Exclusion(cloth, missing));
            }
        }
        Assert.True(ClothFootClearance.FallbackRadius*.7f >=
            ClothFootClearance.MaximumJointDistance+ClothFootClearance.MaximumBootRadius);
    }
}
