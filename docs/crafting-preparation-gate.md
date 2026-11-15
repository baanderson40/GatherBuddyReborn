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

## Pre-craft home travel and gather handoff

Vulcan now honors the shared `AutoGatherConfig.GoHomeWhenIdle` setting, also exposed
in **Vulcan → Settings → General → Go Home When Idle**. There is no separate persisted
Vulcan toggle. `GoHomeWhenDone` keeps its independent gathering behavior.

- A queue owns one `PreCraftHomeTravelHost` for its run. If retainer restock work is
  enabled, `WaitingForRetainerTravel` runs before any bell navigation or withdrawal
  tasks are queued. Travel and stable readiness must finish before selecting a bell
  at the arrival location. Both automatic bell navigation and direct withdrawal use
  this gate. With the shared setting off, no command is sent; readiness still applies.
- Without retainer work, travel remains after material acquisition/gather reconciliation,
  before job switching and craft preparation. Inventory-ready queues take this path too.
- A completed pre-retainer receipt is consumed and reused before crafting when no
  gathering is required. Starting non-complete gather work invalidates that consumed
  receipt. The final GBR return (or Vulcan, if GBR did not submit one) supplies the new
  crafting-phase receipt. Creating an already-satisfied or empty gather list does not
  invalidate it. Resume/repeated gather notifications cannot reset pending, failed,
  cancelled, or unconsumed travel, so they cannot authorize an implicit retry.
  Subsequent recipes do not send the command again.
- At final gathering completion, GBR's `GoHome` uses that same host. It can submit
  the command or wait on an already submitted request; Vulcan consumes the receipt,
  rather than dispatching a second command. Idle trips between gathering targets do
  not satisfy the final handoff. The old `WentHome` flag is not completion evidence.
- Legacy single gather-and-craft requests have their own host. Direct recipe-browser
  crafts and quick synthesis wait on a fresh request inside `CraftingGameInterop`;
  queue/single handoffs explicitly mark travel as already handled, preventing a
  second request at that boundary. Direct requests already pending cannot be replaced.
- Blank commands normalize to `auto`; a leading `/li ` is removed case-insensitively.
  Standalone GBR and collectables use the same normalization.
- Command submission is reserved before invoking IPC. An exception never authorizes
  another attempt. Unrelated busy travel is observed, not adopted as this run's trip.
- After submission, Lifestream must report busy at least once, then idle with player,
  inventory, screen, crafting/gathering/casting, and navigation readiness continuously
  clear for 500 ms. Queue preparation still applies its independent read-only gate.
  An idle crafting stance is exited through the existing recipe-window task before
  submission; active synthesis is not interrupted by this host.
- Read-only receipt observation runs before teleport-data admission, including while
  loading, paused, or yielded. It cannot submit commands or exit crafting UI.
  Queue inventory recovery remains ahead of pre-craft command admission.
- No busy observation within 10 seconds, travel/readiness exceeding 120 seconds,
  missing/disappearing Lifestream, IPC exceptions, character/logout changes, or command
  changes before receipt consumption fail closed. Queue pauses with retained work;
  direct/single starts report an error instead of opening the recipe. Resume cannot
  silently retry a failed command: inspect travel and stop/restart the crafting run.
  Turning the setting off does not dismiss an already submitted request.
- Stop/reset/disposal cancels receipts and pending direct craft continuation. A fresh
  run gets a fresh receipt. Cancellation does not abort Lifestream or unrelated movement.

These are observed IPC/readiness receipts, **not destination verification**. Lifestream
commands that are invalid or no-op (including already being at the destination) may
never report busy; they block conservatively instead of being treated as successful.
Finish/inspect travel and restart with appropriate settings. The receipt applies to
each admitted retainer/crafting phase until non-complete gathering starts, not a guarantee
the player remains home after later manual movement or another automation interruption.
Existing collectables return-home handling is unchanged.

`PreCraftHomeTravelTests` covers ready ingredients, gather receipt reuse, disabled
settings, pending travel when disabled, busy/unready deferral, stability resets,
start/completion/readiness timeouts, command/character changes, cancellation/new-run
isolation, repeated recipes, and ambiguous submission failures. Compiled-plugin boundary
tests check wiring and shared command normalization. These are managed tests, not native
travel validation. Attended game checks still required: inventory-ready queue, GBR final
return with exactly one command, setting off, direct/browser quick synthesis, paused/
cancelled travel, already-home/no-op commands, and consecutive queue isolation.

Verification: 581 acquisition, 206 compiled-plugin compatibility, and 415 Adaptive
tests pass (1,202 distinct). Debug and Release solution builds pass with zero warnings
on this incremental run. Tracked diff and new travel source/test files pass whitespace
checks. Logs: `/tmp/opencode/pre-craft-travel-{acquisition-tests,compat-tests,adaptive-tests,debug-build,release-build}.log`.
No in-game travel trial, commit, or push performed.

### Pre-retainer ordering correction

The supplied 22:06 runtime trace showed bell processing at 22:06:18 and home command
submission only at 22:06:21 after withdrawal. Queue startup now defers the entire
retainer stage behind its own travel state; there is no departure-location bell target
to retain across teleport. Inventory recovery returns to this state, pause/AutoRetainer
yield cannot schedule bell tasks while it waits, and failure/cancellation keeps the
existing no-retry policy. The status window labels this phase separately from crafting.

Additional managed regressions cover retained pre-retainer receipts without gathering,
fresh receipts after gathering, repeated departure notifications, disabled phases,
failed/cancelled/unconsumed receipt protection, deferred bell-stage wiring, and the
new non-recipe UI state. Native checks still needed: start away from home with restock
and navigation on/off; verify command before bell discovery; verify no second command
for retainer-only→craft; verify fresh final return after real gathering; pause/stop/yield
while traveling; check consecutive-run isolation. No new in-game validation claimed.

Correction verification: 587 acquisition, 207 compiled-plugin compatibility, and 415
Adaptive tests pass (1,209 distinct). Debug/Release builds pass with zero warnings on
this incremental run. Logs:
`/tmp/opencode/pre-retainer-travel-{acquisition-tests,compat-tests,adaptive-tests,debug-build,release-build}.log`.

### Coordinated AR relog before home travel

The supplied 08:36–08:37 trace completed an AR single pass and original-character
return, then immediately paused `WaitingForRetainerTravel` with "Character changed
or logged out". Root cause: framework home-receipt observation correctly ran during
loading, but incorrectly treated the coordinated AR logout as an unowned character
change. Queue preparation reset could not clear that sticky receipt error.

The integration now binds the run's exact home-travel host to coordinator owner and
handoff ID after safe yield, before parking and AR submission. Queue and legacy
single gather→craft hosts support this lifecycle. While suspended, neither observer
nor command admission reads alternate-character travel/player status or submits
work. The pure receipt returns Waiting before character checks, cannot consume/reset
or submit, and does not collect AR return travel as home-command busy evidence.

Coordinator release must match owner, handoff ID, receipt instance, and original
content ID. It occurs after the coordinator's original-character readiness gate.
Only that suspension interval is excluded from readiness/start/completion deadlines;
already-spent budget, pending submission, busy evidence, and consumed state survive.
Partially established readiness is reset. An unsubmitted trip still needs its normal
home command after return; a submitted trip cannot be automatically reissued.

Stop/reset/unload still cancel, wrong return identity fails closed, and suspension
cannot erase an existing error. Manual/uncoordinated logout remains an error. Already
poisoned queues from the old code require a stop/restart; this patch does not heal
arbitrary failed travel transactions. No native acceptance claimed: repeat AR-before-
teleport→return→home→bell, cancellation/recovery, and no-AR/manual-logout checks in game.

Managed regression drives the production AR coordinator through pass startup, logout,
alternate-character work, original-character return/settling, and one subsequent home
submission without a sticky logout error. Other cases cover pre-spent readiness/start/
completion budgets, duplicate park, exact handoff/character validation, cancellation,
existing errors, consumed receipts, and compiled yield/release/observer wiring.
Verification: 620 acquisition, 211 compatibility, and 415 Adaptive tests pass (1,246
distinct). Debug/Release builds pass with zero warnings on this incremental run;
tracked diff and affected suspension files pass whitespace checks. Logs:
`/tmp/opencode/ar-home-suspension-{acquisition,compat,adaptive,debug-build,release-build}.log`.
No in-game test, commit, or push performed for this fix.

### Idle polling with housing-interior fast path

Arrival now additionally requires `!Lifestream.IsBusy()` and live player readiness,
plus either native `HousingManager.IsInside()` or two continuously idle/ready
seconds. The existing final 500 ms receipt/preparation checks remain conservative
additional gates; the interior branch skips the new outdoor two-second interval,
not all existing stability checks. Housing state is read only after readiness and
idle checks. This is not the older `IsInHousing()` helper, which can include housing
district outdoors. No territory-ID list, selected-address inference, or change to
Lifestream's `auto` destination/entry settings is used. Inns/outdoor routes can use
the timed fallback, so indoor entry is not universally required.

Busy, zoning/loading, inventory/player unreadiness, or navigation blockers reset
the arrival timer. Idle before command submission does not count. New submissions,
gathering-departure invalidation, and coordinated AR suspension/release reset it.
A Ready receipt is revocable until consumed if another busy/unready stage begins;
this does not authorize reissuing its command. Start acknowledgement, timeouts,
deduplication, and no-op/uncertain-command failure rules remain unchanged.

Recheck arrival before selecting a new bell navigator and before initial withdrawal
planning/bell interaction. Once the executor owns its UI session, its existing
withdrawal guard continues instead of requiring an unoccupied player between every
menu step. Likewise, do not block our own active bell movement as unrelated nav work.
The navigator rechecks Lifestream busy plus current player/inventory, territory, and
object identity before taking a completed path into movement. A returning travel stage
therefore blocks new bell access even after the original receipt was consumed.

The supplied trace's 1.327-second apartment-selection→zoning gap fits inside the new
two-second outdoor window. This is still a heuristic: an idle/ready gap longer than
two seconds can admit outdoors, and being inside housing is not exact-address proof.
Managed regressions cover that gap, mid-route busy resumption, exact timer boundaries,
readiness resets, mandatory idle on the interior branch, final receipt stability,
Ready revocation, native housing-reader wiring, and bell-entry gating. Attended
checks remain needed for apartment entry, outdoor housing, inns, active withdrawal,
and AR-before-home-return. No native acceptance, commit, or push claimed here.

Verification: 632 acquisition, 212 compatibility, and 415 Adaptive tests pass (1,259
distinct). Debug/Release solution builds pass with zero warnings on this incremental
run; tracked diff and new arrival files pass whitespace checks. Logs:
`/tmp/opencode/arrival-readiness-{acquisition,compat,adaptive,debug-build,release-build}.log`.

Supplied inn trace: Lifestream's final task ended at 09:44:17.221, inn territory
transition followed at 09:44:19.218, player/teleport readiness returned at 09:44:21.430,
and bell navigation began at 09:44:23.969. Craft startup followed at 09:44:26.393,
with no duplicate home command in the supplied segment. This supports the timed inn
arrival path only: no actual item withdrawal or full-queue completion was shown.
