namespace GatherBuddy.Crafting;

internal static class CraftableMaterialAvailability
{
    internal static bool CanSatisfy(
        IngredientQualityDemand demand,
        int inventoryNq,
        int inventoryHq,
        int retainerNq,
        int retainerHq)
    {
        demand = demand.ConsumeSplit(inventoryNq, inventoryHq, out _, out _);
        demand = demand.ConsumeSplit(retainerNq, retainerHq, out _, out _);
        return demand.Total == 0;
    }
}
