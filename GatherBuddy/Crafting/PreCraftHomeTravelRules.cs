using System;

namespace GatherBuddy.Crafting;

public static class PreCraftHomeTravelRules
{
    public const int PreferenceConfigVersion = 18;
    public const uint InnIntendedUseId = 2;

    private static readonly uint[] CompanyWorkshopTerritories = [423, 424, 425, 653, 984];

    public static bool IsCompanyWorkshop(uint territoryId)
        => Array.IndexOf(CompanyWorkshopTerritories, territoryId) >= 0;

    public static bool ShouldSkipInitialTravelForNearbyBell(bool bellAvailable, bool travelSubmitted)
        => bellAvailable && !travelSubmitted;

    public static bool IsIndoorFastPath(bool insideHousing, uint territoryId, uint intendedUseId = 0)
        => insideHousing || IsCompanyWorkshop(territoryId) || intendedUseId == InnIntendedUseId;

    public static bool MigratePreference(int configVersion, bool legacyGoHomeWhenIdle, bool currentValue)
        => configVersion < PreferenceConfigVersion ? legacyGoHomeWhenIdle : currentValue;
}
