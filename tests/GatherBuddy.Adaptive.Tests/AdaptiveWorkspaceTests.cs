using GatherBuddy.Vulcan;
using GatherBuddy.Vulcan.Adaptive;
using Xunit;
using Xunit.Abstractions;

namespace GatherBuddy.Adaptive.Tests;

public class AdaptiveWorkspaceTests(ITestOutputHelper output)
{
    [Fact]
    public void ScratchTransitionsRemovePerEdgeStateAllocations()
    {
        var simulation = AdaptiveCoreTests.Simulation();
        var workspace = simulation.CreateWorkspace();
        var state = AdaptiveCoreTests.Initial();
        for (var i = 0; i < 100; ++i)
        {
            simulation.TryApply(state, VulcanSkill.BasicTouch, out _);
            workspace.TryApply(state, VulcanSkill.BasicTouch, out _);
        }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 8192; ++i) simulation.TryApply(state, VulcanSkill.BasicTouch, out _);
        var allocating = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 8192; ++i) workspace.TryApply(state, VulcanSkill.BasicTouch, out _);
        var scratch = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"8192 transitions: public={allocating} bytes; worker scratch={scratch} bytes");
        Assert.True(allocating > 0);
        // Allow incidental runtime/tiered-JIT bookkeeping, but not even one StepState per edge.
        Assert.True(scratch < allocating / 10);
    }

    [Fact]
    public void ReusedOutputResetsFailedActionFieldsAndRejectsAliasing()
    {
        var craft = SimulatorAuditTests.Craft();
        var initial = SimulatorAuditTests.Step();
        var scratch = new StepState();
        Assert.Equal(Simulator.ExecuteResult.Succeeded, Simulator.ExecuteInto(craft, initial, VulcanSkill.HastyTouch, 0, 1, scratch));
        Assert.Equal(1, scratch.ExpedienceLeft);
        var expected = Simulator.Execute(craft, initial, VulcanSkill.HastyTouch, 1, 1);
        Assert.Equal(Simulator.ExecuteResult.Failed, Simulator.ExecuteInto(craft, initial, VulcanSkill.HastyTouch, 1, 1, scratch));
        Assert.Equal(expected.Item2, scratch);
        Assert.Equal(0, scratch.ExpedienceLeft);
        Assert.Throws<ArgumentException>(() => Simulator.ExecuteInto(craft, initial, VulcanSkill.BasicTouch, 0, 1, initial));
        var rejected = Simulator.Execute(craft, initial, VulcanSkill.None, 0, 1);
        Assert.Equal(Simulator.ExecuteResult.CantUse, rejected.Item1);
        Assert.Same(initial, rejected.Item2);
    }

    [Fact]
    public async Task ConcurrentSearchesOnSharedImmutableSimulationUseIndependentWorkspaces()
    {
        var simulation = AdaptiveCoreTests.Simulation();
        var state = AdaptiveCoreTests.Initial();
        var limits = AdaptiveCoreTests.Limits() with { MaximumTransitions = 4000, MaximumStoredNodes = 2000 };
        var expected = new AdaptiveSearchEngine().Search(simulation, state, limits);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            new AdaptiveSearchEngine().Search(simulation, state, limits))));
        foreach (var actual in results)
        {
            Assert.Equal(expected.Status, actual.Status);
            Assert.Equal(expected.Plan?.Actions, actual.Plan?.Actions);
            Assert.Equal(expected.Plan?.FinalState, actual.Plan?.FinalState);
            Assert.Equal(expected.Diagnostics.Transitions, actual.Diagnostics.Transitions);
        }
    }

    [Fact]
    public void HeuristicScratchOverridesCannotLeakIntoRealTransitions()
    {
        var simulation = AdaptiveCoreTests.Simulation();
        var workspace = simulation.CreateWorkspace();
        var state = AdaptiveCoreTests.Initial() with { FirstStep = false, InnerQuiet = 10, Veneration = 3 };
        Assert.True(workspace.FinisherPotential(state) > 0);
        Assert.True(workspace.ProgressBuffPotential(state) > 0);
        var expected = simulation.TryApply(state, VulcanSkill.BasicTouch, out var expectedState);
        var actual = workspace.TryApply(state, VulcanSkill.BasicTouch, out var actualState);
        Assert.Equal(expected, actual);
        Assert.Equal(expectedState, actualState);
        Assert.Equal(0, actualState.GreatStrides);
        Assert.Equal(0, actualState.Innovation);
    }
}
