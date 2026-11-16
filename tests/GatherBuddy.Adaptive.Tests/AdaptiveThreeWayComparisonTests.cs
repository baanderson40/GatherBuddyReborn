using System.Diagnostics;
using System.Text.Json;
using GatherBuddy.Vulcan;
using GatherBuddy.Vulcan.Adaptive;
using Xunit;
using Xunit.Abstractions;

namespace GatherBuddy.Adaptive.Tests;

/// <summary>Production search sources, frozen fresh CLI macros, and deterministic Normal outcomes. No native execution.</summary>
public class AdaptiveThreeWayComparisonTests(ITestOutputHelper output)
{
    private sealed record CliRun(string Key, string[] Arguments, double ElapsedSeconds, int ExitCode,
        VulcanSkill[] Actions, int FinalQuality, int Duration, int Steps);
    private sealed record CliFixture(string Evidence, string SourceCommit, string CliSha256, CliRun[] Runs);
    private sealed record Run(string Mode, bool Completed, int Quality, int Actions, int ModeledSeconds,
        long Transitions, long NodeAdmissions, double SearchWallMilliseconds,
        int? InitialProjectionQuality, int? InitialProjectionSeconds, string[] Route);

    [Theory]
    [InlineData("recipe-34890-level-90-durability-35", 6838)]
    [InlineData("recipe-35907-level-100-durability-35", 9521)]
    [InlineData("recipe-5618-level-100-durability-80", 10053)]
    [Trait("Category", "Benchmark")]
    public void CompareUnseededFreshRaphaelAndRaphaelSeededAdaptive(string key, int expectedUnseededQuality)
    {
        var fixture = JsonSerializer.Deserialize<CliFixture>(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "raphael-three-way-macros.json")))!;
        var cli = Assert.Single(fixture.Runs, r => r.Key == key);
        var recipe = ReferenceCorpus.Loaded.Recipes.Single(r => r.Key == key);
        var craft = recipe.Craft;
        var simulation = new AdaptiveSimulation(craft, AdaptiveCraftGoal.Resolve(craft, AdaptiveGoalKind.Quality, recipe.TargetQuality));
        using var pin = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "raphael-dependency.json")));
        Assert.Equal(pin.RootElement.GetProperty("commit").GetString(), fixture.SourceCommit);
        Assert.Equal(64, fixture.CliSha256.Length);
        Assert.Equal(0, cli.ExitCode);
        Assert.True(AdaptivePlanValidator.TryValidate(simulation, ReferenceCorpus.Initial(recipe), cli.Actions, out var macro));
        Assert.Equal(Math.Min(craft.MaxQuality, cli.FinalQuality), macro!.FinalState.Quality);
        Assert.Equal(cli.Duration, macro.Duration);
        Assert.Equal(cli.Steps, macro.Actions.Count);
        AssertArguments("--recipe-id", craft.RecipeId.ToString());
        AssertArguments("--level", craft.Level.ToString());
        AssertArguments("--stats", craft.Craftsmanship.ToString(), craft.Control.ToString(), craft.MaxCP.ToString());
        AssertArguments("--initial-quality", recipe.InitialQuality.ToString());
        AssertArguments("--threads", "1");
        Assert.Contains("--manipulation", cli.Arguments);
        Assert.DoesNotContain("--heart-and-soul", cli.Arguments);
        Assert.DoesNotContain("--quick-innovation", cli.Arguments);
        Assert.DoesNotContain("--backload-progress", cli.Arguments);
        Assert.DoesNotContain("--adversarial", cli.Arguments);

        // Warm independent ordinary work; never import a result or state into the actual cases.
        var warm = ReferenceCorpus.Loaded.Recipes[0];
        new AdaptiveSearchEngine().Search(new(warm.Craft, AdaptiveCraftGoal.Resolve(warm.Craft, AdaptiveGoalKind.Quality, warm.TargetQuality)),
            ReferenceCorpus.Initial(warm), new() { TimeLimit = null, MaximumTransitions = 1000 });

        var plain = Execute("AdaptiveUnseeded");
        var raphael = Execute("FreshRaphaelMacro");
        var seeded = Execute("AdaptiveRaphaelSeeded");
        Assert.True(plain.Completed);
        Assert.True(raphael.Completed);
        Assert.True(seeded.Completed);
        Assert.Equal(expectedUnseededQuality, plain.Quality);
        Assert.Equal(Math.Min(craft.MaxQuality, cli.FinalQuality), raphael.Quality);
        Assert.Equal(cli.Steps, raphael.Actions);
        Assert.Equal(cli.Duration, raphael.ModeledSeconds);
        // All-Normal outcomes are deterministic and every own continuation is replayed. The
        // seeded lane cannot lose the replay-validated macro's quality under this comparator.
        Assert.True(seeded.Quality >= raphael.Quality);
        output.WriteLine(JsonSerializer.Serialize(new
        {
            Key = key, craft.Craftsmanship, craft.Control, craft.MaxCP, craft.Level,
            recipe.InitialQuality, recipe.TargetQuality, Conditions = "Normal",
            CliSolveSeconds = cli.ElapsedSeconds,
            CliUncappedQuality = cli.FinalQuality,
            AdaptiveLimits = new AdaptiveSearchLimits { TimeLimit = null },
            Runs = new[] { plain, raphael, seeded },
        }));

        void AssertArguments(string flag, params string[] values)
        {
            var index = Array.IndexOf(cli.Arguments, flag);
            Assert.True(index >= 0);
            Assert.Equal(values, cli.Arguments.Skip(index + 1).Take(values.Length));
        }

        Run Execute(string mode)
        {
            var state = ReferenceCorpus.Initial(recipe);
            IReadOnlyList<VulcanSkill>? continuation = null;
            var route = new List<VulcanSkill>();
            var duration = 0;
            long transitions = 0, nodes = 0;
            var searchWall = TimeSpan.Zero;
            int? initialQuality = null, initialDuration = null;
            var provenance = new AdaptiveSeedProvenance("FreshRaphaelCli", key, craft.RecipeId, craft.Level,
                craft.Craftsmanship, craft.Control, craft.MaxCP, craft.ManipulationUnlocked, false, recipe.InitialQuality,
                $"Pinned {fixture.SourceCommit}; all-Normal fixture; CLI sha256 {fixture.CliSha256}");
            var seed = new AdaptiveSeedCandidate(new(provenance, cli.Actions), 0, 0);
            for (var turn = 0; turn < 128; ++turn)
            {
                VulcanSkill action;
                if (mode == "FreshRaphaelMacro")
                {
                    Assert.True(turn < cli.Actions.Length);
                    action = cli.Actions[turn];
                }
                else
                {
                    var start = Stopwatch.GetTimestamp();
                    var result = new AdaptiveSearchEngine().Search(simulation, state, new() { TimeLimit = null }, continuation,
                        seeds: mode == "AdaptiveRaphaelSeeded" && turn == 0 ? new[] { seed } : null);
                    searchWall += Stopwatch.GetElapsedTime(start);
                    transitions += result.Diagnostics.Transitions;
                    nodes += result.Diagnostics.StoredNodes;
                    Assert.Equal(AdaptiveSearchStatus.Found, result.Status);
                    Assert.NotNull(result.Plan);
                    Assert.True(AdaptivePlanValidator.TryValidate(simulation, state, result.Plan.Actions, out var replay));
                    Assert.Equal(result.Plan.FinalState, replay!.FinalState);
                    Assert.InRange(result.Diagnostics.Transitions, 0, 40000);
                    Assert.InRange(result.Diagnostics.StoredNodes, 0, 20000);
                    if (turn == 0)
                    {
                        initialQuality = result.Plan.FinalState.Quality;
                        initialDuration = result.Plan.Duration;
                        if (mode == "AdaptiveRaphaelSeeded")
                        {
                            Assert.Equal("Validated", Assert.Single(result.Diagnostics.Seeding!.Candidates).Status);
                            Assert.False(AdaptivePlanValidator.IsBetter(macro, result.Plan));
                        }
                    }
                    else
                    {
                        Assert.True(AdaptivePlanValidator.TryValidate(simulation, state, continuation!, out var baseline));
                        Assert.False(AdaptivePlanValidator.IsBetter(baseline!, result.Plan));
                    }
                    action = result.Plan.Actions[0];
                    continuation = result.Plan.Actions.Skip(1).ToArray();
                }
                route.Add(action);
                duration += AdaptiveSimulation.ActionDuration(action);
                var outcome = simulation.TryApply(state, action, out state);
                Assert.True(outcome is AdaptiveOutcome.Nonterminal or AdaptiveOutcome.Success);
                Assert.Equal(Condition.Normal, state.Condition);
                if (outcome == AdaptiveOutcome.Success)
                    return new(mode, true, state.Quality, route.Count, duration, transitions, nodes,
                        searchWall.TotalMilliseconds, initialQuality, initialDuration, route.Select(a => a.ToString()).ToArray());
            }
            return new(mode, false, state.Quality, route.Count, duration, transitions, nodes,
                searchWall.TotalMilliseconds, initialQuality, initialDuration, route.Select(a => a.ToString()).ToArray());
        }
    }
}
