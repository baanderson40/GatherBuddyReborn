using Lumina.Excel.Sheets;
using Xunit;
using GatherBuddy.Vulcan.Vendors;
using GatherBuddy.Crafting;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace GatherBuddy.Compatibility.Tests;

public class GemstoneSchemaTests
{
    [Fact]
    public void ListingQuestReferenceMustBeUnderstoodBeforeNativeQuestChecks()
    {
        var property = typeof(SpecialShop.ItemStruct).GetProperty("Quest")!;
        Assert.Equal(typeof(Quest), property.PropertyType.GenericTypeArguments.Single());
    }

    [Fact]
    public void ActualDefaultLuminaCostGetterReproducesInitializationFailure()
    {
        var rows = Array.Empty<SpecialShop.ItemStruct.ItemCostsStruct>();
        Assert.Throws<NullReferenceException>(() => rows.FirstOrDefault().CurrencyCost);
    }

    [Fact]
    public void ProductionGemstoneLookupDoesNotReadAnUnmatchedDefaultLuminaRow()
    {
        Assert.False(VendorShopResolver.TryFindGemstoneCost(Array.Empty<SpecialShop.ItemStruct.ItemCostsStruct>(),
            _ => throw new InvalidOperationException("Empty listing must not resolve any currency."), out var amount));
        Assert.Equal(0u, amount);
    }

    [Fact]
    public void VendorViewStampChangesForRecoveryAndNewGenerationButNotStableFrames()
    {
        var pending = new VendorShopResolver.CacheStamp(0, false);
        var ready = new VendorShopResolver.CacheStamp(1, true);
        Assert.NotEqual(pending, ready);
        Assert.NotEqual(ready, new VendorShopResolver.CacheStamp(2, true));
        Assert.NotEqual(ready, new VendorShopResolver.CacheStamp(1, false));
        Assert.Equal(ready, new VendorShopResolver.CacheStamp(1, true));
    }

    [Theory]
    [InlineData("ShouldRebuildMaterialView")]
    [InlineData("RebuildMaterialView")]
    public void ActualMaterialsWindowChecksAndCapturesVendorGeneration(string name)
    {
        using var file = File.OpenRead(typeof(CraftingQueueProgress).Assembly.Location);
        using var pe = new PEReader(file);
        var metadata = pe.GetMetadataReader();
        var type = Assert.Single(metadata.TypeDefinitions, h => metadata.GetString(metadata.GetTypeDefinition(h).Name) == "CraftingMaterialsWindow");
        var method = Assert.Single(metadata.GetTypeDefinition(type).GetMethods(), h => metadata.GetString(metadata.GetMethodDefinition(h).Name) == name);
        Assert.Contains("get_ViewStamp", TeleportNativeBoundaryTests.Calls(pe, metadata, method));
    }

    [Fact]
    public void ViewMetadataAcceptsCapturedStampRatherThanResamplingAtEnd()
    {
        using var file = File.OpenRead(typeof(CraftingQueueProgress).Assembly.Location);
        using var pe = new PEReader(file);
        var metadata = pe.GetMetadataReader();
        var type = Assert.Single(metadata.TypeDefinitions, h => metadata.GetString(metadata.GetTypeDefinition(h).Name) == "CraftingMaterialsWindow");
        var handle = Assert.Single(metadata.GetTypeDefinition(type).GetMethods(), h => metadata.GetString(metadata.GetMethodDefinition(h).Name) == "UpdateMaterialViewCacheMetadata");
        Assert.Contains(metadata.GetMethodDefinition(handle).GetParameters(), p => metadata.GetString(metadata.GetParameter(p).Name) == "vendorStamp");
        Assert.DoesNotContain("get_ViewStamp", TeleportNativeBoundaryTests.Calls(pe, metadata, handle));
    }

    [Theory]
    [InlineData("UpdatePurchasingDirectSpecialShopItem")]
    [InlineData("UpdateConfirmingPurchase")]
    public void ActualNativePurchasePhasesRetainExactValidationAndBoundedDiagnostics(string name)
    {
        using var file = File.OpenRead(typeof(CraftingQueueProgress).Assembly.Location);
        using var pe = new PEReader(file);
        var metadata = pe.GetMetadataReader();
        var type = Assert.Single(metadata.TypeDefinitions, h => metadata.GetString(metadata.GetTypeDefinition(h).Name) == "VendorPurchaseManager");
        var method = Assert.Single(metadata.GetTypeDefinition(type).GetMethods(), h => metadata.GetString(metadata.GetMethodDefinition(h).Name) == name);
        var calls = TeleportNativeBoundaryTests.Calls(pe, metadata, method).ToList();
        Assert.Contains("TryFindAuthorizedExchange", calls);
        Assert.Contains("LogExchangeValidation", calls);
        if (name == "UpdatePurchasingDirectSpecialShopItem")
        {
            Assert.Contains("ShouldWait", calls);
            Assert.True(calls.IndexOf("ShouldWait") < calls.IndexOf("TrySelectSpecialShopItem"));
        }
    }

    [Fact]
    public void NativeExchangeReaderUsesDetachedLivePriceMatcherWithoutPurchasingOrSheetPriceSubstitution()
    {
        using var file = File.OpenRead(typeof(CraftingQueueProgress).Assembly.Location);
        using var pe = new PEReader(file);
        var metadata = pe.GetMetadataReader();
        var type = Assert.Single(metadata.TypeDefinitions, h => metadata.GetString(metadata.GetTypeDefinition(h).Name) == "VendorInteractionHelper");
        var method = Assert.Single(metadata.GetTypeDefinition(type).GetMethods(), h => metadata.GetString(metadata.GetMethodDefinition(h).Name) == "TryFindAuthorizedExchange");
        var calls = TeleportNativeBoundaryTests.Calls(pe, metadata, method).ToArray();
        Assert.Contains("Read", calls);
        Assert.Contains("Match", calls);
        Assert.Contains("UniqueCurrencyId", calls);
        Assert.DoesNotContain("TrySelectSpecialShopItem", calls);
        Assert.DoesNotContain("TryConfirmPurchase", calls);
        Assert.DoesNotContain("ReadExchange", calls);
    }
}
