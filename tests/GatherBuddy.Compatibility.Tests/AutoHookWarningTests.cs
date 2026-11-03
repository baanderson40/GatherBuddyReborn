using GatherBuddy.AutoHookIntegration.Models;
using Newtonsoft.Json;
using Xunit;

namespace GatherBuddy.Compatibility.Tests;

public sealed class AutoHookWarningTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IntuitionSwapWithoutBaitDoesNotForceBait(bool onGain)
    {
        var preset = new AHCustomPresetConfig("test");
        preset.SetIntuitionSwap(onGain, "next");
        var extra = Assert.IsType<AHExtraCfg>(preset.ExtraCfg);
        Assert.True(extra.Enabled);
        Assert.False(extra.ForceBaitSwap);
        Assert.Equal(0u, extra.ForcedBaitId);
        Assert.Equal(onGain, extra.SwapPresetIntuitionGain);
        Assert.Equal(!onGain, extra.SwapPresetIntuitionLost);
        Assert.Contains("next", JsonConvert.SerializeObject(preset));
    }

    [Fact]
    public void IntuitionSwapPreservesExistingBaitConfiguration()
    {
        var extra = new AHExtraCfg { Enabled = true, ForceBaitSwap = true, ForcedBaitId = 12704 };
        var preset = new AHCustomPresetConfig("test") { ExtraCfg = extra };
        preset.SetIntuitionSwap(true, "gain");
        preset.SetIntuitionSwap(false, "lost");
        Assert.Same(extra, preset.ExtraCfg);
        Assert.Equal(12704u, extra.ForcedBaitId);
        Assert.True(extra.ForceBaitSwap);
        Assert.Equal("gain", extra.PresetToSwapIntuitionGain);
        Assert.Equal("lost", extra.PresetToSwapIntuitionLost);
    }

    [Fact]
    public void OptionalMoochPreservesOmittedExportRatherThanInventingConfiguration()
    {
        var config = new AHFishConfig(1) { Mooch = null };
        var json = JsonConvert.SerializeObject(config, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
        Assert.DoesNotContain("\"Mooch\"", json);
        config.Mooch = new AHAutoMooch(2);
        Assert.True(config.Mooch.Enabled);
        Assert.Equal(2u, config.Mooch.Id);
    }
}
