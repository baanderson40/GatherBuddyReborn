using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using GatherBuddy.Crafting;
using GatherBuddy.RebornUI.Settings;
using GatherBuddy.Vulcan.Adaptive;
using RebornMaterial;
using System;
using System.Numerics;

namespace GatherBuddy.Gui;

public partial class VulcanWindow
{
    private void DrawRaphaelSolverPage()
    {
        using (M3Card.Begin("raphael_solver_settings", "Raphael solver", FontAwesomeIcon.Brain, M3.AccentFor(3)))
        {
            var coordinator = GatherBuddy.RaphaelSolveCoordinator;
            var config = GatherBuddy.Config.RaphaelSolverConfig;

            SettingRows.Number("Max concurrent solves", "How many Raphael solves can run at the same time.",
                config.MaxConcurrentRaphaelProcesses, 1, 1000, v => config.MaxConcurrentRaphaelProcesses = v);
            SettingRows.Number("Solve timeout",
                "Minutes per Raphael solve. A solve that takes longer is marked as failed and the craft is skipped.",
                config.RaphaelTimeoutMinutes, 1, 60, v => config.RaphaelTimeoutMinutes = v, 1, "min");
            SettingRows.Number("Cache max age", "Solutions older than this are discarded when the plugin loads.",
                config.SolutionCacheMaxAgeDays, 1, 365, v => config.SolutionCacheMaxAgeDays = v, 1, "days");

            if (SettingRows.Switch("Backload progress",
                    "Only use progress actions at the end of the rotation. May lower the quality you can reach; turn it off for the most quality. "
                  + "Changing this clears the solution cache.",
                    config.RaphaelBackloadProgress, v => config.RaphaelBackloadProgress = v))
            {
                coordinator.Clear();
            }

            if (SettingRows.Switch("Allow specialist actions",
                    "When off (the default), Raphael makes non-specialist rotations even if you are a specialist. "
                  + "Changing this clears the solution cache.",
                    config.RaphaelAllowSpecialistActions, v => config.RaphaelAllowSpecialistActions = v))
            {
                coordinator.Clear();
            }

            M3Widgets.SectionLabel("Status");
            var muted = M3.Scheme.OnSurfaceVariant;
            M3Widgets.RowValue("Active solves", $"{coordinator.ActiveSolves} / {config.MaxConcurrentRaphaelProcesses}", null,
                coordinator.ActiveSolves > 0 ? M3.Severity(M3Severity.Success) : muted);
            M3Widgets.RowValue("Pending solves", coordinator.PendingSolves.ToString(), null,
                coordinator.PendingSolves > 0 ? M3.Severity(M3Severity.Warning) : muted);
            M3Widgets.RowValue("Cached solutions", coordinator.CachedSolutionCount.ToString(), null,
                coordinator.CachedSolutionCount > 0 ? M3.Severity(M3Severity.Success) : muted);

            if (M3Widgets.RowButton("Clear the solution cache", "Clear cache", "Deletes every saved Raphael solution.", M3ButtonStyle.Outlined,
                    FontAwesomeIcon.Trash))
            {
                coordinator.Clear();
                M3Snackbar.Show("Cleared the Raphael solution cache.");
            }
        }

        ImGui.Dummy(new Vector2(0, M3.Space2));
        M3Widgets.SectionLabel("Saved solutions");
        DrawSolutionsPage();
    }

    private static void DrawAdaptiveSolverPage()
    {
        using (M3Card.Begin("adaptive_solver_settings", "Adaptive (Experimental)", FontAwesomeIcon.ExclamationTriangle,
                   M3.AccentFor(4), M3CardStyle.Filled))
        {
            var config = GatherBuddy.Config.RaphaelSolverConfig;
            var target = Math.Clamp(config.AdaptiveCollectabilityTier, 1, 3);
            var targetIndex = target - 1;
            var targetLabels = new[] { "Tier 1 (minimum)", "Tier 2", "Tier 3 (highest)" };
            var targetWidth = MathF.Max(M3Widgets.ComboWidthFor(targetLabels[2]), 180f * M3.Scale);
            var row = M3SettingRow.Begin("Collectable target",
                "Desired Adaptive reward tier. Tier 1 is the minimum acceptable quality; higher tiers are not guaranteed.",
                new Vector2(targetWidth, M3Widgets.ComboHeight));
            ImGui.SetCursorScreenPos(row.ControlPosition);
            var changed = M3Widgets.Combo("##adaptive_collectability_target", ref targetIndex, targetLabels.Length,
                i => targetLabels[i], targetWidth);
            M3SettingRow.End(row);
            if (changed && targetIndex >= 0 && targetIndex < targetLabels.Length && targetIndex + 1 != target)
            {
                config.AdaptiveCollectabilityTier = targetIndex + 1;
                GatherBuddy.Config.Save();
                CraftingGameInterop.ReloadSolvers();
            }

            if (!ImGui.TreeNode("Experimental / Diagnostics##AdaptiveSettingsDiagnostics"))
            {
                return;
            }

            ImGui.TextWrapped("WARNING: Selecting Adaptive authorizes automated game actions, including consecutive queue crafts. Native hook/layout remains experimental and patch-sensitive; broader unattended acceptance is incomplete.");
            ImGui.TextWrapped("Capture and seed preparation run automatically for each Adaptive craft. Collectables use local reward-tier data; missing or ambiguous data blocks synthesis. Selected or associated macros are not supported. No retry or legacy fallback after uncertainty.");
            ImGui.TextWrapped(AdaptiveLiveDiagnostics.Status);
            var seed = AdaptiveLiveDiagnostics.SeedStatus;
            ImGui.TextWrapped($"Seed-first startup: {seed.Phase} — {seed.Reason}");
            if (ImGui.Button("Copy Adaptive trace##AdaptiveSettingsExport"))
            {
                ImGui.SetClipboardText(AdaptiveLiveDiagnostics.Export());
            }

            ImGui.TreePop();
        }
    }
}
