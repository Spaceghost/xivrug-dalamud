using System.Numerics;
using System.Text.Json;
using XivPiano.Core;
using XivSurface.Core;

public sealed class ProducerConsumerTests(ITestOutputHelper output)
{
    private const int Rate = 48000;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static AudioFeatures Producer(long stream = 1, float rawPeak = .5f, float volume = .6f,
        double frequency = 100, double replayGainDb = 0, bool twoPulses = true)
    {
        // Stereo PCM after the same gain/volume/clipping operation used by MpegSampleProvider.Read.
        // Queue-ahead and hardware cursor are deliberately distinct in every integration test.
        var pcm = new float[Rate * 2];
        var gain = (float)Math.Pow(10, Math.Clamp(replayGainDb, -15, 6) / 20);
        for (var frame = 0; frame < Rate; frame++)
        {
            var t = (double)frame / Rate;
            var audible = t is >= .2 and < .34 || twoPulses && t is >= .6 and < .74;
            var x = audible ? Math.Clamp(rawPeak * volume * gain * (float)Math.Sin(Math.Tau * frequency * t), -1, 1) : 0;
            pcm[frame * 2] = x;
            pcm[frame * 2 + 1] = -x; // anti-phase must not vanish in the energy analyzer
        }
        var producer = new AudioFeatures(Rate, 2, stream);
        producer.Push(pcm);
        return producer;
    }

    private static (AudioFeatureSnapshot Frame, bool Accepted) Poll(AudioFeatures producer, PianoClothResponse consumer,
        double played, double now, bool playing = true, float strength = .55f)
    {
        var frame = producer.Read(played, playing, now);
        var accepted = consumer.Update(JsonSerializer.Serialize(frame, Json), now, new Vector2(12, -3), strength);
        return (frame, accepted);
    }

    [Fact]
    public void QueuedAttackDoesNotExciteClothUntilActuallyPlayed()
    {
        var producer = Producer(); var consumer = new PianoClothResponse();
        for (var i = 0; i <= 4; i++)
        {
            Poll(producer, consumer, i * .05, 10 + i * .05);
            Assert.Empty(consumer.Impulses.ToArray());
        }
        var played = Poll(producer, consumer, .25, 10.25);
        Assert.True(played.Accepted);
        Assert.True(played.Frame.Fresh);
        Assert.Equal(12, played.Frame.Sequence);
        Assert.InRange(played.Frame.Rms, .212f, .213f);
        Assert.InRange(played.Frame.Onset, .77f, .79f);
        var impulse = Assert.Single(consumer.Impulses.ToArray());
        Assert.Equal(new Vector2(12, -3), impulse.Center);
        Assert.Equal(10.25, impulse.Time);
        Assert.InRange(impulse.Strength, .78f, .80f);
    }

    [Fact]
    public void LateSubscriberDoesNotRequireSequenceOneAndGetsNextPlayedAttack()
    {
        var producer = Producer(); var consumer = new PianoClothResponse();
        var initial = Poll(producer, consumer, .45, 10);
        Assert.True(initial.Accepted);
        Assert.True(initial.Frame.Sequence > 1);
        Assert.Equal(0, initial.Frame.Onset);
        Assert.Empty(consumer.Impulses.ToArray());
        for (var i = 1; i <= 4; i++) Poll(producer, consumer, .45 + i * .05, 10 + i * .05);
        Assert.Single(consumer.Impulses.ToArray());
    }

    [Fact]
    public void JoiningDuringSoundOnlySuppressesOneWindowNotFutureResponse()
    {
        var producer = Producer(); var consumer = new PianoClothResponse();
        Assert.Equal(0, Poll(producer, consumer, .25, 10).Frame.Onset);
        Assert.Empty(consumer.Impulses.ToArray());
        Assert.True(Poll(producer, consumer, .3, 10.05).Frame.Onset > .035);
        Assert.Single(consumer.Impulses.ToArray());
    }

    [Fact]
    public void PauseClearsForcesResumeStartsQuietAndNextAttackWorks()
    {
        var producer = Producer(); var consumer = new PianoClothResponse();
        Poll(producer, consumer, .1, 10);
        Poll(producer, consumer, .25, 10.15);
        Assert.Single(consumer.Impulses.ToArray());
        var paused = Poll(producer, consumer, .25, 10.2, false);
        Assert.False(paused.Frame.Playing);
        Assert.False(paused.Accepted);
        Assert.Empty(consumer.Impulses.ToArray());
        var resumed = Poll(producer, consumer, .25, 15);
        Assert.True(resumed.Accepted);
        Assert.Equal(0, resumed.Frame.Onset);
        Assert.Empty(consumer.Impulses.ToArray());
        Poll(producer, consumer, .5, 15.25);
        Poll(producer, consumer, .6, 15.35);
        Poll(producer, consumer, .65, 15.4);
        Assert.Single(consumer.Impulses.ToArray());
    }

    [Fact]
    public void PluginReloadReusingStreamIdRecoversAfterOneRewindPoll()
    {
        var consumer = new PianoClothResponse(); var before = Producer();
        Poll(before, consumer, .1, 1);
        Poll(before, consumer, .25, 1.15);
        Poll(before, consumer, .8, 1.7);
        Assert.Single(consumer.Impulses.ToArray());
        var after = Producer(stream: 1);
        Assert.False(Poll(after, consumer, .05, 2).Accepted);
        Assert.Empty(consumer.Impulses.ToArray());
        Assert.True(Poll(after, consumer, .1, 2.05).Accepted);
        Poll(after, consumer, .25, 2.2);
        Assert.Single(consumer.Impulses.ToArray());
    }

    [Fact]
    public void NewGenerationCannotReuseOldImpulses()
    {
        var consumer = new PianoClothResponse(); var before = Producer();
        Poll(before, consumer, .1, 1);
        Poll(before, consumer, .25, 1.15);
        Assert.Single(consumer.Impulses.ToArray());
        var after = Producer(stream: 2);
        Assert.True(Poll(after, consumer, .05, 2).Accepted);
        Assert.Empty(consumer.Impulses.ToArray());
        Poll(after, consumer, .25, 2.2);
        Assert.Single(consumer.Impulses.ToArray());
    }

    [Fact]
    public void StalledHardwareCursorClearsImpulseEvenIfDecodedAudioExists()
    {
        var consumer = new PianoClothResponse(); var producer = Producer();
        Poll(producer, consumer, .1, 1);
        Poll(producer, consumer, .25, 1.15);
        Assert.Single(consumer.Impulses.ToArray());
        Assert.False(Poll(producer, consumer, .25, 1.36).Accepted);
        Assert.Empty(consumer.Impulses.ToArray());
    }

    [Fact]
    public void QuietAudiblePcmBelowProducerNoiseFloorHasNoOnsetOrImpulse()
    {
        var consumer = new PianoClothResponse(); var producer = Producer(rawPeak: .01f);
        Poll(producer, consumer, .1, 1);
        var frame = Poll(producer, consumer, .25, 1.15).Frame;
        Assert.True(frame.Playing && frame.Fresh);
        Assert.InRange(frame.Rms, .0042f, .0043f);
        Assert.Equal(0, frame.Onset);
        Assert.Empty(consumer.Impulses.ToArray());
    }

    [Theory]
    [InlineData(.5f, 0d, 100d)]
    [InlineData(.2f, 0d, 100d)]
    [InlineData(.1f, 0d, 100d)]
    [InlineData(.05f, 0d, 100d)]
    [InlineData(.02f, 0d, 100d)]
    [InlineData(.5f, -12d, 100d)]
    [InlineData(.2f, -12d, 100d)]
    [InlineData(.5f, 0d, 800d)]
    [InlineData(.5f, 0d, 7000d)]
    public void QuantifyActualPointSixVolumeChain(float rawPeak, double replayDb, double frequency)
    {
        var consumer = new PianoClothResponse();
        var producer = Producer(rawPeak: rawPeak, replayGainDb: replayDb, frequency: frequency);
        Poll(producer, consumer, .1, 1);
        var frame = Poll(producer, consumer, .25, 1.15).Frame;
        Assert.True(frame.Fresh);
        var events = consumer.Impulses.ToArray();
        var strength = events.Length == 0 ? 0 : events[0].Strength;
        var tone = events.Length == 0 ? 0 : events[0].Tone;
        output.WriteLine($"MEASURE rawPeak={rawPeak:F3} volume=0.6 replayDb={replayDb:F1} frequency={frequency:F0} played=.25 rms={frame.Rms:F6} onset={frame.Onset:F6} impulses={events.Length} strength={strength:F6} tone={tone:F6}");
        Assert.InRange(strength, 0, 1);
        Assert.InRange(tone, 0, 1);
    }
}
