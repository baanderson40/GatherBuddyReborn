# Crafting preparation after travel

## Scope

The 13:47 test recovered inventory successfully, but emitted job-switch deferral
diagnostics every frame while travel was still busy. One equip request preceded a
further transition. This patch gates crafting preparation on observed travel and
player readiness, rather than treating a visible screen as travel completion.

## Admission rules

- One side-effect-free gate serves wrong-job and already-correct-job paths.
- Independent blockers: zone transition, loading/fade screen, unavailable
  player/inventory, Lifestream busy, vnavmesh movement/pathfinding, or unavailable
  travel-status probes. Failed IPC probes fail closed; they do not grant admission.
- Before prep tasks run, before native equip, when entering solver/craft prep,
  before consumables, and before starting synthesis, the gate is checked again.
- After observed blockers clear, require 500 ms continuously clear readiness.
  A blocker returning resets stability. Repeated final checks remain valid without
  adding a delay before every action when readiness has stayed clear.
- Travel waiting has a distinct **Waiting for Travel / Preparation Readiness**
  state. No new equip, consumable, or synthesis requests are submitted while waiting.
  Recovery discards stale prep closures and reselects from current inventory.
- Native status is rechecked immediately before submission. A stability interval
  cannot prove that a later travel stage will not start after a command; subsequent
  transitions still use inventory recovery and actual job confirmation.

## Ownership and timeouts

- All active vnavmesh work is observed conservatively, including unrelated movement.
  Waiting never claims, stops, or restarts that navigation or Lifestream.
- Existing acquisition/vendor travel, receipts, gathering, repair, and materia
  engines retain their own execution and timeout behavior. Active synthesis and
  output confirmation are not suspended by this travel gate; zoning still uses
  the separate inventory-recovery logic.
- Stuck travel/probe status pauses preparation after 120 seconds of observed active
  travel-wait time. Paused and inventory-recovery/loading time do not consume it.
  Loading suspends rather than resets an existing travel budget. Explicit Resume
  after timeout starts a fresh travel-wait budget, without restarting travel.
- Pause/cancel/reset prevent continuation. New runs receive a fresh gate and
  diagnostic scope; cancelled gates cannot admit late work.

## Equip attempts

Checking whether an equip can be scheduled no longer consumes an attempt. Only an
actual `EquipGearset` submission starts its two-second confirmation window and
increments the three-attempt limit. Exiting crafting UI or blocked frames consume
no attempt. Inventory/travel recovery does not reset attempts; confirmed job change,
new job target, or explicit pause/resume/new run can reset them. Current job is read
again before a queued equip, preventing a redundant request after confirmation.
Failure to submit an equip pauses rather than spinning with no counted attempts.

## Diagnostics

Old per-frame combined deferral strings and queued-task wait logs are removed.
Run-scoped bounded throttling logs first blocker, changed flag sets, and identical
repeats at most every five seconds with suppression counts. Keys contain category;
state is stable blocker flags, not elapsed time or coordinates. Wait text is cached
when blockers change. Recovery logs once after stable admission. UI shows blocker
flags or the readiness-stability wait, not an acquisition failure.

## Verification

`CraftPreparationGateTests` links production gate, diagnostic throttle, and job
guard. Tests cover persistent blockers, stable admission, travel-stage gaps,
last-moment rechecks, timeout/pause/loading accounting, cancelled gates, bounded
diagnostics, schedule-versus-submission attempts, and preserved attempts across
zone recovery. Existing readiness, inventory recovery, output, acquisition,
policy/migration, budget, and ownership tests remain in the full suite.

Pure tests do not run native IPC, task dispatch, equipment changes, or UI callbacks.
Live checks still required:

1. Repeat gather/home-return/craft sequence: observe bounded reason-specific logs,
   no preparation while travel remains busy, and equip only when needed.
2. Repeat already on correct crafting job: synthesis and consumables must still wait.
3. Exercise brief gaps between travel stages and a transition after equip; confirm
   recovery rechecks actual job without resetting the attempt limit automatically.
4. Pause/cancel while waiting; verify no late native submission. Finish one craft
   and verify confirmed output advances the queue once.

The separate CSCore honk error (`0x80070490`, missing default audio endpoint) is not
changed here. Disabling Honk Mode remains the workaround. Earlier uncommitted
Phase 5, unified GC reserve, and inventory-recovery changes are preserved.
