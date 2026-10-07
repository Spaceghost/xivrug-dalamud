using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using XivRug.Plugin;
using XivSurface.Core;

namespace XivRug.Configuration.Tests;

public sealed class ConfigurationTests
{
    [Fact]
    public void PianoResponseIsOptionalAndSavedOptOutSurvives()
    {
        var fresh = Read("{}"); fresh.Sanitize();
        Assert.True(fresh.PianoResponse); Assert.Equal(.55f, fresh.PianoStrength);
        var disabled = Read("{\"PianoResponse\":false,\"PianoStrength\":0.25}"); disabled.Sanitize();
        var reloaded = Read(JsonConvert.SerializeObject(disabled, DalamudSettings)); reloaded.Sanitize();
        Assert.False(reloaded.PianoResponse); Assert.Equal(.25f,reloaded.PianoStrength);
    }

    [Theory]
    [InlineData(float.NaN, .55f)]
    [InlineData(-1, 0)]
    [InlineData(2, 1)]
    public void PianoForceStrengthIsFiniteAndBounded(float input, float expected)
    {
        var config = new Plugin.Configuration { PianoStrength = input };
        config.Sanitize(); Assert.Equal(expected, config.PianoStrength);
    }
    // Match Dalamud's plugin-config serializer, including CLR type metadata.
    // These tests link the real configuration classes into this test assembly;
    // they exercise persistence without loading the plugin or touching a game.
    private static readonly JsonSerializerSettings DalamudSettings = new()
    {
        TypeNameAssemblyFormatHandling = TypeNameAssemblyFormatHandling.Simple,
        TypeNameHandling = TypeNameHandling.Objects,
    };

    [Fact]
    public void FreshConfigurationUsesNativeCompositionAndTinyDragDefaults()
    {
        var config = new Plugin.Configuration();

        AssertDefaults(config);
        config.Sanitize();
        AssertDefaults(config);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"Version\":1,\"Enabled\":true}")]
    public void MissingPropertiesUseNewDefaults(string saved)
    {
        var config = Read(saved);

        AssertDefaults(config);
        config.Sanitize();
        AssertDefaults(config);
    }

    [Fact]
    public void MissingNativePropertyDoesNotResetExistingFootprintOrDragSettings()
    {
        var config = Read("""
            {"Version":1,"Radius":6,"HalfWidth":5,"HalfLength":4,
             "DragRadius":1.5,"DragResponse":0.3}
            """);
        config.Sanitize();

        Assert.True(config.NativeUiComposition);
        Assert.Equal(6, config.Radius);
        Assert.Equal(5, config.HalfWidth);
        Assert.Equal(4, config.HalfLength);
        Assert.Equal(1.5f, config.DragRadius);
        Assert.Equal(0.3f, config.DragResponse);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExplicitSavedNativeSettingAndTinyDragSurviveSanitize(bool native)
    {
        var saved = new JObject
        {
            ["Version"] = 1,
            ["NativeUiComposition"] = native,
            ["DragRadius"] = 0.2f,
            ["DragResponse"] = 0.08f,
        };
        var config = Read(saved.ToString());
        config.Sanitize();
        config.Sanitize();

        Assert.Equal(native, config.NativeUiComposition);
        Assert.Equal(0.2f, config.DragRadius);
        Assert.Equal(0.08f, config.DragResponse);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void RoundTripPreservesExplicitNativeSettingForBothConfigurationTypes(bool native, bool legacy)
    {
        Plugin.Configuration original = legacy ? new XivFloorMap.Plugin.Configuration() : new Plugin.Configuration();
        original.NativeUiComposition = native;
        original.DragRadius = 0.2f;
        original.DragResponse = 0.08f;
        original.Sanitize();

        var saved = JsonConvert.SerializeObject(original, DalamudSettings);
        var payload = JObject.Parse(saved);
        Assert.Equal(native, payload.Value<bool>("NativeUiComposition"));
        Assert.NotNull(payload["$type"]);
        var loaded = Read(saved);
        loaded.Sanitize();

        Assert.Equal(original.GetType(), loaded.GetType());
        Assert.Equal(native, loaded.NativeUiComposition);
        Assert.Equal(0.2f, loaded.DragRadius);
        Assert.Equal(0.08f, loaded.DragResponse);
        Assert.Equal(2, loaded.Radius);
        Assert.Equal(2, loaded.HalfWidth);
        Assert.Equal(1.5f, loaded.HalfLength);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void NonFiniteSizesAndDragUseCurrentFallbacksWithoutChangingNativeOptOut(float invalid)
    {
        var config = new Plugin.Configuration
        {
            Radius = invalid,
            HalfWidth = invalid,
            HalfLength = invalid,
            DragRadius = invalid,
            DragResponse = invalid,
            NativeUiComposition = false,
        };
        config.Sanitize();

        Assert.False(config.NativeUiComposition);
        Assert.Equal(2, config.Radius);
        Assert.Equal(2, config.HalfWidth);
        Assert.Equal(1.5f, config.HalfLength);
        Assert.Equal(0.2f, config.DragRadius);
        Assert.Equal(0.55f, config.DragResponse);
    }

    [Theory]
    [InlineData(-5, -3, 2, 0, 0.05f)]
    [InlineData(99, 99, 2, 1.6f, 2)]
    [InlineData(99, 99, 30, 8, 2)]
    public void SanitizeClampsDragToFootprintAndResponseBounds(
        float radius, float response, float rugRadius, float expectedRadius, float expectedResponse)
    {
        var config = new Plugin.Configuration
        {
            Radius = rugRadius,
            DragRadius = radius,
            DragResponse = response,
        };
        config.Sanitize();

        Assert.Equal(expectedRadius, config.DragRadius);
        Assert.Equal(expectedResponse, config.DragResponse);
        Assert.True(config.NativeUiComposition);
    }

    private static Plugin.Configuration Read(string saved) =>
        Assert.IsAssignableFrom<Plugin.Configuration>(
            JsonConvert.DeserializeObject<Plugin.Configuration>(saved, DalamudSettings));

    [Theory]
    [InlineData(float.NaN, 0)]
    [InlineData(float.PositiveInfinity, 0)]
    [InlineData(-1, 0)]
    [InlineData(2, 1)]
    [InlineData(.65f, .65f)]
    public void WearIsOptionalBoundedAndSurvivesRoundTrip(float value, float expected)
    {
        var config = new Plugin.Configuration { RugWear = value };
        config.Sanitize();
        Assert.Equal(expected, config.RugWear);
        var loaded = Read(JsonConvert.SerializeObject(config, DalamudSettings));
        loaded.Sanitize();
        Assert.Equal(expected, loaded.RugWear);
    }

    private static void AssertDefaults(Plugin.Configuration config)
    {
        Assert.True(config.NativeUiComposition);
        Assert.Equal(0, config.RugWear);
        Assert.Equal(0.2f, config.DragRadius);
        Assert.Equal(0.55f, config.DragResponse);
        Assert.Equal(FootprintShape.Circle, config.Shape);
        Assert.Equal(2, config.Radius);
        Assert.Equal(2, config.HalfWidth);
        Assert.Equal(1.5f, config.HalfLength);
    }
}
