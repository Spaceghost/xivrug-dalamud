using Newtonsoft.Json;
using XivSurface.Core;

namespace XivRug.Configuration.Tests;

public sealed class MusicVisualizationConfigurationTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    [InlineData(8)] [InlineData(9)]
    public void EverySculptureKeepsItsSavedNumericSelection(int mode)
    {
        var config = new Plugin.Configuration { MusicVisualization = (MusicVisualizationMode)mode };
        var saved = JsonConvert.DeserializeObject<Plugin.Configuration>(JsonConvert.SerializeObject(config))!;
        saved.Sanitize();
        Assert.Equal(mode, (int)saved.MusicVisualization);
        Assert.Equal(2, saved.Radius);
    }

    [Fact]
    public void ExistingClothOptOutDoesNotRemoveNewIndependentSpectrumOption()
    {
        var config = JsonConvert.DeserializeObject<Plugin.Configuration>("{\"PianoResponse\":false,\"Radius\":3}")!;
        config.Sanitize();
        Assert.False(config.PianoResponse);
        Assert.True(config.MusicVisualizationEnabled);
        Assert.Equal(MusicVisualizationMode.SpectrumCrown, config.MusicVisualization);
        Assert.Equal(MusicRadialDirection.CenterOut, config.MusicDirection);
        Assert.Equal(3, config.Radius);
    }

    [Fact]
    public void SelectionDirectionAndExplicitOptOutSurviveRoundTrip()
    {
        var config = new Plugin.Configuration
        {
            MusicVisualizationEnabled = false,
            MusicVisualization = MusicVisualizationMode.SpiralFountain,
            MusicDirection = MusicRadialDirection.RimIn,
            MusicVisualizationHeight = 1.2f,
            MusicVisualizationStrength = .42f,
        };
        var loaded = JsonConvert.DeserializeObject<Plugin.Configuration>(JsonConvert.SerializeObject(config))!;
        loaded.Sanitize();
        Assert.False(loaded.MusicVisualizationEnabled);
        Assert.Equal(config.MusicVisualization, loaded.MusicVisualization);
        Assert.Equal(config.MusicDirection, loaded.MusicDirection);
        Assert.Equal(config.MusicVisualizationHeight, loaded.MusicVisualizationHeight);
        Assert.Equal(config.MusicVisualizationStrength, loaded.MusicVisualizationStrength);
    }

    [Theory]
    [InlineData(float.NaN, .8f, .65f)]
    [InlineData(float.PositiveInfinity, .8f, .65f)]
    [InlineData(-1, .1f, 0)]
    [InlineData(10, 2.5f, 1)]
    public void VisualParametersAreFiniteBoundedAndDoNotResizeRug(float value, float height, float strength)
    {
        var config = new Plugin.Configuration
        {
            MusicVisualization = (MusicVisualizationMode)99, MusicDirection = (MusicRadialDirection)99,
            MusicVisualizationHeight = value, MusicVisualizationStrength = value,
            Radius = 4, HalfWidth = 3, HalfLength = 2,
        };
        config.Sanitize();
        Assert.Equal(MusicVisualizationMode.SpectrumCrown, config.MusicVisualization);
        Assert.Equal(MusicRadialDirection.CenterOut, config.MusicDirection);
        Assert.Equal(height, config.MusicVisualizationHeight);
        Assert.Equal(strength, config.MusicVisualizationStrength);
        Assert.Equal(4, config.Radius); Assert.Equal(3, config.HalfWidth); Assert.Equal(2, config.HalfLength);
    }
}
