using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using GatherBuddy.Classes;
using GatherBuddy.Enums;

namespace GatherBuddy.FishTimer.Parser;

public unsafe partial class FishingParser : IDisposable
{
    public event Action<FishingSpot?>?                       BeganFishing;
    public event Action?                                     BeganMooching;
    public event Action<Fish, ushort, byte, bool, bool>?     CaughtFish;
    public event Action<FishingSpot>?                        IdentifiedSpot;
    public event Action<HookSet>?                            HookedIn;
    private readonly Hook<AgentCatch.Delegates.UpdateCatch>  _catchHook;
    private readonly Hook<ActionManager.Delegates.UseAction> _hookHook;
    private readonly Guid _hookAttempt = Guid.NewGuid();
    private bool _enabled, _disposed;

    public FishingParser(IGameInteropProvider provider)
    {
        FishingSpotNames = SetupFishingSpotNames();

        (_catchHook, _hookHook) = HookInitialization.CreatePair(
            () => CreateFishingHook(provider, "AgentCatch.UpdateCatch",
                (nint)AgentCatch.MemberFunctionPointers.UpdateCatch, (AgentCatch.Delegates.UpdateCatch)OnCatchUpdate),
            () => CreateFishingHook(provider, "ActionManager.UseAction",
                (nint)ActionManager.MemberFunctionPointers.UseAction, (ActionManager.Delegates.UseAction)OnUseAction),
            ReportHookCleanup);
    }

    private Hook<T> CreateFishingHook<T>(IGameInteropProvider provider, string stage, nint address, T detour)
        where T : Delegate
    {
        WriteHookDiagnostic(() => GatherBuddy.Log.Debug($"[Fishing hooks][{_hookAttempt}] Creating {stage}; target=0x{address:X}; {DescribeHookEnvironment(address)}"));
        try
        {
            var hook = provider.HookFromAddress(address, detour);
            WriteHookDiagnostic(() => GatherBuddy.Log.Debug($"[Fishing hooks][{_hookAttempt}] Created {stage}; backend={hook.BackendName}"));
            return hook;
        }
        catch (Exception ex) when (HookInitialization.IsManagedFailure(ex))
        {
            WriteHookDiagnostic(() => GatherBuddy.Log.Error($"[Fishing hooks][{_hookAttempt}] Failed creating {stage}; target=0x{address:X}. No retry or backend fallback attempted. Preserve Dalamud's preceding 'Could not set up a hook at' address-space report.\n{ex}"));
            throw;
        }
    }

    private static string DescribeHookEnvironment(nint address)
    {
        try { return DescribeHookEnvironmentCore(address); }
        catch (Exception ex) when (HookInitialization.IsManagedFailure(ex))
        { return $"thread={Environment.CurrentManagedThreadId}; environment inspection unavailable: {ex.Message}"; }
    }

    private static string DescribeHookEnvironmentCore(nint address)
    {
        using var process = Process.GetCurrentProcess();
        var target = "not in an enumerated native module";
        foreach (ProcessModule module in process.Modules)
        {
            var offset = address.ToInt64() - module.BaseAddress.ToInt64();
            if (offset < 0 || offset >= module.ModuleMemorySize) continue;
            target = $"{module.ModuleName}+0x{offset:X}";
            break;
        }
        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name is { } name && (name == "Dalamud" || name.Contains("Reloaded", StringComparison.OrdinalIgnoreCase)))
            .Select(a => $"{a.GetName().Name}={a.GetName().Version} ({a.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown build"})")
            .OrderBy(name => name);
        return $"pid={process.Id}; thread={Environment.CurrentManagedThreadId}; module={target}; loaded={string.Join("; ", assemblies)}";
    }

    private static void WriteHookDiagnostic(Action write)
    {
        // Diagnostics cannot turn successful allocation into an unowned hook or prevent creation.
        try { write(); }
        catch (Exception ex) when (HookInitialization.IsManagedFailure(ex)) { }
    }

    private void ReportHookCleanup(string stage, Exception ex)
        => GatherBuddy.Log.Error($"[Fishing hooks][{_hookAttempt}] Cleanup failed at {stage}: {ex}");

    public void Enable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_enabled) return;
        try
        {
            _catchHook.Enable();
            _hookHook.Enable();
            Dalamud.Chat.ChatMessage += OnMessageDelegate;
            _enabled = true;
        }
        catch (Exception ex) when (HookInitialization.IsManagedFailure(ex))
        {
            Disable();
            WriteHookDiagnostic(() => GatherBuddy.Log.Error($"[Fishing hooks][{_hookAttempt}] Enable failed; completed hooks disabled, no retry attempted.\n{ex}"));
            throw;
        }
    }

    public void Disable()
    {
        if (_disposed) return;
        _enabled = false;
        HookInitialization.Cleanup(() => Dalamud.Chat.ChatMessage -= OnMessageDelegate, "chat unsubscribe", ReportHookCleanup);
        HookInitialization.Cleanup(_hookHook.Disable, "UseAction disable", ReportHookCleanup);
        HookInitialization.Cleanup(_catchHook.Disable, "UpdateCatch disable", ReportHookCleanup);
    }

    public void Dispose()
    {
        if (_disposed) return;
        Disable();
        _disposed = true;
        HookInitialization.Cleanup(_hookHook.Dispose, "UseAction dispose", ReportHookCleanup);
        HookInitialization.Cleanup(_catchHook.Dispose, "UpdateCatch dispose", ReportHookCleanup);
    }

    private void OnCatchUpdate(
        AgentCatch* thisPtr,
        uint itemId,
        bool isLarge,
        ushort size,
        byte amount,
        byte level,
        byte stars,
        byte oceanStars,
        bool isMoochable,
        bool isFirstTimeCatch,
        byte a11,
        byte a12)
    {
        if (!GatherBuddy.Config.HideFishSizePopup)
            _catchHook.Original(thisPtr, itemId, isLarge, size, amount, level, stars, oceanStars, isMoochable, isFirstTimeCatch, a11, a12);

        // Check against collectibles.
        var collectible = false;
        if (itemId > 500000)
        {
            itemId      -= 500000;
            collectible =  true;
        }

        if (!GatherBuddy.GameData.Fishes.TryGetValue(itemId, out var fish))
        {
            GatherBuddy.Log.Error($"Unknown fish id {itemId} caught.");
            return;
        }

        CaughtFish?.Invoke(fish, size, amount, isLarge, collectible);
    }

    private bool OnUseAction(ActionManager* thisPtr, ActionType actionType, uint actionId, ulong targetId, uint extraParam, ActionManager.UseActionMode mode, uint comboRouteId, bool* outOptAreaTargeted)
    {
        if (actionType == ActionType.Action)
        {
            switch (actionId)
            {
                case 296:   HookedIn?.Invoke(HookSet.Hook); break;
                case 269:   HookedIn?.Invoke(HookSet.DoubleHook); break;
                case 4103:  HookedIn?.Invoke(HookSet.Powerful); break;
                case 4179:  HookedIn?.Invoke(HookSet.Precise); break;
                case 27523: HookedIn?.Invoke(HookSet.TripleHook); break;
                case 41278: HookedIn?.Invoke(HookSet.Stellar); break;
            }
        }

        return _hookHook!.Original(thisPtr, actionType, actionId, targetId, extraParam, mode, comboRouteId, outOptAreaTargeted);
    }
}
