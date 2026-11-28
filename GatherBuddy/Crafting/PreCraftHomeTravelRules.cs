using System;

namespace GatherBuddy.Crafting;

public static class PreCraftHomeTravelRules
{
    public const int PreferenceConfigVersion = 18;

    private static readonly uint[] CompanyWorkshopTerritories = [423, 424, 425, 653, 984];

    public static bool IsCompanyWorkshop(uint territoryId)
        => Array.IndexOf(CompanyWorkshopTerritories, territoryId) >= 0;

    public static bool IsIndoorFastPath(bool insideHousing, uint territoryId)
        => insideHousing || IsCompanyWorkshop(territoryId);

    public static bool MigratePreference(int configVersion, bool legacyGoHomeWhenIdle, bool currentValue)
        => configVersion < PreferenceConfigVersion ? legacyGoHomeWhenIdle : currentValue;
}
