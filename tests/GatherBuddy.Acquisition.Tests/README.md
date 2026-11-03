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
6. Verify the saved shopping list's selection, entries, and settings are unchanged. This standalone Debug test must not start Gather + Craft automatically; the integrated pipeline uses its own run/session and persisted policy.
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

## Phase 4 integrated acquisition

Gather + Craft queue entry points now share one acquisition stage after actual retainer withdrawal, or directly when withdrawal is disabled/unavailable. Saved-list actions, recipe-browser queue actions, and `/vulcan craft` use this path. Direct craft-only actions remain direct. The legacy single-recipe Gather + Craft entry also uses the queue path when automatic purchases are enabled.

The effective global/list purchase policy is snapshotted with the execution plan. Default policy remains disabled. Temporary Debug preview controls never authorize normal purchases. Configure normal execution under **Vulcan → Settings → Automatic Vendor Purchases**: enable purchases, allow gil/current-GC seals, choose their relative priority, set reserves, and optionally limit gil spending per run. Limits remain editable while disabled so protections can be set before authorizing spending. Use low spending caps and protected balances for testing.

For a saved crafting list, click **Automatic Vendor Purchases...** in the list editor's footer near **Start Gather/Crafting**. Choose **Use global settings**, **Disabled**, or **Custom settings**. Custom policies have their own enable toggle and the same controls; selecting Custom alone does not enable a fresh custom policy. A Disabled list override takes precedence over global enablement. Changes save automatically for future runs; active/paused runs keep their original snapshot. Quick recipe runs inherit global settings.

Amounts accept unsigned whole numbers without truncating to 32-bit values; invalid/negative/overflow input shows an error and keeps the saved value. An unchecked gil limit means unlimited; zero means no gil purchases. GC reserves are separate for Storm/Serpent/Flame seal currency IDs. The controls preserve unrelated currency groups, priority entries, and reserves already present in configuration, but integrated execution remains gil/GC-only. Policy editing/parsing helpers are linked into tests; actual widget layout, persistence through the plugin host, and popup interaction need live verification.

**An enabled persisted policy authorizes real spending when Gather + Craft starts, without the Debug confirmation checkbox.** Teleport fees remain additional gil costs outside purchase reserves/caps. Start near an inexpensive supported vendor for initial tests. Special exchanges, equipment, housing items, and nonstackable materials are excluded from integrated purchase planning rather than reserving money for offers the executor cannot execute.

Execution order:

1. Withdraw actual retainer materials, if configured; rebuild from bag inventory even after an aborted/partial withdrawal.
2. Plan from total leaf demands and split NQ/HQ bag counts. Wait at most 30 seconds for vendor data or purchase availability. Missing native inventory fails safely rather than being treated as zero owned.
3. Run one exact transient batch under the crafting session's ownership. Pausing allows submitted work to settle; cancellation never authorizes a later gathering/crafting transition. Existing shared-engine safeguards still apply.
4. Wait for terminal receipt/cleanup. Uncertain accounting or cleanup fails the acquisition stage. An ordinary, safely settled purchase failure can fall back to gathering. No automatic repeated vendor-route retries occur.
5. Capture current inventory again with further purchases disabled. Generate absolute NQ gathering targets as current NQ count plus the quality-compatible remaining gather quantity; do not subtract inventory twice. Mandatory HQ remains unresolved.
6. Report non-gatherable shortages once and continue unrelated gathering. After gathering, refresh requirements/inventory before entering crafting. Known remaining shortages retain the queue in **Blocked by Material Shortages**, never Complete. **Recheck materials (no purchases)** supports manual acquisition without automatically repeating vendor routes.

The status window shows acquisition stage, effective policy/cap/reserves, vendor execution status, and shortages. Console events use `[Vulcan Acquisition]`, the crafting session ID, and `P4`; owned purchases use the same session ID in P3. Unrelated shopping, gathering, and collectable starts are rejected during acquisition. Existing activity must be stopped before starting a new Gather + Craft run. Stop/unload cancels owned transient work; its service retains vendor ownership until safe cleanup, even after the queue detaches.

Phase boundary: readiness scheduling around blocked recipes and acquisition retry are Phase 5. This phase safely blocks crafting when acquisition still has shortages rather than trying to select independent ready recipes. An ingredient preparation failure during enabled integrated execution retains remaining intent as a failure instead of applying the legacy skip-on-repeated-missing-material behavior. Stop and resolve that failure before restarting; pre-craft material rechecking must not rebuild an already partly executed queue.

Live verification still required:

- Feature disabled: compare existing no-retainer/retainer behavior; confirm no purchases and no Debug-policy leakage.
- Small saved list and quick recipe with inexpensive gil/GC materials: verify planning → purchasing → cleanup → gathering → crafting. Repeat with retainer restocking off and on, including a partial/aborted withdrawal and a withdrawal covering everything.
- Tight reserve/cap: verify exact spending, residual gathering, and a non-gatherable shortage retained as Blocked. Add missing material manually, then Recheck; no extra purchase or completed-recipe repetition should occur.
- Pause/resume and Stop during vendor-data waiting, travel, submitted purchase, cleanup, and gathering. Confirm no late stage advance or unrelated automation takeover. Check saved shopping lists and restored gathering-list state.
- Confirm special-exchange-only materials produce fallback/shortage rather than a claimed acquisition. Independent ready-craft scheduling is not an acceptance criterion for Phase 4.

Linked-source fake-port tests cover post-withdrawal inventory, owned materials, shared/split targets, cleanup ordering, fresh reconciliation, partial/failing purchases, NQ/HQ semantics, continued gathering with non-gatherable shortages, bounded waits, disabled policy, ownership availability, stale results, pause/resume, and cancellation. Native queue/bridge entry wiring, retainer UI, source navigation, and full crafting transitions require in-game testing; compilation and pure tests do not verify those boundaries.

## Planning contract

- Demands are total requirements, not net shortages. Repeated item demands are summed; the separate NQ/HQ inventory snapshot is consumed once.
- Repeated demands for one item must agree on gathering and purchasing permissions.
- Plans allocate spending but never represent confirmed purchases. Allocation IDs are fresh tracking IDs; allocation decisions and ordering are deterministic.
- Replanning may release explicitly supplied cancelled, unexecuted allocations from the same session. The caller must not release in-flight work. Other commitments remain reserved.
- Loading vendor data returns a waiting plan without purchase allocations, gathering fallback, or false shortage warnings. Rebuild after data becomes ready.
- Unknown vendor access requires later execution-time validation. Known inaccessible/unsupported offers are excluded.
- The pure planner supports whole bundles without overbuying. The native adapter conservatively rejects special-shop bundles, multiple outputs, and multiple costs because the current executor assumes unit output and a single cost.
- Input and output snapshots are detached/read-only. Native snapshots must be captured on the game thread after existing caches are requested.
