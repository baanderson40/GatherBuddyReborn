using GatherBuddy.Crafting;
using GatherBuddy.Vulcan.Vendors;
using Xunit;

namespace GatherBuddy.Acquisition.Tests;

public class DiagnosticTests
{
    private static MaterialAcquisitionPlanningInput Input(VendorOfferDataStatus status = VendorOfferDataStatus.Ready)
        => new(Guid.NewGuid(), ResolvedVendorPurchasePolicy.Resolve(new() { Enabled = true }),
            [new(100, IngredientQualityDemand.FromPreferNQ(5))],
            [new(100, 1, VendorCurrencyGroup.Gil, 10, 1, VendorShopType.GilShop, new(10, "Vendor\nName", 20)),
             new(100, 20, VendorCurrencyGroup.GrandCompanySeals, 5, 1, VendorShopType.GrandCompanySeals,
                 new(11, "Other GC", 21), Access: VendorOfferAccess.Inaccessible, RejectionReason: "Different Grand Company.")],
            new Dictionary<uint, AcquisitionInventory> { [100] = new(1, 0) },
            new Dictionary<uint, ulong> { [1] = 20, [20] = 100 },
            new(new Dictionary<uint, ulong>(), new Dictionary<uint, ulong>()), status);

    [Fact]
    public void ReportIncludesInventoryBalancesOffersAllocationsAndShortages()
    {
        var input = Input();
        var plan = MaterialAcquisitionPlanner.Plan(input);
        var report = string.Join("\n", MaterialAcquisitionDiagnostics.Format(input, plan, includeFullOfferDiagnostics: true));
        Assert.Contains("READ ONLY", report);
        Assert.Contains("no retainer withdrawal simulated", report);
        Assert.Contains("balance=20", report);
        Assert.Contains("ownedNQ=1", report);
        Assert.Contains("Different Grand Company.", report);
        Assert.Contains("ALLOCATION Item [100]: quantity=2", report);
        Assert.Contains("unresolved=2", report);
        Assert.Contains("SHORTAGE", report);
        Assert.Empty(input.Ledger.OutstandingCommitments);
        Assert.Equal(20ul, input.CurrencyBalances[1]);
    }

    [Fact]
    public void NamesAndRejectionTextCannotInjectReportLines()
    {
        var input = Input();
        var lines = MaterialAcquisitionDiagnostics.Format(input, MaterialAcquisitionPlanner.Plan(input), _ => "Item\r\nName");
        Assert.All(lines, line =>
        {
            Assert.DoesNotContain("\n", line);
            Assert.DoesNotContain("\r", line);
        });
        Assert.Contains(lines, line => line.Contains("Vendor Name"));
    }

    [Fact]
    public void LoadingReportRequestsAnotherPreviewWithoutFalseShortages()
    {
        var input = Input(VendorOfferDataStatus.Loading);
        var lines = MaterialAcquisitionDiagnostics.Format(input, MaterialAcquisitionPlanner.Plan(input));
        Assert.Contains(lines, line => line.StartsWith("WAIT:"));
        Assert.DoesNotContain(lines, line => line.StartsWith("SHORTAGE"));
    }

    [Fact]
    public void ReportRejectsUnrelatedSession()
    {
        var input = Input();
        var other = Input();
        Assert.Throws<ArgumentException>(() => MaterialAcquisitionDiagnostics.Format(input, MaterialAcquisitionPlanner.Plan(other)));
    }

    [Fact]
    public void CompactIsDefaultAndShowsOnlyPrimaryShortageWithoutOfferSpam()
    {
        var input = Input();
        var plan = MaterialAcquisitionPlanner.Plan(input);
        var lines = MaterialAcquisitionDiagnostics.Format(input, plan);
        Assert.DoesNotContain(lines, line => line.StartsWith("OFFER ") || line.StartsWith("REQUIREMENT "));
        Assert.Single(lines, line => line.StartsWith("SHORT "));
        Assert.Contains(lines, line => line.StartsWith("SHORT ") && line.Contains("InsufficientCurrency"));
        Assert.DoesNotContain(lines, line => line.StartsWith("SHORT ") && line.Contains("VendorInaccessible"));
        Assert.Contains(lines, line => line.StartsWith("OFFERS ") && line.Contains("VendorInaccessible=1"));
        Assert.Contains(lines, line => line.StartsWith("BUY ") && line.Contains("access=not verified"));
        Assert.DoesNotContain(lines, line => line.Contains("requiredHQ="));
        Assert.Contains(lines, line => line.StartsWith("SUMMARY:") && line.Contains("unresolved=1 item types"));
    }

    [Fact]
    public void CompactHidesSatisfiedMaterialsAndUnusedCurrenciesButFullKeepsThem()
    {
        var source = Input();
        var input = new MaterialAcquisitionPlanningInput(source.SessionId, source.Policy,
            source.Materials.Concat([new AcquisitionMaterialDemand(200, IngredientQualityDemand.FromPreferNQ(2))]),
            source.Offers, new Dictionary<uint, AcquisitionInventory> { [100] = new(1, 0), [200] = new(2, 0) },
            source.CurrencyBalances.ToDictionary(), source.Ledger);
        var plan = MaterialAcquisitionPlanner.Plan(input);
        var compact = string.Join("\n", MaterialAcquisitionDiagnostics.Format(input, plan));
        var full = string.Join("\n", MaterialAcquisitionDiagnostics.Format(input, plan, includeFullOfferDiagnostics: true));
        Assert.DoesNotContain("Item [200]", compact);
        Assert.DoesNotContain("CURRENCY Item [20]", compact);
        Assert.Contains("satisfied=1 item types hidden", compact);
        Assert.Contains("Item [200]", full);
        Assert.Contains("CURRENCY Item [20]", full);
    }

    [Fact]
    public void GroupedCountsAreOffersNotDistinctNpcs()
    {
        var source = Input();
        var rejected = source.Offers[1] with { RejectionKind = AcquisitionOfferRejectionKind.WrongGrandCompany };
        var input = new MaterialAcquisitionPlanningInput(source.SessionId, source.Policy, source.Materials,
            source.Offers.Take(1).Concat([rejected, rejected]), source.Inventory.ToDictionary(), source.CurrencyBalances.ToDictionary(), source.Ledger);
        var lines = MaterialAcquisitionDiagnostics.Format(input, MaterialAcquisitionPlanner.Plan(input));
        Assert.Contains(lines, line => line.StartsWith("OFFERS ") && line.Contains("WrongGrandCompany=2"));
    }

    [Fact]
    public void FormattingModesNeverChangeAllocationsOrSpending()
    {
        var input = Input();
        var plan = MaterialAcquisitionPlanner.Plan(input);
        var before = plan.Allocations.ToArray();
        var spending = plan.ProjectedSpending.ToArray();
        MaterialAcquisitionDiagnostics.Format(input, plan);
        MaterialAcquisitionDiagnostics.Format(input, plan, includeFullOfferDiagnostics: true);
        Assert.Equal(before, plan.Allocations);
        Assert.Equal(spending, plan.ProjectedSpending);
        Assert.Empty(input.Ledger.OutstandingCommitments);
    }

    [Fact]
    public void CompactLoadingReportDoesNotCallPendingDemandUnresolved()
    {
        var input = Input(VendorOfferDataStatus.Loading);
        var lines = MaterialAcquisitionDiagnostics.Format(input, MaterialAcquisitionPlanner.Plan(input));
        Assert.Contains(lines, line => line.StartsWith("PENDING "));
        Assert.Contains(lines, line => line.StartsWith("SUMMARY:") && line.Contains("unresolved=pending"));
        Assert.DoesNotContain(lines, line => line.StartsWith("SHORT "));
    }
}
