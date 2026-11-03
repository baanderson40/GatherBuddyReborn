# Acquisition policy/model tests

Tests link the actual pure production sources, avoiding the plugin runtime and native game dependencies. `IngredientQualityDemand` was moved to its own production file without behavior changes so the same implementation can be tested here.

Run from repository root using the Windows SDK:

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test tests/GatherBuddy.Acquisition.Tests/GatherBuddy.Acquisition.Tests.csproj --configuration Debug --nologo
```

The project is intentionally standalone from the plugin solution. These tests cover policy migration/normalization, serialization, snapshots, acquisition contracts, deterministic purchase planning, and transient execution through a fake native port. They do not prove in-game purchasing, config-host integration, full crafting-list import behavior, or the native vendor-offer adapter.

## In-game read-only diagnostics (Debug build)

1. Open `/vulcan`, then **Debug**.
2. Select a crafting list under **Automatic Acquisition Preview**.
3. Leave temporary policy enabled to test gil/GC priority, reserves, and gil cap without saving settings. Disable it to inspect the actual global/list policy.
4. Leave **Include full offer diagnostics** off for the compact report. Enable it for individual offers, inventory/quality details, balance sources, additional eligible-option blockers, and rejected alternatives.
5. Optionally enable **Include policy/planner trace** for bounded P1/P2 event logging, independent of full offer diagnostics. Enable Debug-level output in `/xllog` and filter `[Vulcan Acquisition]` to see events. Each trace shares the preview session ID and has sequence numbers.
6. Click **Log Acquisition Preview to Dalamud Console**.
7. Open `/xllog` and filter for `[Vulcan Acquisition Preview]` (Information level) for the summary report.
8. If vendor caches are loading, wait and click again. No automatic repeated capture occurs.

The compact report hides satisfied materials, unused currencies, and individual offer rows. It includes relevant balances/reserves, selected vendor allocations, remaining acquisition quantities, one primary shortage reason per material, grouped rejected-offer counts, and final item-type totals. Counts are offers, not distinct NPCs. Hard quality requirements remain explicit. Unknown vendor access is labeled unverified.

Full diagnostics include currency balance sources, inventory/quality demand, vendor/shop IDs, all rejected offers, and additional blockers. Wrong-GC offers remain visible as inaccessible. Special exchanges skipped by the underlying resolver cannot appear in the report.

Primary shortage reasons come from highest-priority eligible offers, after planned purchases and gathering fallback. Unsupported or wrong-GC alternatives do not pollute a supported route's funding shortage. When no eligible route exists, diagnostics distinguish disabled purchasing, disallowed currencies, inaccessible vendors, and unsupported offers. Bundle residuals and mandatory HQ demand have separate reasons. Rejected alternatives remain available in the full report and grouped counts. These diagnostic changes do not alter purchasing allocations.

The preview button causes no purchases, navigation, withdrawals, saved shopping-list changes, or spending commitments. Shared vendor caches may initialize. Current inventory is used; retainer withdrawal is not simulated. Unknown vendor access requires later runtime checks. Pause unrelated automation for a stable snapshot. The separate live-test controls below the preview are not read-only.

### Policy and planner trace

- P1 events show actual normalization corrections, effective policy origin, temporary-policy status, detached snapshot settings, and reserves. Configuration/list load and import boundaries emit correction events at Debug/Warning levels under a separate operation ID; normal legacy defaults are not claimed as migrations or warnings.
- P2 events show native balance sources, aggregated quality demand, consumed NQ/HQ inventory, ordering/source permissions, grouped rejected offers, eligible offer funding checks, gil budget, allocation commitment changes, gathering fallback, primary/additional blockers, and projected spending. Replanning logs explicit cancelled-allocation releases and retained commitments.
- Trace is opt-in per preview, not per frame. The pure planner produces structured events and does not invoke Dalamud logging or read additional native balances.
- Preview event buffers hold at most 1,024 events. Text fields are bounded and line breaks sanitized. Omitted-event counts are explicit; final outcome is retained even when events are truncated. The Info-level trace summary explains how to enable Debug output.
- Preview tracing does not execute purchases, serialize whole configurations, or change allocation results. Native execution has separate P3 tracing and requires in-game testing.

## Phase 3 live purchase test (Debug build only)

**Warning:** The live test spends real currency and navigates to a vendor. Purchases cannot be undone. Teleport fees are additional gil costs, outside the purchase cap/reserve accounting; start near the vendor to avoid travel costs during initial validation.

1. Stop gathering, crafting, collectable automation, shopping lists, and other travel. Close existing vendor dialogs. Keep at least one free bag slot.
2. Generate a fresh read-only preview. Check its effective policy, reserves, cap, item, vendor, and currency. Use an inexpensive stackable gil or current-GC material.
3. Under **LIVE transient purchase test**, choose one allocation and quantity **1**. The UI permits at most five items and 1,000 gil or 500 GC seals per test. A policy with a lower cap/reserve still takes precedence.
4. Record item count and currency balance. Check **I authorize this real purchase and vendor travel**, then click **Execute confirmed single-allocation test** once.
5. Wait for **Finished**. Compare actual item/currency changes with acquired quantity and confirmed spending. Check `accounting certain=True` and `cleanup succeeded=True`. Inspect `/xllog`, filter `[Vulcan Acquisition]`, enable Debug for P3 events. The debug authorization and all P3 events identify the live session separately from the source preview.
6. Verify the saved shopping list's selection, entries, and settings are unchanged. Gather + Craft must not start automatically: pipeline hookup is Phase 4.
7. Test pause/resume while travelling, then cancellation before submission, before testing either around purchase confirmation. A submitted transaction may still settle. Unknown accounting stops further purchases; reconcile inventory/currency manually before another test. Cleanup timeout retains ownership and reports `CleanupBlocked`; close vendor dialogs and wait for cleanup rather than attempting to start another run.

Initial native execution supports stackable unit-output gil/GC purchases only. Special exchanges remain preview-only; housing, equipment, nonstackable materials, unsupported routes/offers, full inventory, changed prices, and wrong GC/rank are rejected or skipped. Purchases revalidate the exact offer rather than switching currency/vendor. Each request submits at most one existing-engine batch; partial results return to the coordinator before any further request. Inventory increase and currency decrease must match; unrelated manual inventory/spending changes can deliberately cause an accounting failure.

Fake-port tests cover exclusive ownership (including stale owner IDs), saturating budgets, shared commitments/caps, batches beyond 99, target shrink/already-owned targets, partial outcomes, pause/resume, cancellation, mismatched/stale receipts, unsafe states, rejected submissions, and cleanup timeout/retry. Native shop callbacks, GC dialog sequencing, travel, unload cleanup, and inventory timing still require live verification. The adapter waits one second before receipt reconciliation and at least two seconds before cleanup; these delays are conservative heuristics, not proof of server settlement.

## Aethernet source navigation regression

Source approach now uses the live object's hitbox radius plus 3 m horizontally, with a separate vertical bound of the same size to reject wrong-floor proximity while allowing elevated origins on large objects. Zero radius means 3 m; invalid geometry fails safely. City, post-teleport city, and housing sources share the same handoff logic. Lifestream's active-source IPCs remain the final readiness check; their IDs do not provide a verified mapping to the selected live object.

After arrival, allow 2 seconds for recognition, then one corrective approach toward the source. Recognition/acceptance must finish within 10 seconds of first arrival; the overall source approach is limited to 60 seconds. Drift and replacement sources do not restart the overall deadline. Path calculations are owned/cancellable, with no background continuation that can initiate movement after cancellation. Source diagnostics log on state changes and otherwise at most every 2 seconds. Existing unrelated NPC projection/fallback diagnostics are unchanged.

Live verification (not covered by pure tests):

1. Reload the latest Debug DLL. Reproduce the Limsa route via **The Aftcastle** from the position that previously stalled at 11.2 m. Check `[VendorNavigator] Aethernet source` logs for radius, allowed horizontal distance, actual horizontal/vertical distances, and recognition IDs. Confirm approach continues to hitbox + 3 m and the destination request succeeds without a repeated wait loop.
2. Repeat with a small city shard and a housing/residential source. Test vertical separation to ensure another floor cannot authorize handoff from horizontal proximity alone. Confirm large-object origin height does not prevent normal use.
3. Cancel during path calculation, ground movement, and recognition waiting. No late path or teleport may start. Pause/resume a transient run and confirm fresh source validation.
4. Repeat quantity-2 Hardened Sap purchase: expect 2 acquired, 400 Storm Seals confirmed spent, certain accounting, successful cleanup. This is a real purchase; travel fees remain separate.
5. If recognition remains unavailable, verify a clear bounded navigation failure and transient cleanup rather than indefinite waiting. An externally busy Lifestream retains travel ownership; the source handler does not interrupt it.

Pure tests cover radius/zero/invalid values, boundary agreement, vertical rejection, grace/corrective approach, deadline preservation, missing/replaced sources, territory reset, refused handoff timeout, and terminal cancellation/reset. They do not prove native path cancellation or Lifestream behavior.

## Planning contract

- Demands are total requirements, not net shortages. Repeated item demands are summed; the separate NQ/HQ inventory snapshot is consumed once.
- Repeated demands for one item must agree on gathering and purchasing permissions.
- Plans allocate spending but never represent confirmed purchases. Allocation IDs are fresh tracking IDs; allocation decisions and ordering are deterministic.
- Replanning may release explicitly supplied cancelled, unexecuted allocations from the same session. The caller must not release in-flight work. Other commitments remain reserved.
- Loading vendor data returns a waiting plan without purchase allocations, gathering fallback, or false shortage warnings. Rebuild after data becomes ready.
- Unknown vendor access requires later execution-time validation. Known inaccessible/unsupported offers are excluded.
- The pure planner supports whole bundles without overbuying. The native adapter conservatively rejects special-shop bundles, multiple outputs, and multiple costs because the current executor assumes unit output and a single cost.
- Input and output snapshots are detached/read-only. Native snapshots must be captured on the game thread after existing caches are requested.
