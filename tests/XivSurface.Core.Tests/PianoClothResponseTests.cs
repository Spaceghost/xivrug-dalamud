using System.Numerics;
using System.Text.Json;

namespace XivSurface.Core.Tests;

public sealed class PianoClothResponseTests
{
    private static string Sample(long stream = 1, long sequence = 1, bool playing = true,
        bool fresh = true, float onset = .5f, float rms = .25f, double age = .01, int version = 1) =>
        JsonSerializer.Serialize(new { version, streamId = stream, sequence, playing, fresh,
            ageSeconds = age, positionSeconds = 1d, rms, peak = .7f, low = .2f, mid = .1f, high = .3f, onset });

    [Fact]
    public void AudibleAttackCreatesOneLocalizedBoundedImpulseNotAPollTimer()
    {
        var response = new PianoClothResponse(); var center = new Vector2(12, -3);
        Assert.True(response.Update(Sample(), 1, center, .5f));
        var impulse = Assert.Single(response.Impulses.ToArray());
        Assert.Equal(center, impulse.Center); Assert.Equal(1, impulse.Time);
        Assert.InRange(impulse.Strength, .001f, 1); Assert.InRange(impulse.Tone, 0, 1);
        for (var i = 1; i <= 30; i++)
            Assert.True(response.Update(Sample(sequence: i + 1), 1 + i * .05, center, .5f));
        Assert.Single(response.Impulses.ToArray());
    }

    [Fact]
    public void SameWindowCannotRetriggerAndSilenceCannotInventNotes()
    {
        var response = new PianoClothResponse();
        response.Update(Sample(), 0, Vector2.Zero, 1);
        response.Update(Sample(), .15, Vector2.One, 1);
        Assert.Single(response.Impulses.ToArray());
        response.Update(Sample(sequence: 2, onset: 0, rms: 0), .2, Vector2.Zero, 1);
        response.Update(Sample(sequence: 3, onset: 1, rms: 0), .4, Vector2.Zero, 1);
        Assert.Single(response.Impulses.ToArray());
        response.Update(Sample(sequence: 4, onset: 0), .6, Vector2.Zero, 1);
        response.Update(Sample(sequence: 5, onset: .8f), .8, Vector2.Zero, 1);
        Assert.Equal(2, response.Impulses.Length);
    }

    [Theory]
    [InlineData(false, true, .01)]
    [InlineData(true, false, .01)]
    [InlineData(true, true, .21)]
    public void PauseAndStaleAudioClearMusicForces(bool playing, bool fresh, double age)
    {
        var response = new PianoClothResponse(); response.Update(Sample(), 0, Vector2.Zero, 1);
        Assert.False(response.Update(Sample(sequence: 2, playing: playing, fresh: fresh, age: age), .1, Vector2.Zero, 1));
        Assert.Empty(response.Impulses.ToArray());
    }

    [Fact]
    public void TrackChangesAndCursorRewindsCannotReuseOldForces()
    {
        var response = new PianoClothResponse(); response.Update(Sample(sequence: 20), 0, Vector2.Zero, 1);
        response.Update(Sample(stream: 2), .2, Vector2.One, 1);
        Assert.Equal(Vector2.One, Assert.Single(response.Impulses.ToArray()).Center);
        response.Update(Sample(stream: 2, sequence: 10, onset: 0), .4, Vector2.One, 1);
        Assert.False(response.Update(Sample(stream: 2, sequence: 9), .6, Vector2.One, 1));
        Assert.Empty(response.Impulses.ToArray());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"version\":2}")]
    [InlineData("{garbage}")]
    public void MissingOrMalformedOptionalProviderIsHarmless(string? json)
    {
        var response = new PianoClothResponse();
        Assert.False(response.Update(json, 0, Vector2.Zero, 1));
        Assert.Empty(response.Impulses.ToArray());
    }

    [Fact]
    public void InputsAreBoundedAndRegressionDropsOldState()
    {
        var response = new PianoClothResponse();
        Assert.False(response.Update(new string(' ',4097), 0, Vector2.Zero, 1));
        Assert.False(response.Update(Sample(onset: 2), 0, Vector2.Zero, 1));
        Assert.False(response.Update(Sample(age: -1), 0, Vector2.Zero, 1));
        Assert.False(response.Update(Sample(), 0, new(float.NaN), 1));
        Assert.False(response.Update(Sample(), 0, Vector2.Zero, float.NaN));
        response.Update(Sample(), 1, Vector2.Zero, 1);
        Assert.False(response.Update(Sample(sequence:2), .9, Vector2.Zero, 1));
        Assert.Empty(response.Impulses.ToArray());
    }

    [Fact]
    public void RapidAttacksCannotGrowAnUnboundedClothQueue()
    {
        var response = new PianoClothResponse();
        for (var i = 0; i < 100; i++)
            response.Update(Sample(sequence:i+1,onset:i%2==0 ? .8f : 0),i*.05,Vector2.Zero,1);
        Assert.InRange(response.Impulses.Length,1,PianoClothResponse.MaximumImpulses);
        response.Update(Sample(sequence:101,onset:0),10,Vector2.Zero,1);
        Assert.Empty(response.Impulses.ToArray());
    }
}
