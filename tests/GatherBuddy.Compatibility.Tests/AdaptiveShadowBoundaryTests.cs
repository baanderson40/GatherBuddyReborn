using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.Emit;
using System.Reflection.PortableExecutable;
using GatherBuddy.Crafting;
using GatherBuddy.Vulcan;
using GatherBuddy.Vulcan.Adaptive;
using Xunit;

namespace GatherBuddy.Compatibility.Tests;

/// <summary>Inspect compiled plugin boundaries without loading native game services.</summary>
public class AdaptiveShadowBoundaryTests
{
    private sealed class AssemblyView : IDisposable
    {
        private readonly FileStream _file = File.OpenRead(typeof(AdaptiveShadowSession).Assembly.Location);
        internal PEReader Pe { get; }
        internal MetadataReader Metadata { get; }
        internal AssemblyView() { Pe = new(_file); Metadata = Pe.GetMetadataReader(); }
        internal TypeDefinition Type(string name) => Metadata.GetTypeDefinition(Assert.Single(Metadata.TypeDefinitions,
            handle => Metadata.GetString(Metadata.GetTypeDefinition(handle).Name) == name
                && Metadata.GetTypeDefinition(handle).GetDeclaringType().IsNil));
        internal string[] Calls(string type, string method) => TeleportNativeBoundaryTests.Calls(Pe, Metadata,
            Assert.Single(Type(type).GetMethods(), handle => Metadata.GetString(Metadata.GetMethodDefinition(handle).Name) == method)).ToArray();
        internal IEnumerable<string> AllCalls(string type) => Type(type).GetMethods()
            .SelectMany(handle => TeleportNativeBoundaryTests.Calls(Pe, Metadata, handle));
        internal IEnumerable<string> Fields(string type, string method, bool writesOnly = false)
        {
            var opcodes = typeof(OpCodes).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)!)
                .ToDictionary(c => unchecked((ushort)c.Value));
            var handle = Assert.Single(Type(type).GetMethods(), h => Metadata.GetString(Metadata.GetMethodDefinition(h).Name) == method);
            var il = Pe.GetMethodBody(Metadata.GetMethodDefinition(handle).RelativeVirtualAddress).GetILReader();
            while (il.RemainingBytes > 0)
            {
                var first = il.ReadByte();
                var opcode = opcodes[first == 0xfe ? (ushort)(0xfe00 | il.ReadByte()) : first];
                if (opcode.OperandType == OperandType.InlineField)
                {
                    var token = MetadataTokens.EntityHandle(il.ReadInt32());
                    if (writesOnly && opcode != OpCodes.Stfld && opcode != OpCodes.Stsfld) continue;
                    yield return token.Kind == HandleKind.MemberReference
                        ? Metadata.GetString(Metadata.GetMemberReference((MemberReferenceHandle)token).Name)
                        : Metadata.GetString(Metadata.GetFieldDefinition((FieldDefinitionHandle)token).Name);
                    continue;
                }
                var size = opcode.OperandType switch
                {
                    OperandType.InlineNone => 0,
                    OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                    OperandType.InlineVar => 2,
                    OperandType.InlineI8 or OperandType.InlineR => 8,
                    OperandType.InlineSwitch => il.ReadInt32() * 4,
                    _ => 4,
                };
                il.Offset += size;
            }
        }
        public void Dispose() { Pe.Dispose(); _file.Dispose(); }
    }

    [Theory]
    [InlineData("AdaptiveShadowSession")]
    [InlineData("AdaptiveShadowDiagnostics")]
    [InlineData("AdaptiveShadowNativeReader")]
    [InlineData("AdaptiveShadowExport")]
    [InlineData("AdaptiveShadowView")]
    [InlineData("AdaptiveShadowAnalysisRunner")]
    [InlineData("AdaptiveToolBonus")]
    [InlineData("AdaptiveCollectabilityReader")]
    [InlineData("AdaptiveSeedTracker")]
    [InlineData("RaphaelAdaptiveSeedCache")]
    [InlineData("AdaptiveNativeCraftCapture")]
    [InlineData("AdaptiveNativeCraftEvent")]
    public void ShadowTypesNeverCallExecutionLeasesActionsOrSolverRegistration(string type)
    {
        using var assembly = new AssemblyView();
        var calls = assembly.AllCalls(type).ToArray();
        foreach (var forbidden in new[] { "TryCreateLease", "TryConsumeLease", "ReportExecution", "UseAction",
                     "TryExecuteActionAsync", "CanExecuteAction", "OnCraftStarted", "Save", "SaveIfDirty" })
            Assert.DoesNotContain(forbidden, calls);
    }

    [Fact]
    public void SeedLookupOnlyReadsPublishedCacheAndCannotStartSolveOrAdvanceMacro()
    {
        using var assembly = new AssemblyView();
        var coordinator = assembly.Calls("RaphaelSolveCoordinator", "TryGetAdaptiveSeed");
        Assert.Equal(new[] { "GetKey", "TryGetValue", "TryGet" }, coordinator);
        var lookup = assembly.Calls("RaphaelAdaptiveSeedCache", "TryGet");
        Assert.DoesNotContain("get_ActionIds", lookup);
        Assert.DoesNotContain("Publish", lookup);
        var begin = assembly.Calls("AdaptiveShadowDiagnostics", "BeginCraft");
        Assert.Contains("TryGetAdaptiveSeed", begin);
        foreach (var forbidden in new[] { "EnqueueOrPromoteRequest", "ExecuteRaphaelSolve", "ReenqueueIfMissing",
                     "Save", "Start", "UseAction", "TryExecuteActionAsync", "MoveNext" })
        {
            Assert.DoesNotContain(forbidden, coordinator);
            Assert.DoesNotContain(forbidden, lookup);
            Assert.DoesNotContain(forbidden, begin);
        }
    }

    [Fact]
    public void ExperimentalNativeCaptureIsOptInBoundedAndNeverRunsModelOrExecutesActions()
    {
        using var assembly = new AssemblyView();
        var calls = assembly.AllCalls("AdaptiveNativeCraftCapture").ToArray();
        Assert.Contains("HookFromSignature", calls);
        Assert.Contains("GetEventHandlerById", calls);
        Assert.Contains("TryParseAdvance", calls);
        foreach (var forbidden in new[] { "UseAction", "TryExecuteActionAsync", "TryApply", "Execute", "ReportExecution", "TryConsumeLease", "RunOnFrameworkThread" })
            Assert.DoesNotContain(forbidden, calls);
        Assert.Contains("IsInFrameworkUpdateThread", calls.Select(name => name.StartsWith("get_") ? name[4..] : name));
        Assert.Contains("DrainNative", assembly.Calls("AdaptiveShadowDiagnostics", "Tick"));
        var end = assembly.Calls("AdaptiveShadowDiagnostics", "EndCraft");
        Assert.True(Array.IndexOf(end, "DrainNative") < Array.IndexOf(end, "End"));
        Assert.DoesNotContain("TryEnable", assembly.Calls("AdaptiveShadowDiagnostics", "BeginCraft"));
        Assert.Contains("RequestNativeCapture", assembly.Calls("VulcanWindow", "DrawAdaptiveShadowDebug"));
    }

    [Fact]
    public void ActualNativeCaptureBufferIsBoundedAndOverflowCannotBeHiddenByDraining()
    {
        var buffer = new AdaptiveNativeEvidenceBuffer();
        for (var i = 0; i < 65; ++i)
            buffer.Add(new(1, i + 1, 9, 5618, 0));
        Assert.False(buffer.ResetForFreshStream());
        var events = buffer.Drain(out var lost);
        Assert.Equal(64, events.Length);
        Assert.True(lost);
        Assert.Empty(buffer.Drain(out lost));
        Assert.True(lost);
        Assert.True(buffer.ResetForFreshStream());
        Assert.Empty(buffer.Drain(out lost));
        Assert.False(lost);
    }

    [Fact]
    public void StrictReaderDoesNotUseLegacyStatsStepsOrSimulationFallbacks()
    {
        using var assembly = new AssemblyView();
        var calls = assembly.AllCalls("AdaptiveShadowNativeReader").ToArray();
        foreach (var forbidden in new[] { "GetCurrentPlayerStats", "BuildStepState", "BuildInitialStepState", "Execute", "TryApply" })
            Assert.DoesNotContain(forbidden, calls);
        Assert.Contains("get_CurrentCp", calls);
        Assert.Contains("get_StatusList", calls);
        Assert.Contains("get_IsLoggedIn", calls);
    }

    [Fact]
    public void UiDispatchesOptInAndReadsOnlyPublishedViewAndManagedExport()
    {
        using var assembly = new AssemblyView();
        Assert.Contains("RunOnFrameworkThread", assembly.Calls("AdaptiveShadowDiagnostics", "RequestEnabled"));
        var calls = assembly.Calls("VulcanWindow", "DrawAdaptiveShadowDebug");
        Assert.Contains("RequestEnabled", calls);
        Assert.Contains("get_View", calls);
        Assert.DoesNotContain("SetEnabled", calls);
        Assert.DoesNotContain("TryFrame", calls);
        Assert.DoesNotContain("Observe", calls);
        var export = assembly.Calls("AdaptiveShadowDiagnostics", "ExportTrace");
        Assert.DoesNotContain("get_Events", export);
        Assert.DoesNotContain("get_LocalPlayer", export);
        Assert.Contains("Serialize", export);
        Assert.Contains("CaptureTrace", assembly.Calls("AdaptiveShadowDiagnostics", "RefreshView"));
        Assert.Contains("ToArray", assembly.Calls("AdaptiveShadowSession", "CaptureTrace"));
        var serializer = assembly.Calls("AdaptiveShadowExport", "Serialize");
        Assert.DoesNotContain("get_Events", serializer);
        Assert.DoesNotContain("get_LocalPlayer", serializer);
    }

    [Fact]
    public void FacadeEndsSessionBeforeCapturingFinalSummaryAndPublishesThroughCountPreservingFactory()
    {
        using var assembly = new AssemblyView();
        var calls = assembly.Calls("AdaptiveShadowDiagnostics", "EndCraft");
        Assert.True(Array.IndexOf(calls, "End") >= 0);
        Assert.True(Array.IndexOf(calls, "End") < Array.IndexOf(calls, "RefreshView"));
        Assert.Contains("FromTrace", assembly.Calls("AdaptiveShadowDiagnostics", "RefreshView"));
        Assert.Contains("get_CorrelatedActions", assembly.Calls("AdaptiveShadowView", "FromTrace"));
    }

    [Fact]
    public void FrameworkLifecycleObservesAndChecksLogout()
    {
        using var assembly = new AssemblyView();
        Assert.Contains("Tick", assembly.Calls("GatherBuddy", "Update"));
        var tick = assembly.Calls("AdaptiveShadowDiagnostics", "Tick");
        Assert.Contains("SetEnabled", tick);
        Assert.Contains("get_IsLoggedIn", tick);
        Assert.Contains("Observe", tick);
        Assert.Contains("Shutdown", assembly.Calls("CraftingGameInterop", "Dispose"));
        Assert.Contains("EndCraft", assembly.Calls("CraftingGameInterop", "CancelCurrentCraft"));
        Assert.Contains("EndCraft", assembly.Calls("CraftingGameInterop", "Finish"));
    }

    [Fact]
    public void ShadowOptInHasNoPersistedConfigurationProperty()
    {
        using var assembly = new AssemblyView();
        foreach (var handle in assembly.Metadata.TypeDefinitions)
        {
            var type = assembly.Metadata.GetTypeDefinition(handle);
            if (!assembly.Metadata.GetString(type.Name).Contains("Config", StringComparison.Ordinal)) continue;
            foreach (var property in type.GetProperties())
                Assert.DoesNotContain("AdaptiveShadow", assembly.Metadata.GetString(assembly.Metadata.GetPropertyDefinition(property).Name));
        }
        Assert.DoesNotContain("SetEnabled", assembly.Calls("AdaptiveShadowDiagnostics", ".cctor"));
    }

    [Fact]
    public void ActualPluginFinishedExportRetainsCountAndExplicitUnobservedOutcome()
    {
        var craft = TestCraft();
        var initial = new AdaptiveObservedFrame(1, 0, 0, 80, 600, Condition.Normal);
        using var shadow = new AdaptiveShadowSession(craft, initial, TimeSpan.Zero, scheduler: new UnfinishedScheduler());
        var ticket = shadow.RecordRequest(initial, VulcanSkill.GreatStrides, TimeSpan.Zero);
        shadow.ReportRequest(ticket, true);
        var after = initial with { StepIndex = 2, CP = 568, GreatStrides = 3 };
        shadow.Observe(1, TimeSpan.FromMilliseconds(10), after, false);
        shadow.Observe(2, TimeSpan.FromMilliseconds(20), after, false);
        shadow.End("Managed craft finished.", TimeSpan.FromSeconds(1), managedCraftEnded: true);
        var view = AdaptiveShadowView.FromTrace(shadow.Status, shadow.CaptureTrace());
        Assert.Empty(shadow.HistoricalResults);
        using var json = System.Text.Json.JsonDocument.Parse(AdaptiveShadowExport.Serialize(view));
        Assert.Equal(1, json.RootElement.GetProperty("CorrelatedActions").GetInt32());
        var summary = json.RootElement.GetProperty("Data").GetProperty("Summary");
        Assert.True(summary.GetProperty("ManagedCraftEnded").GetBoolean());
        Assert.Equal("Unobserved", summary.GetProperty("TerminalObservation").GetString());
        Assert.Equal("GreatStrides", json.RootElement.GetProperty("Data").GetProperty("Events").EnumerateArray()
            .First(e => e.GetProperty("Kind").GetString() == "Request").GetProperty("RequestedActionName").GetString());
    }

    [Fact]
    public void HistoricalShadowContractsCannotPublishCurrentReadySnapshots()
    {
        foreach (var type in new[] { typeof(AdaptiveShadowView), typeof(AdaptiveShadowEvent), typeof(AdaptiveHistoricalResult) })
        {
            Assert.DoesNotContain(type.GetProperties(), p => p.PropertyType == typeof(AdaptiveDecisionSnapshot));
            Assert.DoesNotContain(type.GetProperties(), p => p.Name is "Action" or "Phase" or "Recommendation");
        }
        Assert.DoesNotContain(typeof(AdaptiveShadowSession).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic),
            f => f.FieldType == typeof(AdaptiveSolveCoordinator));
        using var assembly = new AssemblyView();
        Assert.Contains("Submit", assembly.Calls("AdaptiveShadowSession", "Observe"));
        Assert.DoesNotContain("Revoke", assembly.Calls("AdaptiveShadowSession", "RecordRequest"));
        Assert.Contains("RecordAttempt", assembly.Calls("AdaptiveShadowSession", "RecordRequest"));
    }

    [Fact]
    public void StrictToolCaptureUsesLoadedEquipmentAndEnglishEffectMetadataNotNamesOrLegacyDefaults()
    {
        using var assembly = new AssemblyView();
        var reader = assembly.Calls("AdaptiveShadowNativeReader", "TryTool");
        Assert.Contains("TryMainHandItemId", reader);
        Assert.Contains("GetExcelSheet", reader);
        Assert.Contains("ExtractText", reader);
        Assert.Contains("TryResolve", reader);
        Assert.DoesNotContain("get_Name", reader);
        Assert.DoesNotContain("GetSplendorCosmic", reader);
        Assert.Contains("IsLoaded", assembly.Fields("AdaptiveShadowNativeReader", "TryMainHandItemId"));
        Assert.Contains("ItemId", assembly.Fields("AdaptiveShadowNativeReader", "TryMainHandItemId"));
        Assert.Contains("CheckToolContext", assembly.Calls("AdaptiveShadowDiagnostics", "Tick"));
        Assert.Contains("CheckToolContext", assembly.Calls("AdaptiveShadowDiagnostics", "RecordRequest"));
        Assert.Contains("Revoke", assembly.Calls("AdaptiveShadowSession", "Fail"));
    }

    [Theory]
    [InlineData(false, 9045)]
    [InlineData(true, 9337)]
    public void ActualPluginToolBonusMatchesSuppliedGoodQualityTransition(bool enhanced, int quality)
    {
        var snapshot = TestCraft() with { Craftsmanship = 5953, Control = 5490, RecipeLevel = 100,
            RequiredProgress = 6600, MaxQuality = 12000, ProgressDivider = 170, ProgressModifier = 90,
            QualityDivider = 150, QualityModifier = 75, GoodQualityPercent = enhanced ? 175 : 150 };
        var state = new AdaptiveSearchState(false, 6209, 7290, 70, 320, Condition.Good,
            InnerQuiet: 3, WasteNot: 3, Manipulation: 1, Innovation: 3, TrainedPerfectionAvailable: true);
        var simulation = new AdaptiveSimulation(snapshot, AdaptiveCraftGoal.Resolve(snapshot, AdaptiveGoalKind.Quality, 12000));
        Assert.Equal(AdaptiveOutcome.Nonterminal, simulation.TryApply(state, VulcanSkill.PreparatoryTouch, out var next));
        Assert.Equal(quality, next.Quality);
        Assert.Equal(65, next.Durability);
        Assert.Equal(280, next.CP);
    }

    [Fact]
    public void ActualPluginHistoricalPlanStaysNonExecutableAfterExistingSolverRequest()
    {
        var initial = new AdaptiveObservedFrame(1, 0, 0, 80, 600, Condition.Normal);
        using var shadow = new AdaptiveShadowSession(TestCraft(), initial, TimeSpan.Zero,
            new() { MaximumTransitions = 2000, TimeLimit = null }, new ImmediateScheduler());
        shadow.Observe(1, TimeSpan.FromMilliseconds(10), initial, false);
        shadow.Observe(2, TimeSpan.FromMilliseconds(20), initial, false);
        var ticket = shadow.RecordRequest(initial, VulcanSkill.GreatStrides, TimeSpan.FromMilliseconds(20));
        Assert.NotEqual(default, ticket);
        var result = Assert.Single(shadow.HistoricalResults);
        Assert.Equal("Validated", result.ReplayValidation);
        Assert.Equal(VulcanSkill.GreatStrides, result.Source.ExistingSolverAttempt);
        Assert.False(result.Executable);
        Assert.NotNull(result.ProposedFirstAction);
        shadow.End("Managed craft finished.", TimeSpan.FromSeconds(1), managedCraftEnded: true);
        using var json = System.Text.Json.JsonDocument.Parse(AdaptiveShadowExport.Serialize(
            AdaptiveShadowView.FromTrace(shadow.Status, shadow.CaptureTrace())));
        Assert.Equal(6, json.RootElement.GetProperty("SchemaVersion").GetInt32());
        Assert.False(json.RootElement.TryGetProperty("Analysis", out _));
        Assert.Contains("never executable", json.RootElement.GetProperty("AnalysisUsage").GetString());
        Assert.Equal("Validated", json.RootElement.GetProperty("Data").GetProperty("HistoricalResults")[0].GetProperty("ReplayValidation").GetString());
    }

    [Theory]
    [InlineData(4, true)]
    [InlineData(1, false)]
    public void ActualPluginPoorTerminalPreservesConditionAndExportsPacketMismatch(int terminalCondition, bool accepted)
    {
        var craft = TestCraft() with { RecipeId = 5618, RequiredProgress = 100 };
        var simulation = new AdaptiveSimulation(craft, AdaptiveCraftGoal.Resolve(craft, AdaptiveGoalKind.Quality, craft.MaxQuality));
        var before = new AdaptiveObservedFrame(1, 0, 0, 80, 600, Condition.Normal);
        var state = new AdaptiveSearchState(true, 0, 0, 80, 600, Condition.Normal, TrainedPerfectionAvailable: true);
        using var shadow = new AdaptiveShadowSession(craft, before, TimeSpan.Zero, scheduler: new UnfinishedScheduler(),
            nativeContext: new(1, craft.RecipeId, 0, 2));
        long sequence = 2, frame = 0;
        foreach (var condition in new[] { Condition.Excellent, Condition.Poor })
        {
            var ticket = shadow.RecordRequest(before, VulcanSkill.BasicTouch, TimeSpan.Zero, (uint)VulcanSkill.BasicTouch);
            Assert.True(shadow.ReportRequest(ticket, true));
            simulation.TryApply(state, VulcanSkill.BasicTouch, out state);
            state = state with { Condition = condition };
            var after = new AdaptiveObservedFrame(before.StepIndex + 1, state.Progress, state.Quality, state.Durability,
                state.CP, condition, InnerQuiet: state.InnerQuiet);
            shadow.ObserveNative(new(1, ++sequence, 9, craft.RecipeId, 0, (uint)VulcanSkill.BasicTouch,
                after.StepIndex, after.Progress, after.Quality, after.Durability, (int)condition + 1, 18), TimeSpan.Zero);
            shadow.Observe(++frame, TimeSpan.Zero, after, false);
            shadow.Observe(++frame, TimeSpan.Zero, after, false);
            Assert.False(shadow.Blocked);
            before = after;
        }
        var final = shadow.RecordRequest(before, VulcanSkill.CarefulSynthesis, TimeSpan.Zero, (uint)VulcanSkill.CarefulSynthesis);
        Assert.True(shadow.ReportRequest(final, true));
        simulation.TryApply(state, VulcanSkill.CarefulSynthesis, out var predicted);
        shadow.ObserveNative(new(1, ++sequence, 9, craft.RecipeId, 0, (uint)VulcanSkill.CarefulSynthesis,
            before.StepIndex, 100, predicted.Quality, predicted.Durability, terminalCondition, 22), TimeSpan.Zero);
        shadow.End("Managed craft finished.", TimeSpan.Zero, managedCraftEnded: true);
        Assert.Equal(!accepted, shadow.Blocked);
        Assert.Equal(accepted ? "ExperimentalPacketSuccess" : "Unobserved", shadow.Summary.TerminalObservation);
        Assert.Equal(before, shadow.Summary.LastStableFrame);
        Assert.Equal(2, shadow.CorrelatedActions);
        if (!accepted)
        {
            var mismatch = shadow.Summary.NativeEvidence!.LastMismatch!;
            var difference = Assert.Single(mismatch.Differences);
            Assert.Equal(new AdaptiveFieldDifference("ConditionPlusOne", "4", "1"), difference);
        }
        using var json = System.Text.Json.JsonDocument.Parse(AdaptiveShadowExport.Serialize(AdaptiveShadowView.FromTrace(shadow.Status, shadow.CaptureTrace())));
        Assert.False(json.RootElement.GetProperty("NativeActionIdentityConfirmed").GetBoolean());
        var evidence = json.RootElement.GetProperty("Data").GetProperty("Summary").GetProperty("NativeEvidence");
        Assert.Equal(accepted ? System.Text.Json.JsonValueKind.Null : System.Text.Json.JsonValueKind.Object, evidence.GetProperty("LastMismatch").ValueKind);
        Assert.False(evidence.GetProperty("ExecutionAuthorized").GetBoolean());
    }

    [Fact]
    public void LiveNativeStartClaimsBeforeLegacySolverAndNeverRequestsMidCraftRaphaelOrHistoricalAnalysis()
    {
        using var assembly = new AssemblyView();
        var start = assembly.Calls("CraftingGameInterop", "TransitionFromWaitStart");
        Assert.True(Array.IndexOf(start, "TryBegin") >= 0);
        Assert.True(Array.IndexOf(start, "TryBegin") < Array.IndexOf(start, "OnCraftStarted"));
        var calls = assembly.Calls("AdaptiveLiveDiagnostics", "TryBegin");
        Assert.Contains("get_Quiescent", calls);
        Assert.Contains("TryAttach", calls);
        Assert.Contains("TryResolveAdaptiveGoal", calls);
        foreach (var forbidden in new[] { "OnCraftStarted", "EnqueueOrPromoteRequest", "BeginCraft", "BuildInitialStepState", "TryExecuteActionAsync" })
            Assert.DoesNotContain(forbidden, calls);
        Assert.Contains("get_OwnsCraft", assembly.Calls("CraftingGameInterop", "TransitionFromInProgress"));
        Assert.Contains("DispatchIfReady", assembly.Calls("CraftingGameInterop", "TransitionFromInProgress"));
    }

    [Fact]
    public void AutomaticSeedPreparationGatesSynthesisAndRechecksBeforeCallback()
    {
        using var assembly = new AssemblyView();
        var start = assembly.Calls("CraftingGameInterop", "StartCraft");
        Assert.Contains("get_AdaptiveSelected", start);
        Assert.Contains("BeginSeedPreparation", start);
        Assert.Contains("get_PreparationTimeoutMs", start);
        var execution = assembly.Calls("CraftingGameInterop", "ExecuteAdaptivePreparedCraft");
        Assert.True(Array.IndexOf(execution, "WaitForAdaptiveSeed") < Array.IndexOf(execution, "ExecuteCraft"));
        Assert.Contains("PrepareSeed", assembly.Calls("CraftingGameInterop", "WaitForAdaptiveSeed"));
        Assert.Contains("get_IsInFrameworkUpdateThread", assembly.Calls("CraftingGameInterop", "WaitForAdaptiveSeed"));
        Assert.Contains("get_IsLoggedIn", assembly.Calls("AdaptiveLiveDiagnostics", "PrepareCaptureForCraft"));
        Assert.Contains("get_Paused", assembly.Calls("AdaptiveLiveDiagnostics", "PrepareCaptureForCraft"));
        Assert.Contains("get_IsInFrameworkUpdateThread", assembly.Calls("AdaptiveLiveDiagnostics", "PrepareCaptureForCraft"));
        Assert.Contains("CancelPreparation", assembly.Calls("AdaptiveLiveDiagnostics", "BeginSeedPreparation"));
        Assert.Contains("PrepareCaptureForCraft", assembly.Calls("AdaptiveLiveDiagnostics", "PrepareSeed"));
        Assert.Contains("PrepareCapture", assembly.Calls("AdaptiveLiveDiagnostics", "PrepareCaptureForCraft"));
        Assert.Contains("CancelPreparation", assembly.Calls("AdaptiveLiveDiagnostics", "Stop"));
    }

    [Fact]
    public void CollectabilityCaptureUsesLocalSheetsBeforeSnapshotAndNeverLegacyFallback()
    {
        using var assembly = new AssemblyView();
        var read = assembly.Calls("AdaptiveShadowNativeReader", "TryCraft");
        Assert.Contains("TryResolve", read);
        Assert.True(Array.IndexOf(read, "TryResolve") < Array.IndexOf(read, "Capture"));
        var lookup = assembly.Calls("AdaptiveCollectabilityReader", "TryResolve");
        Assert.Contains("GetSubrowExcelSheet", lookup);
        Assert.Contains("GetExcelSheet", lookup);
        foreach (var forbidden in new[] { "BuildCraftState", "BuildInitialStepState", "GetAsync", "GetStringAsync", "ExecuteCraft", "UseAction" })
            Assert.DoesNotContain(forbidden, lookup);
        var goal = assembly.Calls("CraftingGameInterop", "TryResolveAdaptiveGoal");
        Assert.Contains("get_AdaptiveCollectabilityTier", goal);
        Assert.Equal(3, new RaphaelSolveCoordinatorConfig().AdaptiveCollectabilityTier);
    }

    [Fact]
    public void CollectableOutputReconciliationScansLoadedPlayerBagsWithoutChangingIngredientCapture()
    {
        using var assembly = new AssemblyView();
        foreach (var method in new[] { "BeginResultTracking", "UpdateCraftResult" })
            Assert.Contains("TryCaptureCraftOutputCount", assembly.Calls("CraftingQueueProcessor", method));
        Assert.Contains("get_AlwaysCollectable", assembly.Calls("CraftingQueueProcessor", "BeginResultTracking"));
        Assert.Contains("_resultCollectable", assembly.Fields("CraftingQueueProcessor", "BeginResultTracking", true));
        var output = assembly.Calls("CraftingQueueProcessor", "TryCaptureCraftOutputCount");
        foreach (var required in new[] { "TryCaptureCraftInventory", "GetPlayerInventoryAvailability", "GetInventoryContainer", "GetInventorySlot", "GetBaseItemId", "IsCollectable", "Add" })
            Assert.Contains(required, output);
        var ingredientLambdas = assembly.Metadata.MethodDefinitions.Where(h =>
            assembly.Metadata.GetString(assembly.Metadata.GetMethodDefinition(h).Name).StartsWith("<TryCaptureCraftInventory>"));
        Assert.NotEmpty(ingredientLambdas);
        Assert.Contains("GetInventoryItemCount", ingredientLambdas.SelectMany(h =>
            TeleportNativeBoundaryTests.Calls(assembly.Pe, assembly.Metadata, h)));
        Assert.DoesNotContain("TryCaptureCraftOutputCount", assembly.Calls("CraftingQueueProcessor", "TryCaptureCraftInventory"));
        Assert.Contains("ConfirmedCrafts", assembly.Calls("CraftingQueueProcessor", "UpdateCraftResult"));
        Assert.Contains("Pause", assembly.Calls("CraftingQueueProcessor", "UpdateCraftResult"));
        foreach (var forbidden in new[] { "TryCraft", "get_HasTerminal", "get_ConfirmedState", "ExecuteCraft", "UseAction" })
            Assert.DoesNotContain(forbidden, output);
    }

    [Fact]
    public void PreparationRejectionPausesAndExportsAttemptedContextWithoutRetainedLiveHistory()
    {
        using var assembly = new AssemblyView();
        var reject = assembly.Calls("AdaptiveLiveDiagnostics", "RejectPreparation");
        Assert.Contains("Pause", reject);
        Assert.Contains("Stop", reject);
        Assert.Contains("SerializePreparation", assembly.Calls("AdaptiveLiveDiagnostics", "Stop"));
        Assert.Contains("SerializePreparation", assembly.Calls("AdaptiveLiveDiagnostics", "PublishPreparationStatus"));
        Assert.Contains("_attemptedRecipe", assembly.Fields("AdaptiveLiveDiagnostics", "BeginSeedPreparation", true));
        Assert.Contains("_attemptedTicket", assembly.Fields("AdaptiveLiveDiagnostics", "BeginSeedPreparation", true));
        Assert.Contains("_analysis", assembly.Fields("AdaptiveLiveDiagnostics", "BeginSeedPreparation", true));
        var empty = assembly.Calls("AdaptivePreparationView", "get_LiveAnalysis");
        Assert.Contains("get_Empty", empty);
        foreach (var forbidden in new[] { "ExecuteCraft", "QueueSeed", "RegisterSolver", "TryExecuteActionAsync" })
            Assert.DoesNotContain(forbidden, reject);
    }

    [Fact]
    public void SelectedAdaptiveIsGatedBeforeSynthesisAndCannotFallThroughToLegacyExecution()
    {
        using var assembly = new AssemblyView();
        var start = assembly.Calls("CraftingGameInterop", "StartCraft");
        Assert.True(Array.IndexOf(start, "CanStartSolver") < Array.IndexOf(start, "Enqueue"));
        Assert.Contains("_adaptiveCraftRequired", assembly.Fields("CraftingGameInterop", "StartCraft", true));
        Assert.Contains("_adaptiveCraftRequired", assembly.Fields("CraftingGameInterop", "ExecuteCraft"));
        Assert.Contains("get_CaptureReady", assembly.Calls("CraftingGameInterop", "ExecuteCraft"));
        Assert.Contains("_adaptiveCraftRequired", assembly.Fields("CraftingGameInterop", "TransitionFromWaitStart"));
        var begin = assembly.Calls("AdaptiveLiveDiagnostics", "TryBegin");
        Assert.Contains("Stop", begin);
        Assert.DoesNotContain("OnCraftStarted", begin);
        Assert.DoesNotContain("RegisterSolver", begin);
        foreach (var method in new[] { "Initialize", "ReloadSolvers", "ReloadSolversForCraft" })
            Assert.Contains("UnregisterSolver", assembly.Calls("CraftingGameInterop", method));
        foreach (var method in new[] { "ReloadSolvers", "ReloadSolversForCraft" })
            Assert.DoesNotContain("_adaptiveCraftRequired", assembly.Fields("CraftingGameInterop", method, true));
    }

    [Fact]
    public void SelectedAdaptiveQueueChecksConflictsBeforeOutputTrackingAndDirectStartChecksReadiness()
    {
        using var assembly = new AssemblyView();
        var queue = assembly.Calls("CraftingQueueProcessor", "StartNextCraft");
        Assert.Contains("Pause", queue);
        Assert.True(Array.IndexOf(queue, "CanStartSolver") < Array.IndexOf(queue, "BeginResultTracking"));
        Assert.True(Array.IndexOf(queue, "CanStartSolver") < Array.IndexOf(queue, "StartCraft"));
        var direct = assembly.Calls("VulcanWindow", "StartCraftWithRaphaelAfterJobSwitch");
        Assert.Contains("CanStartSolver", direct);
        Assert.Contains("get_AdaptiveSelected", assembly.Calls("AdaptiveLiveDiagnostics", "PrepareCapture"));
        Assert.Contains("RejectPreparation", assembly.Calls("AdaptiveLiveDiagnostics", "PrepareSeed"));
    }

    [Fact]
    public void SettingsExposeStatusAndStopWithoutSeparateArmingOrNativeReads()
    {
        using var assembly = new AssemblyView();
        Assert.DoesNotContain("DrawAdaptiveSolverSettings", assembly.Calls("VulcanWindow", "DrawGeneralSettings"));
        Assert.Contains("DrawAdaptiveSolverSettings", assembly.Calls("VulcanWindow", "DrawSettingsPanels"));
        Assert.Contains("DrawSettingsSectionLink", assembly.Calls("VulcanWindow", "DrawSettingsPanels"));
        var permission = assembly.Calls("VulcanWindow", "DrawAdaptiveSolverSettings");
        Assert.Contains("RequestStop", permission);
        Assert.DoesNotContain("TryFrame", permission);
        Assert.Contains("Save", permission);
        Assert.Contains("ReloadSolvers", permission); // Target changes revoke preparing/live context.
        foreach (var method in new[] { "Initialize", "ReloadSolvers", "ReloadSolversForCraft", "StartCraft" })
        {
            Assert.DoesNotContain("RequestArm", assembly.Calls("CraftingGameInterop", method));
            Assert.DoesNotContain("TryEnable", assembly.Calls("CraftingGameInterop", method));
        }
    }

    [Fact]
    public void SeedPreparationUsesSharedCacheAndQueueButNeverStartsOwnProcessOrMutatesSettings()
    {
        using var assembly = new AssemblyView();
        var lookup = assembly.Calls("AdaptiveLiveDiagnostics", "LookupSeed");
        Assert.Contains("TryGetAdaptiveSeed", lookup);
        Assert.Contains("HasFailedSolution", lookup);
        Assert.Contains("IsKnown", lookup);
        Assert.True(Array.IndexOf(lookup, "TryGetAdaptiveSeed") < Array.IndexOf(lookup, "HasFailedSolution"));
        Assert.Contains("EnqueueOrPromoteRequest", assembly.Calls("AdaptiveLiveDiagnostics", "QueueSeed"));
        foreach (var forbidden in new[] { "Save", "SaveIfDirty", "CancelAllPendingSolves", "RemoveCachedSolution", "UseAction", "TryExecuteActionAsync", "Start" })
        {
            Assert.DoesNotContain(forbidden, assembly.AllCalls("AdaptiveLiveDiagnostics"));
            Assert.DoesNotContain(forbidden, assembly.AllCalls("AdaptiveSeedPreparation"));
        }
    }

    [Fact]
    public void LiveSeedHandoffIsOneTimeAndStillRequiresFreshNativeStart()
    {
        using var assembly = new AssemblyView();
        var begin = assembly.Calls("AdaptiveLiveDiagnostics", "TryBegin");
        Assert.Contains("Consume", begin);
        Assert.Contains("TryAttach", begin);
        Assert.DoesNotContain("QueueSeed", begin);
        Assert.DoesNotContain("LookupSeed", begin);
        Assert.DoesNotContain("QueueSeed", assembly.Calls("AdaptiveLiveDiagnostics", "Tick"));
        var root = assembly.Calls("AdaptiveSolveCoordinator", "ProcessObservation");
        Assert.Contains("MatchesCraft", root);
        Assert.Contains("TryValidate", root);
        Assert.Contains("IsBetter", assembly.Calls("AdaptiveSolveCoordinator", "Publish"));
        Assert.Contains("get_StartupSeedStatus", assembly.Calls("AdaptiveLiveDiagnostics", "Refresh"));
    }

    [Fact]
    public void SelectedOrdinaryAdaptiveQueueDefersLegacyRaphaelHardGateButQuickAndLegacyPathsRemain()
    {
        using var assembly = new AssemblyView();
        foreach (var method in new[] { "TransitionToRaphaelOrCraft", "EnsureRaphaelSolutionReadyForCurrentCraft", "UsesAdaptiveSeedPreparation" })
        {
            var calls = assembly.Calls("CraftingQueueProcessor", method);
            Assert.Contains("get_EffectiveSolverMode", calls);
            Assert.Contains("get_UseQuickSynthesis", calls);
        }
        Assert.Contains("UsesAdaptiveSeedPreparation", assembly.Calls("CraftingQueueProcessor", "CheckRaphaelSolutionReady"));
        Assert.Contains("SkipFailedRaphaelItem", assembly.Calls("CraftingQueueProcessor", "CheckRaphaelSolutionReady"));
        Assert.Contains("HasFailedSolution", assembly.Calls("CraftingQueueProcessor", "EnsureRaphaelSolutionReadyForCurrentCraft"));
    }

    [Fact]
    public void AutomaticCaptureAndShadowEnableCheckExclusiveDrainedLanesWithoutSavingSettings()
    {
        using var assembly = new AssemblyView();
        var arm = assembly.Calls("AdaptiveLiveDiagnostics", "PrepareCapture");
        Assert.Contains("get_Quiescent", arm);
        Assert.Contains("PrepareForLiveTrial", arm);
        Assert.Contains("Poll", arm);
        Assert.Contains("TryEnable", assembly.Calls("AdaptiveLiveDiagnostics", "EnableCapture"));
        var shadowEnable = assembly.Calls("AdaptiveShadowDiagnostics", "SetEnabled");
        Assert.Contains("get_Preparing", shadowEnable);
        Assert.Contains("get_OwnsCraft", shadowEnable);
        Assert.Contains("get_Quiescent", shadowEnable);
        Assert.Contains("RunOnFrameworkThread", assembly.Calls("AdaptiveLiveDiagnostics", "RequestStop"));
        Assert.Contains("TryConsume", assembly.Calls("AdaptiveLiveDiagnostics", "TryBegin"));
        foreach (var forbidden in new[] { "Save", "SaveIfDirty", "RegisterSolver", "TryEnable" })
            Assert.DoesNotContain(forbidden, assembly.Calls("AdaptiveLiveDiagnostics", "TryBegin"));
    }

    [Fact]
    public void LiveDispatchChecksFreshContextFrameAndNativeAvailabilityBeforeTransaction()
    {
        using var assembly = new AssemblyView();
        var calls = assembly.Calls("AdaptiveLiveDiagnostics", "DispatchIfReady");
        foreach (var required in new[] { "get_IsInFrameworkUpdateThread", "Drain", "CheckContext", "TryFrame", "ActionId", "GetActionStatus", "TryDispatch" })
            Assert.Contains(required, calls);
        Assert.True(Array.IndexOf(calls, "GetActionStatus") < Array.IndexOf(calls, "TryDispatch"));
        Assert.True(Array.LastIndexOf(calls, "Drain") > Array.IndexOf(calls, "TryDispatch"));
        Assert.Contains("ActionQueued", assembly.Fields("AdaptiveLiveDiagnostics", "DispatchIfReady"));
        foreach (var field in new[] { "ActionQueued", "QueuedActionId", "QueuedActionType", "QueuedTargetId", "QueueType" })
            Assert.DoesNotContain(field, assembly.Fields("AdaptiveLiveDiagnostics", "DispatchIfReady", writesOnly: true));
        var transaction = assembly.Calls("AdaptiveLiveTrial", "TryDispatch");
        Assert.True(Array.IndexOf(transaction, "RecordRequest") < Array.IndexOf(transaction, "TryConsumeLease"));
        Assert.True(Array.IndexOf(transaction, "TryConsumeLease") < Array.IndexOf(transaction, "MarkLiveDispatchInvoked"));
        Assert.True(Array.IndexOf(transaction, "MarkLiveDispatchInvoked") < Array.IndexOf(transaction, "Invoke"));
        Assert.True(Array.IndexOf(transaction, "Invoke") < Array.IndexOf(transaction, "RecordLiveNativeReturn"));
        Assert.DoesNotContain("ReportExecution", transaction);
        Assert.DoesNotContain("ReportRequest", transaction);
        Assert.Contains("ReportExecution", assembly.Calls("AdaptiveLiveTrial", "Tick"));
        foreach (var forbidden in new[] { "TryExecuteActionAsync", "Delay", "Save", "OnCraftAdvanced" })
            Assert.DoesNotContain(forbidden, transaction);
    }

    [Fact]
    public void InstalledClientStructsExposesAuditedReadOnlyQueueFlag()
    {
        var field = typeof(FFXIVClientStructs.FFXIV.Client.Game.ActionManager).GetField("ActionQueued")!;
        Assert.Equal(typeof(bool), field.FieldType);
        var offset = Assert.Single(field.GetCustomAttributes(typeof(System.Runtime.InteropServices.FieldOffsetAttribute), false));
        Assert.Equal(0x68, ((System.Runtime.InteropServices.FieldOffsetAttribute)offset).Value);
    }

    [Fact]
    public void ActualPluginConfirmationOnlyModeCreatesNoHistoricalSearchAndNeedsStableFrames()
    {
        var craft = TestCraft();
        var frame = new AdaptiveObservedFrame(1, 0, 0, 80, 600, Condition.Normal);
        using var coordinator = new AdaptiveSolveCoordinator(new UnfinishedScheduler());
        using var trial = new AdaptiveLiveTrial(coordinator, craft, AdaptiveCraftGoal.Resolve(craft, AdaptiveGoalKind.ProgressOnly),
            frame, new(1, craft.RecipeId, 0, 2), TimeSpan.Zero, TimeSpan.Zero);
        trial.Tick(1, TimeSpan.Zero, frame, false, false);
        Assert.Null(trial.Evidence.ConfirmedState);
        trial.Tick(2, TimeSpan.Zero, frame, false, false);
        Assert.NotNull(trial.Evidence.ConfirmedState);
        Assert.Equal(AdaptiveDecisionPhase.Searching, trial.Decision.Phase);
        Assert.Equal(0, trial.Evidence.AnalysisSummary.SearchesDispatched);
        Assert.Empty(trial.Evidence.HistoricalResults);
    }

    [Theory]
    [InlineData("AdaptiveLiveEvidenceTrace")]
    [InlineData("AdaptiveLiveView")]
    [InlineData("AdaptiveLiveExport")]
    [InlineData("AdaptiveLiveAnalysisSnapshot")]
    [InlineData("AdaptiveLiveRootRecord")]
    [InlineData("AdaptivePublishedPlan")]
    [InlineData("AdaptiveLiveAnalysisHistory")]
    public void LiveExportTypesCannotDispatchOrMutateGameState(string type)
    {
        using var assembly = new AssemblyView();
        var calls = assembly.AllCalls(type).ToArray();
        foreach (var forbidden in new[] { "UseAction", "TryDispatch", "TryConsumeLease", "ReportExecution", "TryFrame", "Save", "RegisterSolver" })
            Assert.DoesNotContain(forbidden, calls);
        Assert.Contains("FromConfirmation", assembly.Calls("AdaptiveLiveDiagnostics", "Refresh"));
    }

    [Fact]
    public void LiveAnalysisCaptureCannotRunSearchReplayOrNativeReads()
    {
        using var assembly = new AssemblyView();
        var calls = assembly.Calls("AdaptiveSolveCoordinator", "CaptureDecision");
        foreach (var forbidden in new[] { "Search", "Schedule", "TryApply", "TryValidate", "Execute", "UseAction", "TryConsumeLease" })
            Assert.DoesNotContain(forbidden, calls);
        Assert.Contains("get_Analysis", assembly.Calls("VulcanWindow", "DrawAdaptiveShadowDebug"));
        var draw = assembly.Calls("VulcanWindow", "DrawAdaptiveLiveRoot");
        foreach (var forbidden in new[] { "TryFrame", "TryCraft", "TryDispatch", "Poll", "CaptureTrace", "Schedule" })
            Assert.DoesNotContain(forbidden, draw);
        Assert.Contains("get_AnalysisVersion", assembly.Calls("AdaptiveLiveDiagnostics", "Refresh"));
    }

    [Fact]
    public void ActualPluginLiveTerminalExportUsesLiveLabelsAndKeepsOnlyActualFrame()
    {
        var craft = TestCraft() with { RecipeId = 5618, MaxDurability = 20, RequiredProgress = 100 };
        var initial = new AdaptiveObservedFrame(1, 0, 0, 20, 600, Condition.Normal);
        using var evidence = new AdaptiveShadowSession(craft, initial, TimeSpan.Zero,
            nativeContext: new(1, craft.RecipeId, 0, 2), confirmationGoal: AdaptiveCraftGoal.Resolve(craft, AdaptiveGoalKind.ProgressOnly));
        evidence.Observe(1, TimeSpan.Zero, initial, false);
        evidence.Observe(2, TimeSpan.Zero, initial, false);
        var ticket = evidence.RecordRequest(initial, VulcanSkill.Groundwork, TimeSpan.Zero, 100410);
        Assert.True(evidence.MarkLiveDispatchInvoked(ticket, TimeSpan.Zero));
        Assert.True(evidence.RecordLiveNativeReturn(ticket, true, TimeSpan.Zero));
        evidence.ObserveNative(new(1, 3, 9, craft.RecipeId, 0, 100410, 1, 100, 0, 0, 1, 30), TimeSpan.Zero);
        evidence.End("Managed craft ended.", TimeSpan.Zero, true);
        var view = new AdaptiveLiveView("Managed craft ended.", "Synthetic actual-plugin boundary case", new(AdaptiveDecisionPhase.Completed,
            Guid.NewGuid(), null, VulcanSkill.None, "Finished.", 0, 0, null), true, null, false, false,
            AdaptiveLiveEvidenceTrace.FromConfirmation(evidence.CaptureTrace()));
        var exported = AdaptiveLiveExport.Serialize(view);
        using var json = System.Text.Json.JsonDocument.Parse(exported);
        Assert.Equal(4, json.RootElement.GetProperty("SchemaVersion").GetInt32());
        Assert.Equal("ExperimentalPacketSuccess", json.RootElement.GetProperty("Evidence").GetProperty("Summary").GetProperty("TerminalObservation").GetString());
        Assert.Equal(initial, view.Evidence.Summary.LastStableFrame);
        Assert.DoesNotContain("Historical shadow analysis", exported);
        Assert.DoesNotContain("Execution remains disabled", exported);
        Assert.DoesNotContain("Shadow terminal observation", exported);
        Assert.False(view.NativeLayoutValidated);
        Assert.False(view.Evidence.Summary.NativeEvidence!.ExecutionAuthorized);
    }

    private static AdaptiveCraftSnapshot TestCraft() => AdaptiveCraftSnapshot.Capture(new CraftState
    {
        StatCraftsmanship = 1000, StatControl = 1000, StatCP = 600, StatLevel = 100,
        CraftLevel = 90, CraftDurability = 80, CraftProgress = 400, CraftQualityMax = 5000,
        CraftProgressDivider = 100, CraftProgressModifier = 100,
        CraftQualityDivider = 100, CraftQualityModifier = 100, CraftHQ = true,
        UnlockedManipulation = true,
    });

    private sealed class ImmediateScheduler : IAdaptiveSearchScheduler
    {
        public Task<AdaptiveSearchResult> Schedule(AdaptiveSearchRequest request, CancellationToken cancellation)
            => Task.FromResult(new AdaptiveSearchEngine().Search(new(request.Identity.Craft, request.Identity.Goal),
                request.Identity.State, request.Identity.Limits, cancellation: cancellation));
    }

    private sealed class UnfinishedScheduler : IAdaptiveSearchScheduler
    {
        public Task<AdaptiveSearchResult> Schedule(AdaptiveSearchRequest request, CancellationToken cancellation)
            => new TaskCompletionSource<AdaptiveSearchResult>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
    }
}
