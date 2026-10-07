using System.Text.Json;
using XivPiano.Core;
using XivSurface.Core;

public sealed class SpectrumVisualizationIntegrationTests
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static AudioFeatures Tone(float gain = .3f)
    {
        const int rate = 48000;
        var pcm = new float[rate * 2];
        for (var i = 0; i < rate; i++)
        {
            var value = gain * (float)Math.Sin(Math.Tau * 1000 * i / rate);
            pcm[i * 2] = value; pcm[i * 2 + 1] = -value;
        }
        var producer = new AudioFeatures(rate, 2, 1); producer.Push(pcm); return producer;
    }

    [Theory]
    [InlineData(MusicVisualizationMode.SpectrumCrown)]
    [InlineData(MusicVisualizationMode.RadialRibbons)]
    [InlineData(MusicVisualizationMode.SpiralFountain)]
    [InlineData(MusicVisualizationMode.OrbitHalo)]
    [InlineData(MusicVisualizationMode.HelixCanopy)]
    [InlineData(MusicVisualizationMode.PrismBloom)]
    [InlineData(MusicVisualizationMode.WaveDome)]
    [InlineData(MusicVisualizationMode.StarFountain)]
    [InlineData(MusicVisualizationMode.AuroraVeil)]
    [InlineData(MusicVisualizationMode.ResonanceArches)]
    public void RealAudiblePcmDrivesAllThreeDimensionalModes(MusicVisualizationMode mode)
    {
        var producer = Tone(); var consumer = new MusicSpectrumConsumer();
        var frame = producer.Read(.5, true, 10);
        Assert.True(consumer.Update(JsonSerializer.Serialize(frame, Json), 10));
        var bands = consumer.Sample(10);
        Assert.Equal(32, bands.Length);
        Assert.True(bands.ToArray().Max() > .01f);
        var outward = new MusicVertex[MusicVisualization.MaximumVertices];
        var inward = new MusicVertex[MusicVisualization.MaximumVertices];
        var count = MusicVisualization.Build(outward, bands, mode, MusicRadialDirection.CenterOut, 2, 1, .9, .8f);
        Assert.InRange(count, 3, outward.Length);
        Assert.Equal(0, count % 3);
        Assert.Equal(count, MusicVisualization.Build(inward, bands, mode, MusicRadialDirection.RimIn, 2, 1, .9, .8f));
        Assert.Contains(outward.Take(count), vertex => vertex.Position.Y > .001f);
        Assert.False(outward.AsSpan(0, count).SequenceEqual(inward.AsSpan(0, count)));
        foreach (var vertex in outward.Take(count))
        {
            Assert.InRange(vertex.Position.X * vertex.Position.X + vertex.Position.Z * vertex.Position.Z, 0, 4.00001f);
            Assert.InRange(vertex.Position.Y, 0, 1);
        }
    }

    [Fact]
    public void PausedAndExpiredAudibleFramesProduceNoVisualGeometry()
    {
        var producer = Tone(); var consumer = new MusicSpectrumConsumer();
        Assert.True(consumer.Update(JsonSerializer.Serialize(producer.Read(.5, true, 10), Json), 10));
        var output = new MusicVertex[MusicVisualization.MaximumVertices];
        Assert.True(MusicVisualization.Build(output, consumer.Sample(10), MusicVisualizationMode.SpectrumCrown,
            MusicRadialDirection.CenterOut, 2, 1, .9, .8f) > 0);
        Assert.Empty(consumer.Sample(10.201).ToArray());
        Assert.Equal(0, MusicVisualization.Build(output, consumer.Sample(10.201), MusicVisualizationMode.SpectrumCrown,
            MusicRadialDirection.CenterOut, 2, 1, .9, .8f));
        Assert.False(consumer.Update(JsonSerializer.Serialize(producer.Read(.6, false, 10.3), Json), 10.3));
        Assert.Empty(consumer.Sample(10.3).ToArray());
    }

    [Fact]
    public void MutedRealPcmDoesNotInventAVisualizer()
    {
        var producer = Tone(0); var consumer = new MusicSpectrumConsumer();
        Assert.False(consumer.Update(JsonSerializer.Serialize(producer.Read(.5, true, 10), Json), 10));
        var output = new MusicVertex[MusicVisualization.MaximumVertices];
        Assert.Equal(0, MusicVisualization.Build(output, consumer.Sample(10), MusicVisualizationMode.SpiralFountain,
            MusicRadialDirection.CenterOut, 2, 1, 1000, .8f));
    }

    [Fact]
    public void NewSpectrumExtensionRemainsReadableByExistingClothResponse()
    {
        var producer = Tone(); var oldConsumer = new PianoClothResponse();
        var json = JsonSerializer.Serialize(producer.Read(.5, true, 10), Json);
        Assert.True(oldConsumer.Update(json, 10, default, .55f));
    }
}
