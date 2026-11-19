using GatherBuddy.Interfaces;
using GatherBuddy.Plugin;
using GatherBuddy.AutoGather.Extensions;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using Functions = GatherBuddy.Plugin.Functions;

namespace GatherBuddy.AutoGather.Lists;

public class ManualOrderSortMode : ISortMode<AutoGatherList>
{
    public IEnumerable<FileSystem<AutoGatherList>.IPath> GetChildren(FileSystem<AutoGatherList>.Folder folder)
    {
        var leaves = new List<FileSystem<AutoGatherList>.Leaf>(folder.GetLeaves());
        leaves.Sort(static (lhs, rhs) =>
        {
            var order = lhs.Value.Order.CompareTo(rhs.Value.Order);
            return order != 0 ? order : StringComparer.OrdinalIgnoreCase.Compare(lhs.Name, rhs.Name);
        });
        var children = new List<FileSystem<AutoGatherList>.IPath>(folder.GetSubFolders());
        children.AddRange(leaves);
        return children;
    }
}

public partial class AutoGatherListsManager : IDisposable
{
    public event Action? ActiveItemsChanged;

    private const string FileName = "auto_gather_lists.json";
    private const string FileNameFallback = "gather_window.json";

    private readonly FileSystem<AutoGatherList> _fileSystem;
    private readonly List<(IGatherable Item, uint Quantity)> _activeItems = [];
    private readonly List<(IGatherable Item, uint Quantity)> _fallbackItems = [];
    private readonly Dictionary<(IGatherable Item, bool Fallback), GatherInventoryDemand> _inventoryDemands = [];
    public static ManualOrderSortMode SortMode { get; } = new();

    public FileSystem<AutoGatherList> FileSystem
        => _fileSystem;

    public IEnumerable<AutoGatherList> Lists
        => _fileSystem.Values;

    public ReadOnlyCollection<(IGatherable Item, uint Quantity)> ActiveItems
        => _activeItems.AsReadOnly();

    public ReadOnlyCollection<(IGatherable Item, uint Quantity)> FallbackItems
        => _fallbackItems.AsReadOnly();

    internal int GetRemainingDemand(IGatherable item, bool fallback = false)
    {
        return GetInventoryDemandProgress(item, fallback)?.Remaining ?? 0;
    }

    internal bool NeedsGathering(IGatherable item, bool fallback = false)
        => GetRemainingDemand(item, fallback) > 0;

    internal GatherInventoryDemandProgress? GetInventoryDemandProgress(IGatherable item, bool fallback = false)
    {
        if (!_inventoryDemands.TryGetValue((item, fallback), out var demand))
            return null;

        var retainerAvailable = GatherableExtensions.CanReadRetainerInventory;
        var playerInventory = item.GetInventoryCount();
        var retainerInventory = retainerAvailable ? item.GetRetainerInventoryCount() : 0;
        return demand.GetProgress(playerInventory, retainerInventory, retainerAvailable);
    }

    internal int GetListTotalCount(AutoGatherList list, IGatherable item)
        => item.GetTotalCount(list.UsesRetainerInventory && !list.IgnoreRetainerInventory);

    public AutoGatherListsManager()
    {
        _fileSystem = new FileSystem<AutoGatherList>();
        _fileSystem.Changed += OnFileSystemChanged;
    }

    private AutoGatherListsManager(AutoGatherList.Config[] configs)
    {
        _fileSystem = new FileSystem<AutoGatherList>();
        var change = false;

        foreach (var cfg in configs)
        {
            change |= AutoGatherList.FromConfig(cfg, out var list);

            var folderPath = string.IsNullOrEmpty(list.FolderPath) ? string.Empty : list.FolderPath;

            if (folderPath == list.Name)
            {
                folderPath = string.Empty;
                change = true;
            }

            var folderNames = folderPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

            var folder = _fileSystem.Root;
            foreach (var folderName in folderNames)
            {
                (folder, _) = _fileSystem.FindOrCreateFolder(folder, folderName);
            }

            try
            {
                _fileSystem.CreateLeaf(folder, list.Name, list);
            }
            catch
            {
                _fileSystem.CreateDuplicateLeaf(folder, list.Name, list);
                change = true;
            }
        }

        if (change)
        {
            Save();
        }

        _fileSystem.Changed += OnFileSystemChanged;
        SetActiveItems();
    }

    private void OnFileSystemChanged(FileSystemChangeType type, FileSystem<AutoGatherList>.IPath changedObject, FileSystem<AutoGatherList>.IPath? previousParent, FileSystem<AutoGatherList>.IPath? newParent)
    {
        // Not renumbering the source folder on ObjectRemoved or ObjectMoved makes the numbering sparse, but it's fine for ordering.
        if (type is FileSystemChangeType.ObjectMoved or FileSystemChangeType.LeafAdded && changedObject is FileSystem<AutoGatherList>.Leaf newLeaf)
        {
            int? maxOrder = null;
            foreach (var leaf in newLeaf.Parent.GetLeaves())
            {
                if (leaf != newLeaf && (maxOrder == null || leaf.Value.Order > maxOrder.Value))
                {
                    maxOrder = leaf.Value.Order;
                }
            }

            newLeaf.Value.Order = (maxOrder ?? 0) + 1;
            Save();
        }
    }

    public void Dispose()
    { }

    public void SetActiveItems(bool removeCompletedItems = false)
    {
        if (removeCompletedItems && RemoveCompletedItemsFromEnabledLists())
        {
            Save();
        }

        _activeItems.Clear();
        _fallbackItems.Clear();
        _inventoryDemands.Clear();

        var totals = new Dictionary<(IGatherable Item, bool Fallback), (long Quantity, GatherInventoryDemand Demand)>();
        foreach (var path in _fileSystem.Root.GetAllDescendants(SortMode))
        {
            if (path is not FileSystem<AutoGatherList>.Leaf { Value: { Enabled: true } list })
            {
                continue;
            }

            foreach (var listItem in list.Items)
            {
                var listQuantity = list.Quantities[listItem];
                if (!list.EnabledItems[listItem])
                {
                    continue;
                }

                var useRetainerInventory = list.UsesRetainerInventory && !list.IgnoreRetainerInventory;
                ref var total = ref CollectionsMarshal.GetValueRefOrAddDefault(totals, (listItem, list.Fallback), out var exists);
                if (exists)
                {
                    total.Quantity = Math.Min(uint.MaxValue, total.Quantity + listQuantity);
                    total.Demand.Add(listQuantity, useRetainerInventory);
                }
                else
                {
                    var demand = new GatherInventoryDemand();
                    demand.Add(listQuantity, useRetainerInventory);
                    total = (listQuantity, demand);
                }
            }
        }

        foreach (var ((item, fallback), (totalQuantity, demand)) in totals)
        {
            var quantity = (uint)Math.Min(totalQuantity, uint.MaxValue);
            _inventoryDemands[(item, fallback)] = demand;
            if (fallback)
            {
                _fallbackItems.Add((item, quantity));
            }
            else
            {
                _activeItems.Add((item, quantity));
            }
        }

        ActiveItemsChanged?.Invoke();
    }

    public void Save()
    {
        var file = Functions.ObtainSaveFile(FileName);
        if (file == null)
        {
            GatherBuddy.Log.Error("Failed to obtain save file for auto-gather lists");
            return;
        }

        try
        {
            List<AutoGatherList> allLists = [.. _fileSystem.Values];
            foreach (var list in allLists)
            {
                if (_fileSystem.TryGetValue(list, out var leaf))
                {
                    list.FolderPath = leaf.Parent.IsRoot ? string.Empty : leaf.Parent.FullName();
                }
            }

            var text = JsonConvert.SerializeObject(allLists.ConvertAll(p => new AutoGatherList.Config(p)), Formatting.Indented);
            File.WriteAllText(file.FullName, text);
        }
        catch (Exception e)
        {
            GatherBuddy.Log.Error($"Error serializing auto-gather lists data:\n{e}");
        }
    }

    public static AutoGatherListsManager Load()
    {
        var file = Functions.ObtainSaveFile(FileName);
        if (file is not { Exists: true })
        {
            file = Functions.ObtainSaveFile(FileNameFallback);
        }

        if (file is { Exists: true })
        {
            try
            {
                var text = File.ReadAllText(file.FullName);
                var configs = JsonConvert.DeserializeObject<AutoGatherList.Config[]>(text);
                if (configs != null)
                {
                    return new AutoGatherListsManager(configs);
                }
            }
            catch (Exception e)
            {
                GatherBuddy.Log.Error($"Error deserializing auto gather lists:\n{e}");
                Communicator.PrintError($"[GatherBuddy Reborn] Auto gather lists failed to load and have been reset.");
            }
        }

        return new AutoGatherListsManager();
    }
}
