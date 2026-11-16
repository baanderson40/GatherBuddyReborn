using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace GatherBuddy.AutoHookIntegration.Models;

public class AHCustomPresetConfig
{
    [JsonProperty("UniqueId")]
    public Guid UniqueId { get; set; } = Guid.NewGuid();

    [JsonProperty("PresetName")]
    public string PresetName { get; set; } = string.Empty;

    [JsonProperty("ListOfBaits")]
    public List<AHHookConfig> ListOfBaits { get; set; } = [];

    [JsonProperty("ListOfMooch")]
    public List<AHHookConfig> ListOfMooch { get; set; } = [];

    [JsonProperty("ListOfFish")]
    public List<AHFishConfig> ListOfFish { get; set; } = [];

    [JsonProperty("AutoCastsCfg")]
    public AHAutoCastsConfig? AutoCastsCfg { get; set; }

    [JsonProperty("ExtraCfg")]
    public AHExtraCfg? ExtraCfg { get; set; }

    public AHCustomPresetConfig(string name)
    {
        PresetName = name;
    }

    public void SetIntuitionSwap(bool onGain, string presetName)
    {
        ExtraCfg ??= new AHExtraCfg();
        ExtraCfg.Enabled = true;
        if (onGain)
        {
            ExtraCfg.SwapPresetIntuitionGain = true;
            ExtraCfg.PresetToSwapIntuitionGain = presetName;
        }
        else
        {
            ExtraCfg.SwapPresetIntuitionLost = true;
            ExtraCfg.PresetToSwapIntuitionLost = presetName;
        }
    }
}
