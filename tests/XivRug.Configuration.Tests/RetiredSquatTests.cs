using Newtonsoft.Json;

namespace XivRug.Configuration.Tests;

public sealed class RetiredSquatTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"SquatWhenIdle\":true}")]
    [InlineData("{\"SquatWhenIdle\":false}")]
    public void NewAndSavedConfigurationsDoNotReenableAutomaticSquatting(string json)
    {
        var config = JsonConvert.DeserializeObject<Plugin.Configuration>(json)!;
        config.Sanitize();
        Assert.False(config.SquatWhenIdle);
        var reloaded = JsonConvert.DeserializeObject<Plugin.Configuration>(JsonConvert.SerializeObject(config))!;
        reloaded.Sanitize();
        Assert.False(reloaded.SquatWhenIdle);
    }

    [Fact]
    public void RetiringSquatDoesNotDisableUnrelatedUserChoices()
    {
        var config = new Plugin.Configuration { SquatWhenIdle = true, IdleFootwork = true, RideVisual = false, JumpCatch = true };
        config.Sanitize();
        Assert.False(config.SquatWhenIdle);
        Assert.True(config.IdleFootwork); Assert.False(config.RideVisual); Assert.True(config.JumpCatch);
    }
}
