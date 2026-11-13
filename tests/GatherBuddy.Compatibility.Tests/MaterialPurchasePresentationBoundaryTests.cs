using GatherBuddy.Crafting;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;

namespace GatherBuddy.Compatibility.Tests;

public class MaterialPurchasePresentationBoundaryTests
{
    private static string[] Calls(string typeName, string methodName)
    {
        using var file = File.OpenRead(typeof(CraftingQueueProgress).Assembly.Location);
        using var pe = new PEReader(file);
        var metadata = pe.GetMetadataReader();
        var type = Assert.Single(metadata.TypeDefinitions, h => metadata.GetString(metadata.GetTypeDefinition(h).Name) == typeName);
        var method = Assert.Single(metadata.GetTypeDefinition(type).GetMethods(), h => metadata.GetString(metadata.GetMethodDefinition(h).Name) == methodName);
        return TeleportNativeBoundaryTests.Calls(pe, metadata, method).ToArray();
    }

    [Fact]
    public void MaterialsRemovesLocalVendorPreferenceAndUsesPolicyContext()
    {
        using var file = File.OpenRead(typeof(CraftingQueueProgress).Assembly.Location);
        using var pe = new PEReader(file);
        var metadata = pe.GetMetadataReader();
        var type = Assert.Single(metadata.TypeDefinitions, h => metadata.GetString(metadata.GetTypeDefinition(h).Name) == "CraftingMaterialsWindow");
        var fields = metadata.GetTypeDefinition(type).GetFields().Select(h => metadata.GetString(metadata.GetFieldDefinition(h).Name)).ToArray();
        Assert.DoesNotContain("_matsPreferVendors", fields);
        Assert.DoesNotContain("_cachedMaterialViewPreferVendors", fields);
        Assert.Contains("GetMaterialPurchaseDisplayContext", Calls("CraftingMaterialsWindow", "Draw"));
    }

    [Fact]
    public void AcquisitionAndMaterialsShareMaterialOfferAdapter()
    {
        Assert.Contains("Capture", Calls("CraftingAcquisitionPort", "Capture"));
        Assert.Contains("Capture", Calls("CraftingMaterialsWindow", "RebuildMaterialView"));
        // Filtering happens in the shared projection's compiled lambda, not duplicated in either caller.
        Assert.DoesNotContain("get_StackSize", Calls("CraftingAcquisitionPort", "Capture"));
        Assert.DoesNotContain("get_StackSize", Calls("CraftingMaterialsWindow", "RebuildMaterialView"));
        Assert.Contains("IsValid", Calls("MaterialPurchasePresentation", "Classify").Concat(LambdaCalls("MaterialPurchasePresentation")));
    }

    private static IEnumerable<string> LambdaCalls(string owner)
    {
        using var file = File.OpenRead(typeof(CraftingQueueProgress).Assembly.Location);
        using var pe = new PEReader(file);
        var metadata = pe.GetMetadataReader();
        var type = Assert.Single(metadata.TypeDefinitions, h => metadata.GetString(metadata.GetTypeDefinition(h).Name) == owner);
        return metadata.GetTypeDefinition(type).GetNestedTypes()
            .SelectMany(h => metadata.GetTypeDefinition(h).GetMethods())
            .SelectMany(h => TeleportNativeBoundaryTests.Calls(pe, metadata, h)).ToArray();
    }

    [Fact]
    public void ContextUsesMatchingRunAndChecksQueueLifecycleWithoutSavingSettings()
    {
        var calls = Calls("CraftingListEditor", "GetMaterialPurchaseDisplayContext");
        Assert.Contains("GetActiveExecutionPlan", calls);
        Assert.Contains("get_CurrentState", calls);
        Assert.Contains("get_PurchasePolicy", calls);
        Assert.DoesNotContain("Save", calls);
        Assert.DoesNotContain("RefreshFromCurrentInventory", calls);
    }

    [Fact]
    public void ViewInvalidatesForPolicyVendorPublicationGcAndPlayerContext()
    {
        var calls = Calls("CraftingMaterialsWindow", "ShouldRebuildMaterialView");
        Assert.Contains("get_CacheKey", calls);
        Assert.Contains("get_Revision", calls);
        Assert.Contains("GetDisplaySealCurrency", calls);
        Assert.Contains("get_ContentId", calls);
        Assert.Contains("CanReadPurchaseOffers", calls);
        Assert.Contains("get_UtcNow", calls);
    }

    [Fact]
    public void RebuildReadsRequirementsWithoutStartingAutomationOrAllocatingSpending()
    {
        var calls = Calls("CraftingMaterialsWindow", "RebuildMaterialView");
        Assert.Contains("GetCachedMaterials", calls);
        Assert.Contains("GetDisplayIngredientDemands", calls);
        Assert.Contains("ToLookup", calls);
        Assert.DoesNotContain("StartForCrafting", calls);
        Assert.DoesNotContain("StartPurchases", calls);
        Assert.DoesNotContain("Save", calls);
        Assert.DoesNotContain("Plan", calls);
        Assert.DoesNotContain("Resolve", calls); // No live currency balance resolution.
    }
}
