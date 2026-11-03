using Lumina.Data;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace GatherBuddy.Vulcan.Vendors;

/// <summary>Preserves the script union's first-existing-sheet order without runtime type hashes.</summary>
public static class CustomTalkScriptLookup
{
    public static RowRef Resolve(ExcelModule excel, uint rowId, Language language)
    {
        // Raw sheets are cached by ExcelModule, avoiding typed-wrapper allocation per script lookup.
        // Like Lumina's union interval lookup, this only checks row existence, not column schemas.
        if (excel.GetRawSheet(nameof(FateShop), language).HasRow(rowId)) return RowRef.Create<FateShop>(excel, rowId, language);
        if (excel.GetRawSheet(nameof(GilShop), language).HasRow(rowId)) return RowRef.Create<GilShop>(excel, rowId, language);
        if (excel.GetRawSheet(nameof(SpecialShop), language).HasRow(rowId)) return RowRef.Create<SpecialShop>(excel, rowId, language);
        if (excel.GetRawSheet(nameof(GCShop), language).HasRow(rowId)) return RowRef.Create<GCShop>(excel, rowId, language);
        if (excel.GetRawSheet(nameof(FccShop), language).HasRow(rowId)) return RowRef.Create<FccShop>(excel, rowId, language);
        if (excel.GetRawSheet(nameof(InclusionShop), language).HasRow(rowId)) return RowRef.Create<InclusionShop>(excel, rowId, language);
        if (excel.GetRawSheet(nameof(CollectablesShop), language).HasRow(rowId)) return RowRef.Create<CollectablesShop>(excel, rowId, language);
        if (excel.GetRawSheet(nameof(TopicSelect), language).HasRow(rowId)) return RowRef.Create<TopicSelect>(excel, rowId, language);
        if (excel.GetRawSheet(nameof(PreHandler), language).HasRow(rowId)) return RowRef.Create<PreHandler>(excel, rowId, language);
        if (excel.GetRawSheet(nameof(CustomTalk), language).HasRow(rowId)) return RowRef.Create<CustomTalk>(excel, rowId, language);
        return RowRef.CreateUntyped(rowId, language);
    }
}
