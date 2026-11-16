using System.Text.Json;
using GatherBuddy.Vulcan;
using GatherBuddy.Vulcan.Adaptive;

namespace GatherBuddy.Adaptive.Tests;

// Shared test data reader, also compiled by actual-plugin compatibility tests.
public static class ReferenceCorpus
{
    public sealed record Recipe(string Key, uint ItemId, int RecipeLevelRow, AdaptiveCraftSnapshot Craft,
        int BaseProgress, int BaseQuality, int InitialQuality, int TargetQuality,
        string RaphaelStatus, VulcanSkill[] RaphaelActions, int? RaphaelQuality, int? RaphaelDuration);
    public sealed record Transition(string RecipeKey, AdaptiveSearchState Before, VulcanSkill Action,
        bool Legal, AdaptiveSearchState? After, bool Terminal, int Duration);
    public sealed record Data(JsonElement Header, Recipe[] Recipes, Transition[] Transitions);
    public static Data Loaded { get; } = Load();

    // Frozen Rust marks standalone Refined Touch illegal and has no After state.
    // Native user verification confirms it is legal, without the extra combo IQ.
    // Keep raw data intact; these rows require spec tests, not invented Rust parity.
    public static bool IsStandaloneRefinedTouchErratum(Recipe recipe, Transition row)
        => row.Action == VulcanSkill.RefinedTouch && !row.Legal && recipe.Craft.Level >= 92
            && row.Before.CP >= 24 && row.Before.Durability > 0
            && row.Before.Progress < recipe.Craft.RequiredProgress && row.Before.Combo != VulcanSkill.BasicTouch;

    private static Data Load()
    {
        var recipes = new List<Recipe>();
        var transitions = new List<Transition>();
        JsonElement header = default;
        foreach (var line in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "raphael-reference.jsonl")))
        {
            using var document = JsonDocument.Parse(line);
            var row = document.RootElement;
            switch (row.GetProperty("Kind").GetString())
            {
                case "Header": header = row.Clone(); break;
                case "Recipe":
                    var macro = row.GetProperty("Raphael");
                    recipes.Add(new(row.GetProperty("Key").GetString()!, row.GetProperty("ItemId").GetUInt32(),
                        row.GetProperty("RecipeLevelRow").GetInt32(), row.GetProperty("Craft").Deserialize<AdaptiveCraftSnapshot>()!,
                        row.GetProperty("BaseProgress").GetInt32(), row.GetProperty("BaseQuality").GetInt32(),
                        row.GetProperty("InitialQuality").GetInt32(), row.GetProperty("TargetQuality").GetInt32(),
                        macro.GetProperty("Status").GetString()!,
                        macro.GetProperty("Actions").EnumerateArray().Select(a => (VulcanSkill)a.GetInt32()).ToArray(),
                        macro.TryGetProperty("FinalQuality", out var quality) ? quality.GetInt32() : null,
                        macro.TryGetProperty("Duration", out var duration) ? duration.GetInt32() : null));
                    break;
                case "Transition":
                    var after = row.GetProperty("After");
                    transitions.Add(new(row.GetProperty("RecipeKey").GetString()!, State(row.GetProperty("Before")),
                        (VulcanSkill)row.GetProperty("ActionId").GetInt32(), row.GetProperty("Legal").GetBoolean(),
                        after.ValueKind == JsonValueKind.Null ? null : State(after),
                        row.GetProperty("Terminal").GetBoolean(), row.GetProperty("Duration").GetInt32()));
                    break;
                default: throw new InvalidDataException("Unknown independent-reference row kind.");
            }
        }
        return new(header, recipes.ToArray(), transitions.ToArray());
    }

    private static AdaptiveSearchState State(JsonElement state)
    {
        if (state.GetArrayLength() != 17) throw new InvalidDataException("Reference state schema changed.");
        return new(state[0].GetBoolean(), state[1].GetInt32(), state[2].GetInt32(), state[3].GetInt32(),
            state[4].GetInt32(), (Condition)state[5].GetInt32(), state[6].GetInt32(), state[7].GetInt32(),
            state[8].GetInt32(), state[9].GetInt32(), state[10].GetInt32(), state[11].GetInt32(),
            state[12].GetInt32(), state[13].GetInt32(), state[14].GetBoolean(), state[15].GetBoolean(), (VulcanSkill)state[16].GetInt32());
    }

    public static AdaptiveSearchState Project(AdaptiveSearchState state) => state with
    {
        Durability = Math.Max(0, state.Durability),
        Combo = state.Combo switch
        {
            VulcanSkill.BasicTouch => VulcanSkill.BasicTouch,
            VulcanSkill.StandardTouch or VulcanSkill.Observe => VulcanSkill.StandardTouch,
            _ => VulcanSkill.None,
        },
    };

    public static AdaptiveSearchState Initial(Recipe recipe, int? quality = null) => new(
        true, 0, quality ?? recipe.InitialQuality, recipe.Craft.MaxDurability, recipe.Craft.MaxCP, Condition.Normal,
        TrainedPerfectionAvailable: recipe.Craft.Level >= 100);
}
