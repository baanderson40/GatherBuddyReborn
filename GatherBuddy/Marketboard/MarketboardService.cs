using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Lumina.Excel.Sheets;

namespace GatherBuddy.Marketboard;

public sealed class MarketboardService : IDisposable
{
    private const int MaxHistoryItems = 100;
    private const string HistoryFile = "mb_history.json";
    private sealed record PersistedEntry(uint ItemId, string Name, uint IconId);
    private readonly UniversalisService _universalis = new();
    private readonly MarketLookupCoordinator _lookups;
    private readonly List<uint> _history = new();
    private readonly Dictionary<uint, MarketCatalogItem> _catalog = new();
    private readonly Dictionary<uint, PersistedEntry> _legacy = new();
    private bool _catalogReady;
    private DateTime _nextCatalogAttempt;
    private bool _disposed;
    private bool _historyWritable = true;
    private MarketHome? _home;
    public string? CatalogError { get; private set; }
    public string? HistoryWarning { get; private set; }
    public MarketboardPreferences Preferences => GatherBuddy.Config.MarketboardPreferences ??= new();

    public MarketboardService()
    {
        _lookups = new MarketLookupCoordinator(_universalis, onFailure: (item, target, error) =>
            GatherBuddy.Log.Warning($"[Marketboard] Lookup failed for {item.Name} ({item.ItemId}), {target.Name}: {error.Message}"));
        LoadHistory();
    }

    // These entry points and the catalog run on the framework/UI thread. Only the
    // managed coordinator and HTTP adapter run in the background.
    private bool EnsureCatalog()
    {
        if (_catalogReady) return true;
        if (DateTime.UtcNow < _nextCatalogAttempt) return false;
        _nextCatalogAttempt = DateTime.UtcNow.AddSeconds(5);
        try
        {
            var sheet = Dalamud.GameData.GetExcelSheet<Item>();
            if (sheet == null) { CatalogError = "Item data is not available yet."; return false; }
            foreach (var item in sheet)
            {
                if (!MarketSearch.IsMarketable(item.IsUntradable, item.ItemSearchCategory.RowId)) continue;
                var name = item.Name.ExtractText();
                if (string.IsNullOrWhiteSpace(name)) continue;
                _catalog[item.RowId] = new(item.RowId, name, item.Icon, item.CanBeHq);
            }
            _catalogReady = true;
            CatalogError = null;
            return true;
        }
        catch (Exception ex)
        {
            CatalogError = "Could not load marketable items.";
            GatherBuddy.Log.Warning($"[Marketboard] Item index failed: {ex.Message}");
            return false;
        }
    }

    public IReadOnlyList<MarketCatalogItem> Search(string text)
        => EnsureCatalog() ? MarketSearch.Find(_catalog.Values, text) : Array.Empty<MarketCatalogItem>();

    public MarketCatalogItem? GetItem(uint itemId)
        => EnsureCatalog() && _catalog.TryGetValue(itemId, out var item) ? item : null;

    public string GetItemName(uint itemId)
        => GetItem(itemId)?.Name ?? (_legacy.TryGetValue(itemId, out var entry) ? entry.Name : $"Item #{itemId}");

    public uint GetItemIcon(uint itemId)
        => GetItem(itemId)?.IconId ?? (_legacy.TryGetValue(itemId, out var entry) ? entry.IconId : 0);

    public MarketHome? GetHome()
    {
        var worldId = Dalamud.Objects.LocalPlayer?.HomeWorld.RowId ?? 0;
        if (worldId == 0) { _home = null; return null; }
        if (_home?.WorldId == worldId) return _home;
        var sheet = Dalamud.GameData.GetExcelSheet<World>();
        if (sheet?.TryGetRow(worldId, out var world) != true) return null;
        var dc = world.DataCenter.ValueNullable?.Name.ExtractText();
        if (string.IsNullOrEmpty(dc)) return null;
        var worlds = sheet.Where(w => w.IsPublic && w.DataCenter.RowId == world.DataCenter.RowId)
            .Select(w => new MarketWorld(w.RowId, w.Name.ExtractText())).Where(w => !string.IsNullOrEmpty(w.Name)).ToArray();
        return _home = new(worldId, dc, worlds);
    }

    public MarketScope? GetSelectedScope()
    {
        var scope = MarketScope.Resolve(Preferences.ScopeId, GetHome());
        if (scope != null && Preferences.ScopeId != scope.Id) SelectScope(scope);
        return scope;
    }

    public void SelectScope(MarketScope scope)
    {
        Preferences.ScopeId = scope.Id;
        GatherBuddy.Config.Save();
    }

    public void QueueLookup(uint itemId, string itemName, uint iconId = 0)
    {
        if (_disposed) return;
        var item = GetItem(itemId);
        if (item == null) return; // UI explains non-marketable items; never queries API for them.
        Remember(item);
        var scope = GetSelectedScope();
        if (scope != null) _lookups.Request(item, scope);
    }

    public void Request(uint itemId, MarketScope scope, bool force = false, bool failedOnly = false)
    {
        if (_disposed || GetItem(itemId) is not { } item) return;
        Remember(item);
        _lookups.Request(item, scope, force, failedOnly);
    }

    public IReadOnlyList<MarketTargetStatus> Snapshot(uint itemId, MarketScope scope) => _lookups.Snapshot(itemId, scope);
    public IReadOnlyList<uint> GetHistorySnapshot() => _history.ToArray();

    private void Remember(MarketCatalogItem item)
    {
        _history.Remove(item.ItemId);
        _history.Insert(0, item.ItemId);
        if (_history.Count > MaxHistoryItems) _history.RemoveAt(_history.Count - 1);
        _legacy[item.ItemId] = new(item.ItemId, item.Name, item.IconId);
        SaveHistory();
    }

    public void RemoveFromHistory(uint itemId)
    {
        _history.Remove(itemId);
        _lookups.Forget(itemId);
        SaveHistory();
    }

    public void ClearHistory()
    {
        _historyWritable = true; // Explicitly confirmed Clear may replace unreadable history.
        HistoryWarning = null;
        _history.Clear();
        _lookups.Clear();
        SaveHistory();
    }

    private void LoadHistory()
    {
        try
        {
            var path = Path.Combine(Dalamud.PluginInterface.GetPluginConfigDirectory(), HistoryFile);
            if (!File.Exists(path)) return;
            var entries = JsonSerializer.Deserialize<List<PersistedEntry>>(File.ReadAllText(path));
            if (entries == null) return;
            foreach (var entry in entries)
            {
                if (entry.ItemId == 0 || _history.Contains(entry.ItemId) || _history.Count >= MaxHistoryItems) continue;
                _legacy[entry.ItemId] = entry with { Name = string.IsNullOrWhiteSpace(entry.Name) ? $"Item #{entry.ItemId}" : entry.Name };
                _history.Add(entry.ItemId);
            }
        }
        catch (Exception ex)
        {
            _historyWritable = false;
            HistoryWarning = "Existing history could not be read. Original file preserved; new history will not be saved unless you explicitly Clear History.";
            GatherBuddy.Log.Warning($"[Marketboard] Failed to load history: {ex.Message}");
        }
    }

    private void SaveHistory()
    {
        if (!_historyWritable) return;
        try
        {
            var entries = _history.Select(id => _legacy.TryGetValue(id, out var entry) ? entry : new PersistedEntry(id, GetItemName(id), GetItemIcon(id)));
            var path = Path.Combine(Dalamud.PluginInterface.GetPluginConfigDirectory(), HistoryFile);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(entries));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) { GatherBuddy.Log.Warning($"[Marketboard] Failed to save history: {ex.Message}"); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SaveHistory();
        _lookups.Dispose();
        _ = _lookups.WhenIdleAsync().ContinueWith(_ => _universalis.Dispose(), TaskScheduler.Default);
    }
}
