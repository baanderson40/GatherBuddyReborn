using System;
using Dalamud.Game;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GatherBuddy.Vulcan;
using GatherBuddy.Vulcan.Adaptive;
using Recipe = Lumina.Excel.Sheets.Recipe;
using Item = Lumina.Excel.Sheets.Item;

namespace GatherBuddy.Crafting;

/// <summary>Separate strict diagnostic reader. No legacy defaults, simulated steps, or native writes.</summary>
internal static unsafe class AdaptiveShadowNativeReader
{
    internal static bool TryCraft(Recipe recipe, out AdaptiveCraftSnapshot? snapshot, out string reason)
    {
        snapshot = null;
        reason = "Live player stats/recipe unavailable.";
        var player = Dalamud.Objects.LocalPlayer;
        var playerState = PlayerState.Instance();
        var game = GameMain.Instance();
        if (!Dalamud.ClientState.IsLoggedIn || Dalamud.PlayerState.ContentId == 0 || player == null || playerState == null || game == null) return false;
        if (game->CurrentTerritoryIntendedUseId == TerritoryIntendedUse.CosmicExploration)
        {
            reason = "Cosmic/mission mechanics are outside shadow scope.";
            return false;
        }
        var job = player.ClassJob.RowId;
        if (job is < 8 or > 15 || job != recipe.CraftType.RowId + 8)
        {
            reason = "Live crafting job does not match recipe.";
            return false;
        }
        if (!TryTool(job, out var tool, out reason)) return false;
        var craftsmanship = playerState->Attributes[70];
        var control = playerState->Attributes[71];
        var cp = (int)player.MaxCp;
        if (craftsmanship <= 0 || control <= 0 || cp <= 0 || playerState->Attributes[11] != cp) return false;
        var level = recipe.RecipeLevelTable.Value;
        var craft = new CraftState
        {
            RecipeId = recipe.RowId, StatCraftsmanship = craftsmanship, StatControl = control,
            StatCP = cp, StatLevel = player.Level, CraftLevel = level.ClassJobLevel,
            CraftProgress = (int)(level.Difficulty * recipe.DifficultyFactor / 100),
            CraftQualityMax = (int)(level.Quality * recipe.QualityFactor / 100),
            CraftDurability = (int)(level.Durability * recipe.DurabilityFactor / 100),
            CraftRequiredQuality = (int)recipe.RequiredQuality,
            CraftProgressDivider = level.ProgressDivider, CraftProgressModifier = level.ProgressModifier,
            CraftQualityDivider = level.QualityDivider, CraftQualityModifier = level.QualityModifier,
            CraftExpert = recipe.IsExpert, CraftHQ = recipe.CanHq,
            CraftCollectible = recipe.ItemResult.Value.AlwaysCollectable,
            ConditionFlags = (ConditionFlags)level.ConditionsFlag,
            UnlockedManipulation = CraftingStateBuilder.GetManipulationUnlocked(job),
            SplendorCosmic = tool!.GoodQualityPercent == 175,
        };
        AdaptiveCollectabilityEvidence? collectability = null;
        if (craft.CraftCollectible)
        {
            if (!AdaptiveCollectabilityReader.TryResolve(recipe.ItemResult.RowId, craft.CraftQualityMax, out collectability, out reason)) return false;
            var tiers = collectability!.QualityThresholds(craft.CraftQualityMax);
            craft.CraftQualityMin1 = tiers.Low;
            craft.CraftQualityMin2 = tiers.Mid;
            craft.CraftQualityMin3 = tiers.High;
        }
        try { snapshot = AdaptiveCraftSnapshot.Capture(craft, tool, collectability); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        {
            reason = error.Message;
            return false;
        }
        reason = "Strict live stats captured. Shadow target is full raw quality, not selected-solver policy.";
        return true;
    }

    internal static bool TryMainHandItemId(out uint itemId)
    {
        itemId = 0;
        var manager = InventoryManager.Instance();
        if (manager == null) return false;
        var container = manager->GetInventoryContainer(InventoryType.EquippedItems);
        if (container == null || !container->IsLoaded || container->Items == null || container->Size < 1) return false;
        itemId = container->Items[0].ItemId; // Equipped main hand, never glamour/model ID.
        return itemId != 0;
    }

    private static bool TryTool(uint job, out AdaptiveToolEvidence? evidence, out string reason)
    {
        evidence = null;
        reason = "Equipped main-hand slot or English item metadata unavailable.";
        if (!TryMainHandItemId(out var itemId)) return false;
        var sheet = Dalamud.GameData.GetExcelSheet<Item>(ClientLanguage.English);
        if (sheet == null || !sheet.TryGetRow(itemId, out var item)) return false;
        var slot = item.EquipSlotCategory.Value;
        return AdaptiveToolBonus.TryResolve(itemId, job, item.ClassJobUse.RowId, slot.MainHand == 1 && slot.OffHand == 0,
            item.ItemSpecialBonus.RowId, item.ItemSpecialBonusParam, item.ItemAction.RowId,
            item.Description.ExtractText(), out evidence, out reason);
    }

    internal static bool TryFrame(out AdaptiveObservedFrame frame, out string reason)
    {
        frame = default;
        reason = "Synthesis addon/player unavailable.";
        var player = Dalamud.Objects.LocalPlayer;
        var addon = SynthesisReader.GetSynthesisAddon();
        if (player == null || addon == null || !addon->IsVisible || addon->AtkValues == null || addon->AtkValuesCount < 16) return false;
        // Existing reader offsets, deliberately type/range checked here. Native acceptance is
        // still required; an unsupported layout blocks diagnostics rather than supplying defaults.
        if (!Integer(addon->AtkValues[15], out var step) || !Integer(addon->AtkValues[5], out var progress)
            || !Integer(addon->AtkValues[9], out var quality) || !Integer(addon->AtkValues[7], out var durability)
            || !Integer(addon->AtkValues[12], out var condition))
        {
            reason = "Unsupported Synthesis AtkValue types; reader layout needs verification.";
            return false;
        }
        frame = new(step, progress, quality, durability, (int)player.CurrentCp, (Condition)condition);
        foreach (var status in player.StatusList)
        {
            frame = status.StatusId switch
            {
                251 => frame with { InnerQuiet = status.Param },
                252 or 257 => frame with { WasteNot = status.Param },
                1164 => frame with { Manipulation = status.Param },
                254 => frame with { GreatStrides = status.Param },
                2189 => frame with { Innovation = status.Param },
                2226 => frame with { Veneration = status.Param },
                2191 => frame with { MuscleMemory = status.Param },
                2190 => frame with { FinalAppraisal = status.Param },
                2665 => frame with { HeartAndSoulActive = true },
                3813 => frame with { TrainedPerfectionActive = true },
                3812 => frame with { Expedience = status.Param },
                _ => frame,
            };
        }
        reason = "Native scalars read; availability/combo are not native fields.";
        return true;
    }

    private static bool Integer(AtkValue value, out int result)
    {
        result = 0;
        if (value.Type == AtkValueType.Int) { result = value.Int; return true; }
        if (value.Type == AtkValueType.UInt && value.UInt <= int.MaxValue) { result = (int)value.UInt; return true; }
        return false;
    }
}
