# Inventory recovery and gather handoff

## Reported failure

Apartment zoning at 13:18:49 temporarily hid player inventory. AutoGather shutdown
deleted the temporary Vulcan list, `IsGatheringComplete()` treated a missing list
as completion, and readiness converted unavailable inventory to AcquisitionFailed.
The setter labeled every disable as user action, including automatic shutdown.

## Applied changes

- Queue gathering targets are retained in a detached read-only snapshot independent
  of the temporary list. Missing target metadata is not completion; an explicitly
  created empty target set can represent no gathering required.
- AutoGather disable retains queue gather-list data and uses a neutral diagnostic;
  the setter does not know whether the cause was manual, automatic, or zoning.
- Handoff checks real current inventory against retained targets. Stopped gathering
  with unmet/unknown targets pauses; it neither crafts nor restarts itself.
  Explicit Resume can restart retained gathering after player state recovers.
- Known zoning/loading, absent player, or absent inventory manager enter **Waiting
  for Player / Inventory**, not material shortage or terminal acquisition failure.
  Unavailability is returned explicitly; it is not converted into empty stock.
- Recovery uses monotonic time. Known zone/loading duration and paused time do not
  consume the 30-second inventory-recovery budget. Repeated transitions suspend
  rather than reset the accumulated recovery budget. Timeout pauses with retained
  intent; explicit Resume starts a fresh recovery budget.
- Recovery admission has a 500 ms polling interval. Native readiness indicators
  are checked cheaply during updates; full snapshots are read only when available.
  Logs describe state changes rather than emitting a line every frame.
- Pre-craft recovery clears stale prep tasks and reruns readiness/job validation.
  No new queue equip, synthesis, or gathering restart is submitted while waiting.
  Existing vendor travel/receipt handling and its timeouts are unchanged.
  Preparation additionally waits for travel and stable readiness; see
  `crafting-preparation-gate.md`. Inventory recovery preserves equip attempt counts.
- Output confirmation retains recipe, original inventory baseline, batch size, and
  progress. Loading/recovery time extends its confirmation deadline. A matching
  craft-finished callback received during recovery updates the pending continuation
  once; duplicate callbacks cannot credit output. Recovered inventory, not an
  unavailable snapshot, determines confirmed output.
- Pause/cancel/reset invalidate or suspend recovery. Collectables interruptions
  do not begin during inventory recovery or unconfirmed synthesis. Invalid recipe
  data and unexpected snapshot exceptions still produce retained failure states.

This recovery does not classify the original AutoGather disable cause. After
zoning, satisfied targets can hand off to crafting; stopped gathering with unmet
targets requires explicit Resume. It does not silently restart a possible manual
stop. No acquisition retry or purchase authority is added by inventory recovery.

## Verification

`InventoryRecoveryTests` links production recovery, handoff, and retained-target
helpers. Tests cover long apartment-style loading, post-transition recovery and
timeout, repeated transitions, pause/resume, stale cancelled waits, polling bounds,
unknown targets, manual interruption decisions, detached target lifetime, changed
pre-craft inventory, and retained output-baseline calculations.

These pure tests do not execute native callbacks, UI lifetime, or task submissions.
In-game verification still required:

1. Enter apartment at gather handoff: expect waiting during loading, followed by
   current-inventory reconciliation, not AcquisitionFailed.
2. Repeat with unmet targets and AutoGather stopped: expect retained paused work;
   only explicit Resume restarts gathering.
3. Disable AutoGather manually, delete temporary list if desired, then Resume:
   retained targets must not become false completion.
4. Pause/cancel while zoning: no late restart after cancel, no actions while paused.
5. Exercise pre-craft zoning and output-confirmation recovery: no duplicate credit,
   no stale ingredient selection, and uncertain results pause for inspection.
6. Regression-check ordinary gathering, Standard/Pure Raphael crafting, repair,
   materia, collectables, and existing vendor travel/cleanup.

Phase 5 and unified GC reserve changes remain present; no saved-list recipe intent
or unrelated automation is rewritten by this patch.
