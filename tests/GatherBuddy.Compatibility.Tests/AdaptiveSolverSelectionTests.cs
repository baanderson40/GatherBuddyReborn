using GatherBuddy.Crafting;
using GatherBuddy.Adaptive.Tests;
using GatherBuddy.Vulcan.Adaptive;
using System.Reflection;
using Newtonsoft.Json;
using Xunit;

namespace GatherBuddy.Compatibility.Tests;

public class AdaptiveSolverSelectionTests
{
    private delegate bool Permission(VulcanSolverMode mode, bool quick, bool owns, bool macro, out string reason);
    private delegate (string? MacroId, SolverOverrideMode SolverOverride) ListSelection(
        RecipeCraftSettings? settings, bool precraft, CraftingListDefinition list);
    private static readonly Type Selection = typeof(VulcanSolverMode).Assembly.GetType("GatherBuddy.Crafting.AdaptiveSolverSelection", true)!;
    private static readonly Permission CanStart = Selection.GetMethod("CanStart", BindingFlags.NonPublic | BindingFlags.Static)!.CreateDelegate<Permission>();
    private static readonly Func<VulcanSolverMode, SolverOverrideMode, VulcanSolverMode> ResolveMode =
        Selection.GetMethod("ResolveMode", BindingFlags.NonPublic | BindingFlags.Static)!.CreateDelegate<Func<VulcanSolverMode, SolverOverrideMode, VulcanSolverMode>>();
    private static readonly ListSelection ResolveList = typeof(CraftingListQueueBuilder)
        .GetMethod("ResolveEffectiveMacroSelection", BindingFlags.NonPublic | BindingFlags.Static)!.CreateDelegate<ListSelection>();
    [Fact]
    public void SavedEnumValuesAndDefaultRemainCompatible()
    {
        Assert.Equal(0, (int)SolverOverrideMode.Default);
        Assert.Equal(1, (int)SolverOverrideMode.StandardSolver);
        Assert.Equal(2, (int)SolverOverrideMode.RaphaelSolver);
        Assert.Equal(3, (int)SolverOverrideMode.ProgressOnlySolver);
        Assert.Equal(4, (int)SolverOverrideMode.AdaptiveSolver);
        Assert.Equal(VulcanSolverMode.PureRaphael, new RaphaelSolveCoordinatorConfig().SolverMode);
        Assert.Equal(SolverOverrideMode.Default, new RecipeCraftSettings().SolverOverride);
        Assert.Equal(VulcanSolverMode.PureRaphael, JsonConvert.DeserializeObject<RaphaelSolveCoordinatorConfig>("{}")!.SolverMode);
        for (var old = 0; old <= 2; ++old)
            Assert.Equal((VulcanSolverMode)old, JsonConvert.DeserializeObject<RaphaelSolveCoordinatorConfig>($"{{\"SolverMode\":{old}}}")!.SolverMode);
        for (var old = 0; old <= 3; ++old)
            Assert.Equal((SolverOverrideMode)old, JsonConvert.DeserializeObject<RecipeCraftSettings>($"{{\"SolverOverride\":{old}}}")!.SolverOverride);
    }

    [Fact]
    public void NewSelectionRoundTripsAndAuthorizesAutomaticStartup()
    {
        var config = new RaphaelSolveCoordinatorConfig { SolverMode = VulcanSolverMode.Adaptive };
        Assert.Equal(VulcanSolverMode.Adaptive,
            JsonConvert.DeserializeObject<RaphaelSolveCoordinatorConfig>(JsonConvert.SerializeObject(config))!.SolverMode);
        var settings = new RecipeCraftSettings { SolverOverride = SolverOverrideMode.AdaptiveSolver };
        Assert.True(settings.HasAnySettings());
        Assert.Equal(SolverOverrideMode.AdaptiveSolver, settings.Clone().SolverOverride);
        var restored = JsonConvert.DeserializeObject<RecipeCraftSettings>(JsonConvert.SerializeObject(settings))!;
        Assert.Equal(SolverOverrideMode.AdaptiveSolver, restored.SolverOverride);
        Assert.True(CanStart(config.SolverMode, false, false, false, out _));
        restored.Clear();
        Assert.Equal(SolverOverrideMode.Default, restored.SolverOverride);
    }

    public static IEnumerable<object[]> Modes =>
        from global in Enum.GetValues<VulcanSolverMode>()
        from recipe in Enum.GetValues<SolverOverrideMode>()
        select new object[] { global, recipe };

    [Theory]
    [MemberData(nameof(Modes))]
    public void GlobalAndRecipeOverridesResolveWithoutChangingLegacyPrecedence(VulcanSolverMode global, SolverOverrideMode recipe)
    {
        var expected = recipe switch
        {
            SolverOverrideMode.StandardSolver => VulcanSolverMode.StandardSolver,
            SolverOverrideMode.RaphaelSolver => VulcanSolverMode.PureRaphael,
            SolverOverrideMode.ProgressOnlySolver => VulcanSolverMode.ProgressOnly,
            SolverOverrideMode.AdaptiveSolver => VulcanSolverMode.Adaptive,
            _ => global,
        };
        Assert.Equal(expected, ResolveMode(global, recipe));
    }

    public static IEnumerable<object[]> Permissions =>
        from mode in Enum.GetValues<VulcanSolverMode>()
        from quick in new[] { false, true }
        from owns in new[] { false, true }
        from macro in new[] { false, true }
        select new object[] { mode, quick, owns, macro };

    [Theory]
    [MemberData(nameof(Permissions))]
    public void SelectionAuthorizesExecutionButCannotReplaceMacroOrOwner(VulcanSolverMode mode, bool quick, bool owns, bool macro)
    {
        var expected = quick || mode != VulcanSolverMode.Adaptive || !owns && !macro;
        Assert.Equal(expected, CanStart(mode, quick, owns, macro, out var reason));
        Assert.Equal(expected, reason.Length == 0);
    }

    [Fact]
    public void AdaptiveGoalIsActiveQualityPolicyNotDiagnosticGoal()
    {
        var craft = ReferenceCorpus.Loaded.Recipes.Single(r => r.Key == "recipe-5618-level-100-durability-80").Craft with { CanHQ = true };
        var adaptive = new AdaptiveLiveGoalPolicy(AdaptiveLivePolicyMode.Adaptive, 100, 3);
        Assert.Equal(craft.MaxQuality, adaptive.Resolve(craft).DesiredQuality);
        var mandatory = adaptive.Resolve(craft with { RequiredQuality = 4000 });
        Assert.Equal(4000, mandatory.MandatoryQuality);
        Assert.Equal(4000, mandatory.DesiredQuality);
        Assert.Equal(0, adaptive.Resolve(craft with { CanHQ = false }).DesiredQuality);
        Assert.Throws<NotSupportedException>(() => adaptive.Resolve(craft with { Collectable = true }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ListDefaultsInheritAdaptiveButExplicitRecipeMacroOrSolverKeepsPrecedence(bool precraft)
    {
        var list = new CraftingListDefinition
        {
            DefaultPrecraftSolverOverride = SolverOverrideMode.AdaptiveSolver,
            DefaultFinalSolverOverride = SolverOverrideMode.AdaptiveSolver,
        };
        list = JsonConvert.DeserializeObject<CraftingListDefinition>(JsonConvert.SerializeObject(list))!;
        var inherited = ResolveList(null, precraft, list);
        Assert.Null(inherited.MacroId);
        Assert.Equal(SolverOverrideMode.AdaptiveSolver, inherited.SolverOverride);
        var legacy = ResolveList(new()
            { MacroMode = MacroOverrideMode.Specific, SolverOverride = SolverOverrideMode.StandardSolver }, precraft, list);
        Assert.Equal(SolverOverrideMode.StandardSolver, legacy.SolverOverride);
        var macro = ResolveList(new()
            { MacroMode = MacroOverrideMode.Specific, SelectedMacroId = "existing-macro" }, precraft, list);
        Assert.Equal("existing-macro", macro.MacroId);
        Assert.Equal(SolverOverrideMode.Default, macro.SolverOverride);
    }
}
