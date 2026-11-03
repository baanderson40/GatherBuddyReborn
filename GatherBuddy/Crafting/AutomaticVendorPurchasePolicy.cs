using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.Serialization;
using GatherBuddy.Vulcan.Vendors;
using Newtonsoft.Json;

namespace GatherBuddy.Crafting;

public enum VendorPurchaseOverrideMode
{
    Inherit,
    Disabled,
    Custom,
}

public enum VendorPurchasePolicyOrigin
{
    Global,
    ListOverride,
    Disabled,
}

/// <summary>Persisted settings only. Currency balances and spending belong to the acquisition session.</summary>
public sealed class AutomaticVendorPurchasePolicy
{
    private IReadOnlyList<AcquisitionTraceEvent> _loadDiagnostics = Array.Empty<AcquisitionTraceEvent>();
    private long _omittedLoadDiagnostics;
    public bool Enabled { get; set; }
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<VendorCurrencyGroup> AllowedCurrencyGroups { get; set; } = [VendorCurrencyGroup.Gil, VendorCurrencyGroup.GrandCompanySeals];
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<VendorCurrencyGroup> CurrencyPriority { get; set; } = [VendorCurrencyGroup.Gil, VendorCurrencyGroup.GrandCompanySeals];
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public Dictionary<uint, ulong> CurrencyReservesByItemId { get; set; } = new();
    // Null only until legacy reserves have been normalized. Explicit zero must win.
    public ulong? GrandCompanySealReserve { get; set; }
    // Null means unlimited; zero forbids gil spending.
    public ulong? MaximumGilSpendPerRun { get; set; }

    [OnDeserialized]
    private void OnDeserialized(StreamingContext context)
    {
        var trace = new AcquisitionTrace(Guid.NewGuid(), 64);
        Normalize(trace);
        _loadDiagnostics = trace.Events;
        _omittedLoadDiagnostics = trace.DroppedEvents;
    }

    [OnSerializing]
    private void OnSerializing(StreamingContext context) => Normalize();

    public bool Normalize(AcquisitionTrace? trace = null, string path = "Policy")
    {
        TraceGroups(AllowedCurrencyGroups, "AllowedCurrencyGroups", trace, path);
        TraceGroups(CurrencyPriority, "CurrencyPriority", trace, path);
        if (CurrencyReservesByItemId == null)
            trace?.Record("P1", "Normalize", $"field={path}.CurrencyReservesByItemId action=InitializeMissingCollection");
        else if (CurrencyReservesByItemId.ContainsKey(0))
            trace?.Record("P1", "Normalize", $"field={path}.CurrencyReservesByItemId action=RemoveInvalidCurrencyId value=0", AcquisitionTraceLevel.Warning);
        var allowed = (AllowedCurrencyGroups ?? [VendorCurrencyGroup.Gil, VendorCurrencyGroup.GrandCompanySeals])
            .Where(group => Enum.IsDefined(group)).Distinct().ToList();
        var priority = (CurrencyPriority ?? [VendorCurrencyGroup.Gil, VendorCurrencyGroup.GrandCompanySeals])
            .Where(group => Enum.IsDefined(group)).Distinct().ToList();
        foreach (var group in allowed.OrderBy(group => group))
            if (!priority.Contains(group))
            {
                priority.Add(group);
                trace?.Record("P1", "Normalize", $"field={path}.CurrencyPriority action=AppendAllowedCurrency value={group}");
            }
        var legacy = (CurrencyReservesByItemId ?? new()).Where(pair => GrandCompanySealPolicy.IsSealCurrency(pair.Key)).ToList();
        var migrate = !GrandCompanySealReserve.HasValue;
        if (migrate)
            GrandCompanySealReserve = legacy.Count == 0 ? 0 : legacy.Max(pair => pair.Value);
        if (legacy.Count > 0)
            trace?.Record("P1", "MigrateSealReserve", $"path={path}; unifiedReserve={GrandCompanySealReserve}; source={(migrate ? "maximum legacy reserve" : "explicit unified reserve")}; removedLegacyEntries={legacy.Count}",
                legacy.Select(pair => pair.Value).Distinct().Count() > 1 ? AcquisitionTraceLevel.Warning : AcquisitionTraceLevel.Debug);
        var reserves = (CurrencyReservesByItemId ?? new()).Where(pair => pair.Key != 0 && !GrandCompanySealPolicy.IsSealCurrency(pair.Key)).ToDictionary();
        var changed = AllowedCurrencyGroups == null || !AllowedCurrencyGroups.SequenceEqual(allowed)
            || CurrencyPriority == null || !CurrencyPriority.SequenceEqual(priority)
            || CurrencyReservesByItemId == null || CurrencyReservesByItemId.Count != reserves.Count || migrate;
        AllowedCurrencyGroups = allowed;
        CurrencyPriority = priority;
        CurrencyReservesByItemId = reserves;
        return changed;
    }

    public void DrainLoadDiagnostics(AcquisitionTrace trace, string path)
    {
        foreach (var entry in _loadDiagnostics)
            trace.Record(entry.Phase, entry.Action, $"path={path}; {entry.Detail}", entry.Level);
        if (_omittedLoadDiagnostics > 0)
            trace.Record("P1", "LoadDiagnosticsTruncated", $"path={path}; omitted={_omittedLoadDiagnostics}", AcquisitionTraceLevel.Warning);
        _loadDiagnostics = Array.Empty<AcquisitionTraceEvent>();
        _omittedLoadDiagnostics = 0;
    }

    private static void TraceGroups(List<VendorCurrencyGroup>? groups, string field, AcquisitionTrace? trace, string path)
    {
        if (trace == null) return;
        if (groups == null)
        {
            trace.Record("P1", "Normalize", $"field={path}.{field} action=InitializeMissingCollection");
            return;
        }
        var seen = new HashSet<VendorCurrencyGroup>();
        foreach (var group in groups)
            if (!Enum.IsDefined(group))
                trace.Record("P1", "Normalize", $"field={path}.{field} action=RemoveUnknownCurrency value={(int)group}", AcquisitionTraceLevel.Warning);
            else if (!seen.Add(group))
                trace.Record("P1", "Normalize", $"field={path}.{field} action=RemoveDuplicate value={group}", AcquisitionTraceLevel.Warning);
    }

    public AutomaticVendorPurchasePolicy Clone(AcquisitionTrace? trace = null, string path = "Policy")
    {
        var copy = new AutomaticVendorPurchasePolicy
        {
            Enabled = Enabled,
            AllowedCurrencyGroups = AllowedCurrencyGroups == null ? null! : new(AllowedCurrencyGroups),
            CurrencyPriority = CurrencyPriority == null ? null! : new(CurrencyPriority),
            CurrencyReservesByItemId = CurrencyReservesByItemId == null ? null! : new(CurrencyReservesByItemId),
            GrandCompanySealReserve = GrandCompanySealReserve,
            MaximumGilSpendPerRun = MaximumGilSpendPerRun,
        };
        copy.Normalize(trace, path);
        return copy;
    }
}

public sealed class VendorPurchaseListSettings
{
    private IReadOnlyList<AcquisitionTraceEvent> _loadDiagnostics = Array.Empty<AcquisitionTraceEvent>();
    private long _omittedLoadDiagnostics;
    public VendorPurchaseOverrideMode Mode { get; set; }
    public AutomaticVendorPurchasePolicy CustomPolicy { get; set; } = new();

    [OnDeserialized]
    private void OnDeserialized(StreamingContext context)
    {
        var trace = new AcquisitionTrace(Guid.NewGuid(), 64);
        Normalize(trace);
        _loadDiagnostics = trace.Events;
        _omittedLoadDiagnostics = trace.DroppedEvents;
    }

    public bool Normalize(AcquisitionTrace? trace = null, string path = "ListPolicy")
    {
        var changed = false;
        // Fail closed for corrupt/future modes rather than inheriting permission to spend.
        if (!Enum.IsDefined(Mode))
        {
            trace?.Record("P1", "Normalize", $"field={path}.Mode action=DisableInvalidOverride value={(int)Mode}", AcquisitionTraceLevel.Warning);
            Mode = VendorPurchaseOverrideMode.Disabled;
            changed = true;
        }
        if (CustomPolicy == null)
        {
            trace?.Record("P1", "Normalize", $"field={path}.CustomPolicy action=InitializeMissingPolicy");
            CustomPolicy = new();
            changed = true;
        }
        return CustomPolicy.Normalize(trace, $"{path}.CustomPolicy") || changed;
    }

    public void DrainLoadDiagnostics(AcquisitionTrace trace, string path)
    {
        foreach (var entry in _loadDiagnostics)
            trace.Record(entry.Phase, entry.Action, $"path={path}; {entry.Detail}", entry.Level);
        if (_omittedLoadDiagnostics > 0)
            trace.Record("P1", "LoadDiagnosticsTruncated", $"path={path}; omitted={_omittedLoadDiagnostics}", AcquisitionTraceLevel.Warning);
        _loadDiagnostics = Array.Empty<AcquisitionTraceEvent>();
        _omittedLoadDiagnostics = 0;
        CustomPolicy?.DrainLoadDiagnostics(trace, $"{path}.CustomPolicy");
    }

    public VendorPurchaseListSettings Clone(AcquisitionTrace? trace = null)
    {
        var copy = new VendorPurchaseListSettings { Mode = Mode, CustomPolicy = CustomPolicy?.Clone(trace, "ListPolicy.CustomPolicy") ?? null! };
        copy.Normalize(trace);
        return copy;
    }
}

/// <summary>Immutable, detached policy for one run. Retrying uses the same snapshot.</summary>
public sealed class ResolvedVendorPurchasePolicy
{
    public bool Enabled { get; }
    public IReadOnlyList<VendorCurrencyGroup> AllowedCurrencyGroups { get; }
    public IReadOnlyList<VendorCurrencyGroup> CurrencyPriority { get; }
    public IReadOnlyDictionary<uint, ulong> CurrencyReservesByItemId { get; }
    public ulong GrandCompanySealReserve { get; }
    public ulong GetReserve(uint currencyItemId) => GrandCompanySealPolicy.IsSealCurrency(currencyItemId)
        ? GrandCompanySealReserve : CurrencyReservesByItemId.GetValueOrDefault(currencyItemId);
    public ulong? MaximumGilSpendPerRun { get; }
    public VendorPurchasePolicyOrigin Origin { get; }

    private ResolvedVendorPurchasePolicy(AutomaticVendorPurchasePolicy policy, VendorPurchasePolicyOrigin origin)
    {
        Enabled = origin != VendorPurchasePolicyOrigin.Disabled && policy.Enabled;
        AllowedCurrencyGroups = policy.AllowedCurrencyGroups.AsReadOnly();
        CurrencyPriority = policy.CurrencyPriority.AsReadOnly();
        CurrencyReservesByItemId = new ReadOnlyDictionary<uint, ulong>(policy.CurrencyReservesByItemId);
        GrandCompanySealReserve = policy.GrandCompanySealReserve ?? 0;
        MaximumGilSpendPerRun = policy.MaximumGilSpendPerRun;
        Origin = origin;
    }

    public static ResolvedVendorPurchasePolicy Resolve(AutomaticVendorPurchasePolicy? global, VendorPurchaseListSettings? list = null,
        AcquisitionTrace? trace = null, bool temporary = false)
    {
        var settings = list?.Clone(trace);
        var origin = settings?.Mode switch
        {
            VendorPurchaseOverrideMode.Disabled => VendorPurchasePolicyOrigin.Disabled,
            VendorPurchaseOverrideMode.Custom => VendorPurchasePolicyOrigin.ListOverride,
            _ => VendorPurchasePolicyOrigin.Global,
        };
        var policy = origin == VendorPurchasePolicyOrigin.ListOverride
            ? settings!.CustomPolicy.Clone(trace, "SelectedPolicy")
            : global?.Clone(trace, "GlobalPolicy") ?? new();
        var resolved = new ResolvedVendorPurchasePolicy(policy, origin);
        trace?.Record("P1", "Resolve", $"temporary={temporary}; globalEnabled={global?.Enabled ?? false}; listMode={settings?.Mode.ToString() ?? "Inherit (no list)"}; origin={origin}; enabled={resolved.Enabled}");
        trace?.Record("P1", "Snapshot", $"detached=True; allowed={string.Join(",", resolved.AllowedCurrencyGroups)}; priority={string.Join("->", resolved.CurrencyPriority)}; gilCap={resolved.MaximumGilSpendPerRun?.ToString() ?? "unlimited"}");
        if (trace != null)
        {
            trace.Record("P1", "Reserve", $"currency=current-GC seals; protected={resolved.GrandCompanySealReserve}");
            foreach (var reserve in resolved.CurrencyReservesByItemId.OrderBy(pair => pair.Key))
                trace.Record("P1", "Reserve", $"currency={reserve.Key}; protected={reserve.Value}");
        }
        return resolved;
    }

    public bool Allows(VendorCurrencyGroup group) => Enabled && AllowedCurrencyGroups.Contains(group);
}

public static class GrandCompanySealPolicy
{
    public static bool IsSealCurrency(uint currencyItemId) => currencyItemId is 20 or 21 or 22;
    public static bool IsCurrentCurrency(uint currencyItemId, uint currentCurrencyItemId)
        => IsSealCurrency(currencyItemId) && currencyItemId == currentCurrencyItemId;
}
