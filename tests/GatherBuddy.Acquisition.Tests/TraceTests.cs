using GatherBuddy.Crafting;
using GatherBuddy.Vulcan.Vendors;
using Newtonsoft.Json;
using Xunit;

namespace GatherBuddy.Acquisition.Tests;

public class TraceTests
{
    private static MaterialAcquisitionPlanningInput Input(AcquisitionCurrencyLedgerSnapshot? ledger = null,
        IEnumerable<VendorPurchaseAllocation>? replaced = null)
        => new(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            ResolvedVendorPurchasePolicy.Resolve(new() { Enabled = true }),
            [new(100, IngredientQualityDemand.FromPreferNQ(5), true)],
            [new(100, 1, VendorCurrencyGroup.Gil, 10, 1, VendorShopType.GilShop, new(10, "Vendor", 20)),
             new(100, 1, VendorCurrencyGroup.Gil, 10, 1, VendorShopType.GilShop, new(11, "Unsupported", 21), Supported: false)],
            new Dictionary<uint, AcquisitionInventory> { [100] = new(1, 0) },
            new Dictionary<uint, ulong> { [1] = 20 },
            ledger ?? new(new Dictionary<uint, ulong>(), new Dictionary<uint, ulong>()), replacedAllocations: replaced);

    [Fact]
    public void TraceDoesNotChangePlannerDecisionsOrInputs()
    {
        var input = Input();
        var trace = new AcquisitionTrace(input.SessionId);
        var plain = MaterialAcquisitionPlanner.Plan(input);
        var traced = MaterialAcquisitionPlanner.Plan(input, trace);
        Assert.Equal(plain.Allocations.Select(a => (a.ItemId, a.CurrencyItemId, a.ExpectedCurrencyCost, a.RequestedOutputQuantity)),
            traced.Allocations.Select(a => (a.ItemId, a.CurrencyItemId, a.ExpectedCurrencyCost, a.RequestedOutputQuantity)));
        Assert.Equal(plain.Materials.Select(m => (m.GatheringDemand, m.UnresolvedDemand, m.PrimaryShortage)),
            traced.Materials.Select(m => (m.GatheringDemand, m.UnresolvedDemand, m.PrimaryShortage)));
        Assert.Equal(plain.ProjectedSpending.ToArray(), traced.ProjectedSpending.ToArray());
        Assert.Contains(trace.Events, e => e.Action == "Demand" && e.Detail.Contains("consumedNQ=1"));
        Assert.Contains(trace.Events, e => e.Action == "Allocate" && e.Detail.Contains("commitments=0->20") && e.Detail.Contains("spendable=20->0"));
        Assert.Contains(trace.Events, e => e.Action == "Outcome" && e.Detail.Contains("gatheringQuality="));
        Assert.Contains(trace.Events, e => e.Action == "RejectedOffers" && e.Detail.Contains("offerCount=1"));
        Assert.Empty(input.Ledger.OutstandingCommitments);
    }

    [Fact]
    public void PolicyTraceReportsActualCorrectionsAndNormalizationIsIdempotent()
    {
        var policy = new AutomaticVendorPurchasePolicy
        {
            AllowedCurrencyGroups = [VendorCurrencyGroup.Gil, (VendorCurrencyGroup)999, VendorCurrencyGroup.Gil],
            CurrencyPriority = [], CurrencyReservesByItemId = new() { [0] = 10 },
        };
        var trace = new AcquisitionTrace(Guid.NewGuid());
        Assert.True(policy.Normalize(trace));
        Assert.Contains(trace.Events, e => e.Detail.Contains("RemoveUnknownCurrency") && e.Level == AcquisitionTraceLevel.Warning);
        Assert.Contains(trace.Events, e => e.Detail.Contains("RemoveDuplicate"));
        Assert.Contains(trace.Events, e => e.Detail.Contains("RemoveInvalidCurrencyId"));
        Assert.Contains(trace.Events, e => e.Detail.Contains("AppendAllowedCurrency"));
        var count = trace.Events.Count;
        Assert.False(policy.Normalize(trace));
        Assert.Equal(count, trace.Events.Count);
    }

    [Fact]
    public void LegacyDefaultsAreNotReportedAsCorrections()
    {
        var policy = JsonConvert.DeserializeObject<AutomaticVendorPurchasePolicy>("{}")!;
        var trace = new AcquisitionTrace(Guid.NewGuid());
        policy.DrainLoadDiagnostics(trace, "Global");
        Assert.Empty(trace.Events);
        var settings = JsonConvert.DeserializeObject<VendorPurchaseListSettings>("{}")!;
        settings.DrainLoadDiagnostics(trace, "List");
        Assert.Empty(trace.Events);
    }

    [Fact]
    public void DeserializeDiagnosticsSurviveCallbacksAndDrainOnlyOnce()
    {
        var settings = JsonConvert.DeserializeObject<VendorPurchaseListSettings>(
            "{\"Mode\":999,\"CustomPolicy\":{\"AllowedCurrencyGroups\":[999],\"CurrencyPriority\":null}}")!;
        var trace = new AcquisitionTrace(Guid.NewGuid());
        settings.DrainLoadDiagnostics(trace, "List[42]");
        Assert.Contains(trace.Events, e => e.Detail.Contains("DisableInvalidOverride"));
        Assert.Contains(trace.Events, e => e.Detail.Contains("RemoveUnknownCurrency"));
        Assert.Contains(trace.Events, e => e.Detail.Contains("InitializeMissingCollection"));
        var count = trace.Events.Count;
        settings.DrainLoadDiagnostics(trace, "List[42]");
        Assert.Equal(count, trace.Events.Count);
        var json = JsonConvert.SerializeObject(settings);
        Assert.DoesNotContain("loadDiagnostics", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DisableInvalidOverride", json);
    }

    [Fact]
    public void TemporaryResolutionIsExplicitAndTracingDoesNotChangePolicy()
    {
        var global = new AutomaticVendorPurchasePolicy { Enabled = false };
        var list = new VendorPurchaseListSettings { Mode = VendorPurchaseOverrideMode.Custom, CustomPolicy = new() { Enabled = true } };
        list.CustomPolicy.CurrencyReservesByItemId[1] = 100;
        var trace = new AcquisitionTrace(Guid.NewGuid());
        var plain = ResolvedVendorPurchasePolicy.Resolve(global, list);
        var traced = ResolvedVendorPurchasePolicy.Resolve(global, list, trace, temporary: true);
        Assert.Equal(plain.Enabled, traced.Enabled);
        Assert.Equal(plain.CurrencyPriority, traced.CurrencyPriority);
        Assert.Equal(plain.CurrencyReservesByItemId.ToArray(), traced.CurrencyReservesByItemId.ToArray());
        Assert.Contains(trace.Events, e => e.Action == "Resolve" && e.Detail.Contains("temporary=True"));
        Assert.Contains(trace.Events, e => e.Action == "Snapshot" && e.Detail.Contains("detached=True"));
        Assert.Contains(trace.Events, e => e.Action == "Reserve" && e.Detail.Contains("protected=100"));
    }

    [Fact]
    public void TraceOrderIsDeterministicWithoutTrackingIds()
    {
        var input = Input();
        var a = new AcquisitionTrace(input.SessionId);
        var b = new AcquisitionTrace(input.SessionId);
        MaterialAcquisitionPlanner.Plan(input, a);
        MaterialAcquisitionPlanner.Plan(input, b);
        Assert.Equal(a.Events, b.Events);
        Assert.Equal(Enumerable.Range(1, a.Events.Count), a.Events.Select(e => e.Sequence));
    }

    [Fact]
    public void BoundedTraceReportsOmissionsAndAlwaysKeepsOutcomeSummary()
    {
        var input = Input();
        var trace = new AcquisitionTrace(input.SessionId, 2);
        var plan = MaterialAcquisitionPlanner.Plan(input, trace);
        Assert.Equal(2, trace.Events.Count);
        Assert.True(trace.DroppedEvents > 0);
        Assert.Contains("truncated=True", trace.Summary);
        Assert.Contains($"allocations={plan.Allocations.Count}", trace.Summary);
        Assert.Throws<NotSupportedException>(() => ((IList<AcquisitionTraceEvent>)trace.Events).Clear());
        trace.Record("P2", "Unsafe\nAction", "details\r\n");
        Assert.Equal(2, trace.Events.Count);
    }

    [Fact]
    public void TracesBoundAndSanitizeExternalFields()
    {
        var trace = new AcquisitionTrace(Guid.NewGuid());
        trace.Record("P1\r", "Action\n", new string('x', 2000) + "\r\n");
        var text = trace.Format(trace.Events.Single());
        Assert.DoesNotContain("\r", text);
        Assert.DoesNotContain("\n", text);
        Assert.Contains("[field truncated]", text);
        Assert.True(text.Length < 1300);
    }

    [Fact]
    public void ReplanningTracesExplicitReleaseWithoutChangingSettledSpending()
    {
        var input = Input();
        var first = MaterialAcquisitionPlanner.Plan(input);
        var next = Input(first.ResultingLedger, first.Allocations);
        var trace = new AcquisitionTrace(next.SessionId);
        MaterialAcquisitionPlanner.Plan(next, trace);
        Assert.Contains(trace.Events, e => e.Action == "ReleaseReplaced" && e.Detail.Contains("commitments=20->0"));
        Assert.Empty(next.Ledger.SettledSpending);
        Assert.Equal(20ul, next.Ledger.OutstandingCommitments[1]);
    }

    [Fact]
    public void WrongSessionTraceIsRejected()
        => Assert.Throws<ArgumentException>(() => MaterialAcquisitionPlanner.Plan(Input(), new AcquisitionTrace(Guid.NewGuid())));

    [Fact]
    public void DeserializationTruncationIsReportedAtLoadBoundary()
    {
        var json = "{\"CurrencyPriority\":[" + string.Join(",", Enumerable.Repeat("0", 100)) + "]}";
        var policy = JsonConvert.DeserializeObject<AutomaticVendorPurchasePolicy>(json)!;
        var trace = new AcquisitionTrace(Guid.NewGuid());
        policy.DrainLoadDiagnostics(trace, "Global");
        Assert.Contains(trace.Events, e => e.Action == "LoadDiagnosticsTruncated" && e.Level == AcquisitionTraceLevel.Warning);
        Assert.Contains(VendorCurrencyGroup.Gil, policy.CurrencyPriority);
        Assert.Equal(2, policy.CurrencyPriority.Count);
    }

    [Fact]
    public void LoadingTraceDoesNotConcludeShortagesOrAllocateFunds()
    {
        var source = Input();
        var input = new MaterialAcquisitionPlanningInput(source.SessionId, source.Policy, source.Materials, source.Offers,
            source.Inventory.ToDictionary(), source.CurrencyBalances.ToDictionary(), source.Ledger, VendorOfferDataStatus.Loading);
        var trace = new AcquisitionTrace(input.SessionId);
        MaterialAcquisitionPlanner.Plan(input, trace);
        Assert.Contains(trace.Events, e => e.Action == "Readiness" && e.Detail.Contains("shortageConclusionsDeferred=True"));
        Assert.DoesNotContain(trace.Events, e => e.Action == "Allocate" || e.Action == "EligibleBlocker");
        Assert.Contains("unresolvedTypes=deferred", trace.Summary);
    }
}
