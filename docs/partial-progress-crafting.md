# Partial-progress crafting (Phase 5)

## Applied behavior

- Queues with purchasing enabled or disabled reconcile acquisition, then select
  the first ready entry in the remaining expanded queue. Blocked entries move
  behind selected work without losing quantities, options, or quality policy.
- Readiness checks immediate NQ/HQ ingredient demand against current inventory.
  Earlier pending precrafts remain dependency barriers for consumers of their
  output. Planned output is never treated as owned inventory.
- Selection is repeated after confirmed progress. Ingredient availability and job
  are rechecked before synthesis. Quick Synthesis batches fit available materials,
  honor strict quality requirements, and remain limited to 99 crafts.
- Missing-material preparation failures defer the recipe instead of marking its
  remaining entries skipped. Successful progress or explicit recheck rearms it.
  If no entry is ready, the queue enters **Blocked by Material Shortages** with
  remaining intent and quality-aware external shortages. No automatic route retry.
- **Recheck materials (no purchases)** resumes ready work without purchasing.
  **Retry acquisition** explicitly starts another acquisition pass with the run's
  policy, current balances/inventory, and retained queue requirements. Close
  crafting/vendor interactions and stop conflicting automation first.
- Retry never rebuilds the original list after crafting begins. Planned precraft
  yields offset remaining flexible ingredient demand; hypothetical outputs do not
  satisfy hard HQ/NQ requirements. Prior settled spending carries across passes so
  the per-run gil cap cannot restart on each retry.
  Each retry uses a fresh acquisition ownership ID under a stable crafting run ID;
  the vendor executor's finished-session replay protection remains intact.
- A craft-finished callback or Quick Synthesis progress counter is not proof of
  successful output. The queue waits up to three seconds for inventory output
  deltas and advances only whole confirmed crafts. Failed/cancelled or uncertain
  results pause with unconfirmed work retained. Inspect the actual outcome before
  resuming, especially if inventory updates were delayed or items were manually
  moved. Deliberate short Quick Synthesis batches can continue when every observed
  attempt has confirmed output.
  Subsequent inventory-recovery handling excludes loading/recovery time from that
  confirmation window; see `inventory-recovery.md` for zoning and interruption behavior.
- Collectables interruptions wait until synthesis and output reconciliation are
  finished, preventing automatic turn-ins from consuming unconfirmed output.
- Solver failures pause rather than dropping queue entries. Missing recipe data or
  unsafe inventory reconciliation retains work in a failure state. Only explicit
  user skip options exclude work; incomplete queues cannot report Complete.
- Job equip submission waits two seconds for confirmation before retry, with three
  attempts before pausing. Actual submission restarts the confirmation window even
  if exiting crafting UI took time. Pause/reset clears pending tasks and guard
  state; resume can retry deliberately. Craft-start timeouts pause instead of
  automatically issuing synthesis repeatedly.

## Automated coverage

The linked-production-source acquisition suite includes `CraftReadinessTests` for
stable ready selection, independent work after blocked entries, precraft barriers,
newly available output, shared stock, mandatory quality versus flexible fallback,
explicit skips, preparation-failure rearming, remaining multi-output requirements,
inventory changes, whole-output confirmation, material-limited batch sizes, and
job-switch confirmation/retry/reset timing. Existing acquisition tests continue to
exercise ownership, cancellation, reconciliation, and budget allocation.
`CraftingAcquisitionRunTests` additionally cover fresh retry IDs, stale-result
rejection, once-only spending observation, unsafe-retry refusal, snapshot isolation,
and planning against the unspent part of the original run cap.

These tests do not load Dalamud or validate native recipe-window callbacks, live
inventory timing, or the acquisition adapter across actual repeated purchases.

## Pending live verification

1. Start with an unavailable non-gatherable material and an independent ready
   recipe. Confirm ready work crafts; dependent work remains queued and Blocked.
2. Add the missing material and Recheck; finish only remaining work. Repeat with
   Retry acquisition, verifying no completed precrafts are resurrected and prior
  spending still reduces the run cap.
3. Exercise HQ-only/NQ-only ingredients, shared stock, and multi-output precrafts.
4. Exercise normal success, failed/cancelled synthesis, Quick Synthesis with
   interrupted batches, duplicate/jumping progress, and delayed inventory updates.
   Verify no false completion or duplicate credit.
5. Verify delayed job changes issue one equip command, timeout pauses, and
   pause/cancel during switching/acquisition cannot submit late work.
6. Verify collectables, repair, materia, and Standard/Pure Raphael workflows still
   resume correctly. Full acquisition-to-list completion and housing navigation
   remain unverified until demonstrated in game.

Successful Raphael lifecycle log reduction is separate and not included here.
