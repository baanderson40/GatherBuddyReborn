# Native teleport refresh crash — 2026-11-04

## Evidence and limits

User-provided crash log `dalamud_appcrash_20261104_063235_874_25928.log`
reports `C0000005` in `Client::Game::UI::Telepo.UpdateAetheryteList+0xA8`.
Relevant stack:

```text
AutoRetainerCoordinator.Tick
AutoRetainerIntegration.DeferForTimedNode
AutoGather.DeferAutoRetainerForTimedNode
ActiveItemList.GetNextOrDefault -> DoUpdate -> UpdateTeleportationCosts
Telepo.UpdateAetheryteList
```

The stack confirms the AR monitoring/property path invoked a native refresh. It
does not prove the exact login/zoning state or every invalid native dependency.
The accompanying dump has not been analyzed; neither crash artifact is committed.

## Source observations

- Monitoring originally evaluated timed-node priority before character readiness.
- The priority getter called a refreshing selector, not a read-only query.
- The original cost updater cleared its dictionary, checked only Telepo singleton
  existence, and invoked the native refresh. Territory/time markers were advanced
  before the refresh succeeded, potentially suppressing a same-territory retry.
- `Teleporter.IsAttuned` had another direct refresh and returned true if native
  LocalPlayer was missing, potentially authorizing subsequent teleport execution.
- Checked-out Dalamud `Game/ClientState/Aetherytes/AetheryteList.cs` is **not** a
  passive cache: Length, index access, and enumeration call the same native refresh.
  Merely swapping to that wrapper would not remove the native call path.

## Patch

### Read-only AR priority

`ActiveItemList.GetNextOrDefault` publishes a managed timed-node decision only
after its normal safe scheduler update. AR reads this snapshot rather than
GetNextOrDefault, CurrentOrDefault, or inventory predicates. Character/home/current
world/territory changes, dirty list flags, or Eorzea-hour changes make the snapshot
unavailable. Unknown priority conservatively defers AR while normal gathering
continues and repopulates the snapshot. Active, unvisited, in-window timed targets
retain priority; Diadem deferral remains unchanged.

Coordinator monitoring checks character readiness before priority inspection.
The native adapter's handoff admission additionally requires 500 ms stable player
readiness. This gate is separate from existing cancellation and return settling.

### Central native boundary

`SeFunctions/TeleportData.cs` contains the only GBR `UpdateAetheryteList` call.
Before refresh it requires framework-thread execution, login, nonzero identity and
territory, screen readiness, no area transition, matching managed/native local
player pointers, loaded native PlayerState/content identity, UIState, and all four
loaded player inventory containers. The context must remain stable for 500 ms.
Readiness and identity are rechecked immediately before native entry, after it
returns, and before publishing copied costs. No native pointers are retained.

This still performs a guarded native call; checks reduce the identified invalid-
state exposure, not prove immunity to every native failure. Access violations are
not swallowed. Managed refresh errors remain retryable with throttled diagnostics.

Published costs are immutable managed copies. Failed/deferred copies stay pending;
context changes cannot expose another character's costs. Refresh publication checks
generation changes and prevents recursive refresh. UI consumers read managed data
only; refresh requests are serviced on the framework thread, bounded to five-second
intervals for UI-driven freshness. Explicit teleport execution can request a fresh
snapshot after readiness checks. Missing cost values never become zero; oversized
native costs rank as `int.MaxValue` rather than wrapping negative.

### Deferred work and execution

ActiveItemList does not consume update markers when cost data is unavailable.
Target lists are materialized before replacement; managed builder failures keep
retry intent and restore previous context markers. Both checked and unchecked
teleport entry points require native readiness. Unavailable data produces a
temporary-readiness message, not guessed attunement or a false successful teleport.
The checked entry point returns the native teleport acceptance result.

Framework crafting/vendor/gathering updates wait for ready teleport data after AR/
Marketboard coordinators have had an opportunity to process login/return state.
Queued GBR actions pause rather than consuming a deferred native action; active
task deadlines receive paused time once on resume. Manual debug stepping dispatches
onto the framework thread. Saved lists, spending, confirmed craft progress, and
existing handoff ownership are not reset.

## Verification

Managed tests cover readiness debounce/reset, character/home/current-world/territory
changes, missing and empty snapshots, retry without territory change, immutable
publication, refresh-time context changes, reentrancy, timed-node snapshot validity,
readiness-paused deadlines, and AR not inspecting timed priority before readiness.
Actual-plugin IL checks verify priority getters do not invoke refreshing selectors/
inventory methods and there is only one centralized native refresh call site.
These tests do not execute native game functions and cannot prove crash immunity.

Automated evidence: 429 acquisition/source tests and 85 actual-plugin compatibility
tests passed (514 total, including the pending status-label correction). Debug and
Release builds passed; full builds retain only the two existing ElliCon/ElliLib
nullable warnings. `git diff --check` passed.

### Reported live stability — partial acceptance

On 2026-11-04 the user reported approximately one hour of operation without issues
and confirmed timed-node priority was enabled with transition activity during the
run. The confirmation did not enumerate each zoning/login/AR-return scenario or
include a transition log excerpt. This supports the guarded crash-path fix, but
does not prove native crash immunity or complete the individual checks below.

### Remaining controlled native acceptance

- [ ] Enable Prioritize timed gathering nodes; normal and timed-node gathering.
- [ ] Ordinary zoning/teleporting with queued native actions retained.
- [ ] Login/logout and same-DC/cross-DC travel without early native refresh.
- [ ] AR handoff, original-character return, automatic resume, Stop/recovery.
- [ ] New attunement, cost-based location ranking, UI attunement consumers, and manual teleport commands.
- [ ] Temporary readiness failures retry without false not-attuned/vendor failures.
- [ ] Debug single-step still works through framework dispatch.

Legacy-mode log duplication and the unrelated status-label correction remain
separate work items. No commit/push is implied by this patch.
