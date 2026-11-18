using System;
using System.Diagnostics;

namespace GatherBuddy.Plugin;

public sealed class GatherBuddyIpc : IDisposable
{
    public const int IpcVersion = 2;

    private readonly GatherBuddy _plugin;
    private readonly VendorPurchaseIpcOperations _vendorPurchases;
    private readonly CraftingListIpcOperations _craftingLists = new();

    public GatherBuddyIpc(GatherBuddy plugin)
    {
        _plugin = plugin;
        _vendorPurchases = new(payload => VendorPurchaseCompleted?.Invoke(payload));
        EzIPC.Init(this, GatherBuddy.InternalName);
        Debug.Assert(AutoGatherWaiting != null);
        Debug.Assert(AutoGatherEnabledChanged != null);
        Debug.Assert(VendorPurchaseCompleted != null);
    }

#pragma warning disable CA1822 // Mark members as static
    [EzIPC]
    public int Version()
        => IpcVersion;

    [EzIPC("VendorPurchase.Version")]
    public int VendorPurchaseVersion()
        => 1;

    [EzIPC("CraftingList.Version")]
    public int CraftingListVersion()
        => 1;

    [EzIPC("CraftingList.Start")]
    public string StartCraftingList(int listId)
        => _craftingLists.Start(listId);

    [EzIPC("CraftingList.GetStartStatus")]
    public string GetCraftingListStartStatus(string operationId)
        => _craftingLists.GetStatus(operationId);

    [EzIPC("VendorPurchase.Start")]
    public string StartVendorPurchase(string requestJson)
        => _vendorPurchases.Start(requestJson);

    [EzIPC("VendorPurchase.GetStatus")]
    public string GetVendorPurchaseStatus(string operationId)
        => _vendorPurchases.GetStatus(operationId);

    [EzIPC("VendorPurchase.GetResult")]
    public string GetVendorPurchaseResult(string operationId)
        => _vendorPurchases.GetResult(operationId);

    [EzIPC("VendorPurchase.Cancel")]
    public string CancelVendorPurchase(string operationId)
        => _vendorPurchases.Cancel(operationId);

    public void UpdateVendorPurchases()
    {
        _vendorPurchases.Update();
        _craftingLists.Update();
    }

    [EzIPC]
    public uint Identify(string text)
        => _plugin.Executor.Identificator.IdentifyGatherable(text)?.ItemId
         ?? _plugin.Executor.Identificator.IdentifyFish(text)?.ItemId ?? 0;

    [EzIPC]
    public bool IsAutoGatherEnabled()
        => GatherBuddy.AutoGather.Enabled;

    [EzIPC]
    public string GetAutoGatherStatusText()
        => GatherBuddy.AutoGather.AutoStatus;

    [EzIPC]
    public void SetAutoGatherEnabled(bool enabled)
        => GatherBuddy.AutoGather.Enabled = enabled;

    [EzIPC]
    public bool IsAutoGatherWaiting()
        => GatherBuddy.AutoGather.Waiting;

    [EzIPCEvent]
    public Action AutoGatherWaiting;

    [EzIPCEvent]
    public Action<bool> AutoGatherEnabledChanged;

    [EzIPCEvent("VendorPurchase.Completed")]
    public Action<string> VendorPurchaseCompleted;

#pragma warning restore CA1822 // Mark members as static

    public void Dispose()
    {
        _vendorPurchases.Dispose();
        _craftingLists.Dispose();
        // EzIPC disposal is handled in GatherBuddy.cs Dispose method
    }
}
