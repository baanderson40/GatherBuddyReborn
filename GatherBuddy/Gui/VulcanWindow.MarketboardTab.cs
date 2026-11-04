using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Textures;
using ElliLib;
using GatherBuddy.Marketboard;
using ImRaii = ElliLib.Raii.ImRaii;

namespace GatherBuddy.Gui;

public partial class VulcanWindow
{
    private uint _mbSelectedItemId;
    private bool _mbRequestFocus;
    private string _mbSearch = string.Empty;
    private bool _mbShowRecent;
    private bool _mbResultsDirty = true;
    private List<MarketCatalogItem> _mbResults = new();
    private string? _mbLookupKey;
    private MarketTravelDestination? _mbTravelMenuDestination;
    private string _mbTravelMenuWorld = string.Empty;
    private string _mbTravelMenuError = string.Empty;

    private unsafe void DrawMarketboardTab()
    {
        var navigation = GatherBuddy.ControllerSupport?.TabNavigation;
        var flags = _mbRequestFocus || navigation?.ShouldSelectTab((int)VulcanMainTab.Marketboard) == true
            ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        fixed (byte* label = "Marketboard##marketboardTab\0"u8)
        {
            using var tab = ImRaii.TabItem(label, flags);
            if (!tab) return;
            _mbRequestFocus = false;
            navigation?.NotifyTabActive((int)VulcanMainTab.Marketboard);
            if (GatherBuddy.MarketboardService is not { } service)
            {
                ImGui.TextDisabled("Marketboard service unavailable.");
                return;
            }
            DrawMarketboard(service);
        }
    }

    private void DrawMarketboard(MarketboardService service)
    {
        if (DrawSearchInputWithInlineClear("##marketSearch", "Search marketable items…", ref _mbSearch, 128))
        {
            _mbResultsDirty = true;
            if (!string.IsNullOrWhiteSpace(_mbSearch)) _mbShowRecent = false;
        }

        var home = service.GetHome();
        var scope = service.GetSelectedScope();
        ImGui.SetNextItemWidth(Math.Min(VulcanUiScaling.Scaled(220f), ImGui.GetContentRegionAvail().X * 0.55f));
        using (ImRaii.Disabled(home == null))
        {
            if (ImGui.BeginCombo("##marketScope", scope?.Label ?? "Waiting for character"))
            {
                if (home != null)
                {
                    var options = MarketScope.Options(home);
                    for (var i = 0; i < options.Count; i++)
                    {
                        if (i == 2) ImGui.Separator();
                        var option = options[i];
                        if (ImGui.Selectable(option.Label, option.Id == scope?.Id))
                        {
                            service.SelectScope(option);
                            scope = option;
                            _mbLookupKey = null;
                        }
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip(option.Id == MarketScope.CrossDcId ? "All North American DCs: Aether, Primal, Crystal, Dynamis."
                                : option.Id == MarketScope.CrossWorldId ? $"All worlds in {home.DataCenter}."
                                : option.Id == $"world:{home.WorldId}" ? "Your home world." : $"World in {home.DataCenter}.");
                    }
                }
                ImGui.EndCombo();
            }
        }
        ImGui.SameLine();
        var quality = (int)service.Preferences.Quality;
        ImGui.SetNextItemWidth(VulcanUiScaling.Scaled(110f));
        if (ImGui.Combo("##marketQuality", ref quality, "Any quality\0NQ only\0HQ only\0"))
        {
            service.Preferences.Quality = (MarketQuality)quality;
            GatherBuddy.Config.Save();
        }
        if (scope?.Id == MarketScope.CrossWorldId && home != null) ImGui.TextDisabled($"All worlds in {home.DataCenter}");
        if (home == null) ImGui.TextWrapped("Log in to resolve your home world and data center. No default DC is assumed.");

        if (scope != null && _mbSelectedItemId != 0)
        {
            var key = $"{_mbSelectedItemId}/{scope.Id}/{home?.WorldId}";
            if (_mbLookupKey != key && service.GetItem(_mbSelectedItemId) != null)
            {
                _mbLookupKey = key;
                service.Request(_mbSelectedItemId, scope);
            }
        }
        else if (scope == null) _mbLookupKey = null;

        DrawMarketTravelStatus();

        ImGui.Separator();
        var available = ImGui.GetContentRegionAvail();
        var sideBySide = available.X >= VulcanUiScaling.Scaled(800f);
        var listSize = sideBySide
            ? new Vector2(VulcanUiScaling.Scaled(270f), Math.Max(1f, available.Y))
            : new Vector2(0, Math.Max(1f, Math.Min(VulcanUiScaling.Scaled(200f), available.Y * 0.4f)));
        using var panels = VulcanUiStyle.PushPanel();
        using (var list = ImRaii.Child("##marketItems", listSize, true))
        {
            if (list) DrawMarketboardItems(service);
        }
        if (sideBySide) ImGui.SameLine();
        using (var detail = ImRaii.Child("##marketDetail", new Vector2(0, 0), true, ImGuiWindowFlags.HorizontalScrollbar))
        {
            if (detail) DrawMarketboardDetail(service, scope, (MarketQuality)quality);
        }
    }

    private void DrawMarketboardItems(MarketboardService service)
    {
        if (ImGui.RadioButton("Search##marketSearchView", !_mbShowRecent)) _mbShowRecent = false;
        ImGui.SameLine();
        if (ImGui.RadioButton("Recent##marketRecentView", _mbShowRecent)) _mbShowRecent = true;
        ImGui.Separator();
        if (_mbResultsDirty || service.CatalogError != null)
        {
            _mbResults = service.Search(_mbSearch).ToList();
            _mbResultsDirty = false;
        }
        if (service.CatalogError != null)
        {
            ImGui.TextWrapped(service.CatalogError);
            return;
        }
        var items = _mbShowRecent
            ? service.GetHistorySnapshot().Select(id => service.GetItem(id)
                ?? new MarketCatalogItem(id, service.GetItemName(id), service.GetItemIcon(id), false))
                .Where(item => item.Name.Contains(_mbSearch.Trim(), StringComparison.OrdinalIgnoreCase)).ToList()
            : _mbResults;

        if (_mbShowRecent)
        {
            if (service.HistoryWarning != null) ImGui.TextWrapped(service.HistoryWarning);
            if (ImGui.SmallButton("Clear History")) ImGui.OpenPopup("##confirmClearMarketHistory");
            if (ImGui.BeginPopup("##confirmClearMarketHistory"))
            {
                ImGui.TextUnformatted("Clear all recent market lookups?");
                if (ImGui.Button("Clear##confirmMarketClear"))
                {
                    service.ClearHistory();
                    _mbSelectedItemId = 0;
                    _mbLookupKey = null;
                    items.Clear();
                    ImGui.CloseCurrentPopup();
                }
                ImGui.SameLine();
                if (ImGui.Button("Cancel##cancelMarketClear")) ImGui.CloseCurrentPopup();
                ImGui.EndPopup();
            }
        }
        if (items.Count == 0)
        {
            ImGui.TextWrapped(_mbShowRecent ? "No matching recent lookups. Search for any marketable item."
                : string.IsNullOrWhiteSpace(_mbSearch) ? "Type an item name to search all marketable game items."
                : "No matching marketable items.");
            return;
        }
        ImGui.TextDisabled($"{items.Count:N0} items");
        using var results = ImRaii.Child("##marketResults", new Vector2(0, 0));
        if (!results) return;
        ImGuiClip.ClippedDraw(items, item =>
        {
            var iconSize = VulcanUiScaling.Scaled(24f, 24f);
            var texture = item.IconId == 0 ? null : Icons.DefaultStorage.TextureProvider
                .GetFromGameIcon(new GameIconLookup(item.IconId)).GetWrapOrDefault();
            if (texture == null) ImGui.Dummy(iconSize);
            else ImGui.Image(texture.Handle, iconSize);
            ImGui.SameLine();
            if (ImGui.Selectable($"{item.Name}##marketItem{item.ItemId}", _mbSelectedItemId == item.ItemId,
                    ImGuiSelectableFlags.None, new Vector2(0, iconSize.Y)))
            {
                _mbSelectedItemId = item.ItemId;
                _mbLookupKey = null;
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{item.Name}\nItem #{item.ItemId}");
            if (!_mbShowRecent) return;
            var context = GatherBuddy.ControllerSupport != null
                ? GatherBuddy.ControllerSupport.ContextMenu.BeginPopupContextItemWithGamepad($"##marketHistory{item.ItemId}", Dalamud.GamepadState)
                : ImGui.BeginPopupContextItem($"##marketHistory{item.ItemId}");
            if (!context) return;
            if (ImGui.Selectable("Remove from history"))
            {
                service.RemoveFromHistory(item.ItemId);
                if (_mbSelectedItemId == item.ItemId) { _mbSelectedItemId = 0; _mbLookupKey = null; }
            }
            ImGui.EndPopup();
        }, VulcanUiScaling.Scaled(24f) + ImGui.GetStyle().ItemSpacing.Y);
    }

    private void DrawMarketboardDetail(MarketboardService service, MarketScope? scope, MarketQuality quality)
    {
        if (_mbSelectedItemId == 0) { ImGui.TextWrapped("Select an item to compare prices."); return; }
        ImGui.TextColored(ImGuiColors.ParsedGold, service.GetItemName(_mbSelectedItemId));
        if (service.GetItem(_mbSelectedItemId) == null)
        {
            ImGui.TextWrapped("This item is not marketable, or its game data is unavailable.");
            return;
        }
        if (scope == null) { ImGui.TextWrapped("Waiting for home-world identity before looking up prices."); return; }
        if (ImGui.Button("Refresh##marketRefresh")) service.Request(_mbSelectedItemId, scope, force: true);
        ImGui.SameLine();
        if (ImGui.Button("Universalis##marketWeb"))
        {
            try { Process.Start(new ProcessStartInfo($"https://universalis.app/market/{_mbSelectedItemId}") { UseShellExecute = true }); }
            catch (Exception ex) { GatherBuddy.Log.Warning($"[Marketboard] Could not open Universalis: {ex.Message}"); }
        }
        var statuses = service.Snapshot(_mbSelectedItemId, scope);
        var errors = statuses.Count(s => s.Error != null);
        var pending = statuses.Count(s => s.Pending);
        var available = statuses.Count(s => s.Data != null);
        if (errors > 0 && ImGui.Button("Retry failed requests")) service.Request(_mbSelectedItemId, scope, force: true, failedOnly: true);
        if (pending > 0) ImGui.TextWrapped($"Fetching {pending} of {statuses.Count} scope requests. Cached listings remain visible.");
        if (scope.Id == MarketScope.CrossDcId && (pending > 0 || errors > 0 || available < statuses.Count))
            ImGui.TextColored(ImGuiColors.DalamudOrange, $"Partial NA comparison · {available}/{statuses.Count} DCs have data");
        foreach (var status in statuses)
        {
            var state = status.Pending ? "refreshing" : status.Error != null ? $"failed: {status.Error}"
                : status.Data == null ? "not fetched" : status.Data.Listings.Count == 0 ? "no listings returned" : "available";
            if (status.Stale) state += " · stale cached data";
            ImGui.TextWrapped($"{status.Target.Name}: {state}");
            if (status.FetchedAt != null)
            {
                ImGui.TextWrapped($"Fetched: {MarketAge(status.FetchedAt)} · Latest source upload: {MarketAge(status.Data?.SourceUpdatedAt)}");
            }
        }
        ImGui.Separator();
        ImGui.TextWrapped("Returned listings only, not exhaustive inventory. Universalis data may lag in-game availability.");
        var all = statuses.Where(s => s.Data != null).SelectMany(s => s.Data!.Listings);
        var listings = MarketSearch.Listings(all, quality);
        if (listings.Count == 0)
        {
            if (available > 0) ImGui.TextWrapped(quality == MarketQuality.Any ? "No listings returned." : "No returned listings match this quality filter.");
            return;
        }
        ImGui.TextDisabled($"{listings.Count} returned listings · sorted by unit price");
        DrawMarketListings(listings, scope.Id == MarketScope.CrossDcId, _mbSelectedItemId, service.GetItemName(_mbSelectedItemId));
    }

    private static string MarketAge(DateTimeOffset? timestamp)
    {
        if (timestamp == null) return "unavailable";
        var age = DateTimeOffset.UtcNow - timestamp.Value;
        return age.TotalMinutes < 1 ? "just now" : age.TotalHours < 1 ? $"{(int)age.TotalMinutes}m ago"
            : age.TotalDays < 1 ? $"{(int)age.TotalHours}h ago" : $"{(int)age.TotalDays}d ago";
    }

    private void DrawMarketListings(IReadOnlyList<MarketListing> listings, bool showDc, uint itemId, string itemName)
    {
        var flags = ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg;
        var width = Math.Max(ImGui.GetContentRegionAvail().X, VulcanUiScaling.Scaled(showDc ? 620f : 540f));
        if (!ImGui.BeginTable("##marketListings", showDc ? 6 : 5, flags, new Vector2(width, 0))) return;
        ImGui.TableSetupColumn("Quality");
        ImGui.TableSetupColumn("Unit price");
        ImGui.TableSetupColumn("Qty");
        ImGui.TableSetupColumn("Stack total");
        ImGui.TableSetupColumn("World");
        if (showDc) ImGui.TableSetupColumn("DC");
        ImGui.TableHeadersRow();
        var index = 0;
        foreach (var listing in listings)
        {
            using var rowId = ImRaii.PushId($"listing{index++}");
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            if (ImGui.Selectable($"{(listing.IsHq ? "HQ" : "NQ")}##listingRow", false, ImGuiSelectableFlags.SpanAllColumns))
                ImGui.OpenPopup("##listingTravel");
            var context = GatherBuddy.ControllerSupport != null
                ? GatherBuddy.ControllerSupport.ContextMenu.BeginPopupContextItemWithGamepad("##listingTravel", Dalamud.GamepadState)
                : ImGui.BeginPopupContextItem("##listingTravel");
            if (context)
            {
                if (ImGui.IsWindowAppearing())
                {
                    _mbTravelMenuWorld = listing.WorldName;
                    _mbTravelMenuDestination = GatherBuddy.MarketboardTravel?.ResolveDestination(listing, itemId, itemName, out _mbTravelMenuError);
                    if (GatherBuddy.MarketboardTravel == null) _mbTravelMenuError = "Travel integration unavailable.";
                }
                ImGui.TextUnformatted($"{_mbTravelMenuDestination?.ItemName ?? itemName} · {_mbTravelMenuWorld}");
                ImGui.Separator();
                var integration = GatherBuddy.MarketboardTravel;
                var reason = _mbTravelMenuError;
                var allowed = integration != null && _mbTravelMenuDestination != null
                    && integration.Coordinator.CanStart(_mbTravelMenuDestination, out reason);
                using (ImRaii.Disabled(!allowed))
                {
                    var sameWorld = _mbTravelMenuDestination?.WorldId == Dalamud.Objects.LocalPlayer?.CurrentWorld.RowId;
                    if (ImGui.MenuItem($"{(sameWorld ? "Go" : "Travel")} to marketboard on {_mbTravelMenuWorld}"))
                    {
                        if (integration != null && _mbTravelMenuDestination is { } destination)
                        {
                            var previousRequest = integration.Coordinator.RequestId;
                            if (!integration.Start(destination, out var startError) && integration.Coordinator.RequestId == previousRequest)
                                Plugin.Communicator.PrintError(startError); // Preflight failure has no request notification.
                        }
                    }
                }
                if (!allowed) ImGui.TextWrapped(reason);
                if (ImGui.MenuItem("Copy world name")) ImGui.SetClipboardText(_mbTravelMenuWorld);
                ImGui.Separator();
                ImGui.TextDisabled("Starts immediately · Ul’dah marketboard");
                ImGui.TextDisabled("May cost teleport gil · no purchase or return");
                var currentDc = Dalamud.Objects.LocalPlayer?.CurrentWorld.ValueNullable?.DataCenter.ValueNullable?.Name.ExtractText();
                if (_mbTravelMenuDestination is { } target && currentDc != target.DataCenter)
                    ImGui.TextDisabled("Cross-DC: logout/login and queues may apply");
                ImGui.EndPopup();
            }
            ImGui.TableSetColumnIndex(1); ImGui.Text($"{listing.PricePerUnit:N0} gil");
            ImGui.TableSetColumnIndex(2); ImGui.Text($"{listing.Quantity:N0}");
            ImGui.TableSetColumnIndex(3); ImGui.Text($"{listing.StackTotal:N0} gil");
            ImGui.TableSetColumnIndex(4); ImGui.TextUnformatted(listing.WorldName);
            if (showDc) { ImGui.TableSetColumnIndex(5); ImGui.TextUnformatted(listing.DataCenter); }
        }
        ImGui.EndTable();
    }

    private void DrawMarketTravelStatus()
    {
        if (GatherBuddy.MarketboardTravel is not { } travel || travel.Coordinator.State == MarketTravelState.Idle) return;
        var coordinator = travel.Coordinator;
        ImGui.TextWrapped($"Travel · {coordinator.Destination?.WorldName}: {coordinator.Status}");
        if (coordinator.State == MarketTravelState.Failed) ImGui.TextWrapped($"Failed stage: {coordinator.FailedStage}");
        if (coordinator.State == MarketTravelState.Failed && coordinator.LastObservation is { ContentId: not 0 } observed)
            ImGui.TextWrapped($"Last observed character: {observed.ContentId}; world ID: {observed.WorldId}.");
        if (!coordinator.BlocksAutomation) return;
        using (ImRaii.Disabled(coordinator.State == MarketTravelState.StoppingFollowup))
        {
            if (ImGui.SmallButton("Stop follow-up##marketStopTravel")) travel.StopFollowup();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Prevents further travel stages. Submitted Lifestream travel may continue; it is never aborted here. Automation remains blocked until it settles.");
        if (coordinator.State == MarketTravelState.Failed)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Recover travel##marketRecoverTravel") && !travel.Recover())
                Plugin.Communicator.PrintError("Wait for the original character, Lifestream idle, no conflicting automation, and 500 ms stable readiness before recovery.");
        }
    }

}
