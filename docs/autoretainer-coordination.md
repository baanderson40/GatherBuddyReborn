# Shared AutoRetainer coordination

## User settings

GBR's existing **Wait for AutoRetainer Multi-mode** setting now controls both
standalone AutoGather and Vulcan queues. The same controls appear in
**Vulcan > Settings > AutoRetainer**; these are not separate configurations.

- **Ventures**: selected retainers, on AR-enabled characters, within GBR's venture
  threshold. Existing configurations default to this mode.
- **Deployables**: airships/submarines reported ready by AR, on workshop-enabled
  characters. Uses AR's advance timer and wait-for-all settings, not GBR's venture
  threshold.
- **Both**: either readiness category requests a pass; AR processes both categories.
- **Prioritize timed gathering nodes** defers either category while an applicable
  legendary/unspoiled gathering target is active/available. Diadem defers relogging
  without changing the saved setting.

Disabled configurations remain disabled. No saved shopping list, AR configuration,
purchase reserve, or spending cap is changed by this feature.

## Handoff

1. Poll AR readiness at most every two seconds while an automation owner is active.
2. Request a safe yield. Drain queued native actions; finish repair/extraction
   task chains; suspend pending bell tasks and close any owned withdrawal interaction;
   finish normal synthesis; request Quick Synthesis exit
   and confirm actual output. Stop owned gathering movement and exit fishing.
3. Suspend acquisition without discarding allocations, settled spending, or policy
   snapshots. Submitted purchases settle and shop cleanup completes before AR starts.
4. Park queue clocks and block plugin action producers, including the independently
   subscribed AutoGather task manager. Vulcan owns its child gathering handoff:
   there is only one pass request.
5. Call `AutoRetainer.PluginState.EnableSingleMultiMode` with the selected category.
6. Await `OnSingleMultiModeFinished`, then wait for AR mode/tasks to become idle.
7. Return to the original content ID through Lifestream when logged in. AR `Relog`
   can be used at a ready title screen only. Lifestream availability is required
   before handing off because a normal pass finishes on a logged-in character.
8. Require original-character identity, loaded bag containers, screen readiness,
   idle travel/navigation, and 500 ms observed stable readiness before releasing.
9. Refresh gathering targets and invalidate crafting readiness/job selection.
   Continue existing acquisition/inventory-confirmation paths; do not rebuild the
   original saved list and resurrect completed crafts. A suspended retainer stage
   rebuilds withdrawal requirements from current inventory after AR settles.

Yielding is not immediate preemption: a long withdrawal or repair task chain can
delay AR. Unrelated vendor lists/collectable runs/navigation block handoff and are
not stopped. Starting new plugin automation during an active handoff is rejected.

AR decides which work its pass actually processes. GBR's venture threshold requests
the handoff; it does not override AR's eligibility, advance timer, lockouts,
wait-for-all, fuel, inventory, or venture-resource rules. Undeployed vessels alone
do not trigger a pass. AR's configured inventory-management operations may change
materials: configure AR protections appropriately; Vulcan revalidates actual stock.

## Ownership and recovery

- Existing external Multi-mode is never converted to an owned pass or disabled.
  If it begins during automation, the coordinator requests yield and waits without
  claiming AR ownership. Unexpected character changes block inventory consumers.
- Ordinary AR `IsBusy` can include GBR-owned Lifestream travel. In Monitoring,
  busy alone defers readiness polling; it is not treated as an owned pass. If AR
  mode/tasks become active after safe yielding but before our pass starts, the
  coordinator observes external activity until idle instead of starting another
  pass or failing merely because the activity appeared during handoff.
- Normal completion never calls AR `AbortAllTasks` or `DisableAllFunctions`.
- Manual Pause prevents automatic automation resume without cancelling the AR pass.
- **Stop Queue cancels its owned AR pass.** Both queue stop paths notify the shared
  coordinator using the stopped run's captured identity, even after the local queue
  has been discarded. A second Stop can retry a failed/stale Vulcan cancellation.
  Unsubmitted handoffs detach immediately. Observed external AR runs are never
  disabled; their ongoing mode/busy state still prevents unsafe new starts.
- Owned cancellation calls `AutoRetainer.PluginState.DisableAllFunctions` to stop
  further Multi-mode/retainer/voyage scheduling. It does **not** call `AbortAllTasks`:
  already queued transactions and relogs must settle. Scheduler disable is throttled
  and reasserted once the queue is idle, because a draining task may re-enable one.
  Only then are leftover owned menus/dialogues closed and original-character return
  completed. If UI cleanup is blocked, status identifies the remaining interaction.
- Cancellation does not wait for `OnSingleMultiModeFinished` or require readiness
  APIs. Startup shows **Stopping AutoRetainer…** while cleanup/return still holds
  the gate. On success, ownership and the stale handoff are cleared; the stopped
  queue never resumes and new queues can start. Cancellation errors/timeouts keep
  the safety gate and allow explicit Stop retry or manual recovery.
- Disabling integration during a pass prevents future handoffs; it does not release
  crafting on an alternate character.
- Missing APIs before handoff produce compatibility status without falling back to
  continuous mode. Missing APIs during handoff retain work behind recovery gates.
- A stopped mode without its completion event, rejected return, or timeout requires
  explicit recovery. Stop AR, restore required IPC if unavailable, return to the
  original character, then use **Recover AutoRetainer handoff**. Queue work remains
  paused for inspection and manual Resume; standalone gathering must be re-enabled.
- A 30-second post-handoff debounce prevents rapid repeat requests. Two consecutive
  passes with identical readiness allow no third pass until explicit recovery or
  readiness clears/changes. This is conservative repeat-trigger protection, not
  proof that AR made no inventory progress.
- Pass startup timeout: 15 seconds. Pass/external-mode wait: 60 minutes. Cleanup:
  3 minutes. Return/readiness: 5 minutes. No unsafe automatic abort on timeout.
- Unload unsubscribes IPC but does not abort AR. Cross-plugin-unload/game-restart
  resumption is not supported.

## Vulcan retainer withdrawal and bell suppression

### Reported startup race

On 2026-11-04, run `9c6c1eff-10a5-484e-a556-c8ef4b4cd4b8` started the Shark-class
queue beside a bell with retainer restock enabled. Handoff
`23189b2b-6d23-4124-b486-5252ac65d435` entered Yielding at 11:52:12.619, but queued
withdrawal tasks still opened the bell and advanced planning. AR enabled itself
with reason Access at 11:52:12.716 and queued venture work. Vulcan then reported no
withdrawals, entered acquisition, and failed the handoff because external AR work
had started. An AR task timed out at 11:52:33.323. The overlap is established; the
exact cause of that AR task timeout and the empty withdrawal plan is not proven.

### Updated sequencing

- Withdrawal planning is queued, not executed synchronously at startup. Shared AR
  monitoring gets an opportunity to yield before any bell interaction.
- With shared coordination enabled, configured pending AR work goes first. Existing
  AR tasks/Multi-mode always block Vulcan bell ownership. Without shared coordination,
  the guard does not request a pass but still protects withdrawal from bell auto-start.
- Once AR is idle and existing interactions are closed for 500 ms, the guard reads
  `AutoRetainer.GetSuppressed`. If false, it records restoration intent and calls
  `AutoRetainer.SetSuppressed(true)`, verifies the result, and rechecks busy/mode.
- If already suppressed, Vulcan borrows that protection and never clears it.
- An active withdrawal lease blocks new shared handoff admission. Work that becomes
  ready during the owned bell session waits for its completion/cleanup.
- Every withdrawal executor tick verifies the lease, AR activity, and suppression.
  Availability/activity/flag changes pause withdrawal instead of blindly reasserting
  suppression or cancelling external tasks. Character identity is captured as well;
  a change pauses withdrawal, and cleanup never closes another character's menus.
  Fresh planning is required on Resume.
- While yielding, retainer tasks no longer run through ProcessTasks and accidentally
  advance into acquisition. Pending work is suspended; owned interactions are closed
  before parking. After return, current inventory and retainer demand are rebuilt.
- Acquisition cannot start until owned bell cleanup has completed and suppression
  restoration has settled. Completed crafts, purchase policy, and run spending are
  not reset by this rebuild.

### Cleanup and limitations

Stop, Pause, Reset, queue completion, managed retainer failures, and owner removal
request release. Cleanup lives in the framework integration rather than the queue,
so stopping/removing a queue cannot abandon its suppression lease. New automation,
including Marketboard travel, is blocked until the lease is released.

Cleanup never aborts AR queues or changes Multi-mode. It waits for stable player
readiness and external AR tasks to be idle, cancels owned quantity input, hides
owned retainer inventory, dismisses closing dialogue, and cancels owned menus.
Unknown Yes/No confirmations are not accepted; close them manually if cleanup is
blocked. Bell occupancy, retainer agent, and relevant dialogs must be closed for
500 ms before restoring a flag Vulcan changed. Existing external suppression is
preserved. If AR unloads, no suppression IPC remains available; closed-bell ownership
can be released without an IPC write, with status requesting suppression inspection
if AR reloads. Missing IPC on a loaded AR retains the gate
for retry/manual recovery rather than treating an error as idle.

On unload, cleanup/restoration is attempted only with ready player state, idle AR,
and closed interactions; the extra closed-state debounce can be skipped during
disposal. If cleanup cannot finish synchronously, the plugin logs a warning and
leaves suppression in place rather than re-enable AR inside an open bell session.
Close the interaction and inspect/clear AR suppression manually in that case.
Do not blindly clear a flag that was already set before Vulcan; it may belong to
another plugin or the user's existing workflow.
Unloading cannot provide asynchronous cleanup after the plugin is gone.

**Global-boolean limitation:** suppression has no caller ID, lease, atomic acquire,
or reference count. Vulcan restores only when it initially observed false and its
write may have taken effect. It cannot detect another plugin writing true while
the flag was already true during the lease. This IPC cannot provide perfect
cross-plugin ownership; do not describe it as an AR-owned lock.

Status appears in the compact window during the retainer stage and bounded
state-change logs use `[Retainer coordination]` (at most one Debug update per five
seconds, retaining the latest pending status and suppressed-update count). No saved AR settings, processing
mode, vendor policy, list, or queue progress is changed.

### Verification

Managed tests cover AR-first scheduling, busy/mode exclusion, readiness/idle
stability, borrowed suppression, late pending work, foreign owners, ambiguous IPC
writes, restore retry, flag changes, load/unload behavior, cleanup after Stop, and
external activity appearing during yield. Actual-plugin IL checks cover deferred
planning, guarded executor calls, protected acquisition transitions, lifecycle
release, and yield/restart wiring. Native IPC and menu callbacks are not executed
by these tests.

Automated evidence: 510 acquisition/source tests and 103 actual-plugin compatibility
tests pass (613 total). Forced Debug/Release builds and `git diff --check` pass;
full builds retain only the two existing ElliCon/ElliLib nullable warnings.

Pending controlled acceptance:

- [ ] Start at a bell with ventures ready: AR first, then fresh Vulcan withdrawal.
- [ ] No ready AR work: suppression acquired before bell access; restored after exit.
- [ ] Work becomes ready during withdrawal: no competing scheduler or pass.
- [ ] Existing external suppression remains true after completion and Stop.
- [ ] Pause/Stop at bell, inventory, numeric-entry, and cleanup stages; no auto-resume.
- [ ] Unavailable APIs, blocked confirmations, and reload/unload report recovery safely.
- [ ] Normal AR return, retainer withdrawal, acquisition, and confirmed crafting progress.

## IPC source contract

### Bell navigation (follow-up patch)

Bell navigation startup now only enqueues work. A read-only admission check runs
before starting and on every active navigation tick. It waits for readiness,
configured unsuppressed AR work, external busy/Multi-mode activity, handoff, and
previous owned cleanup. It never changes suppression or starts AR. Disabled shared
coordination does not request a pass; existing external suppression remains borrowed.
Initial start, Resume and AR release share this entry path.

The navigator submits `Nav.PathfindCancelable` with its own cancellation token.
That API only returns waypoints: no continuation launches movement. Completed paths
are validated and submitted using `Path.MoveTo` on the framework thread. Stop,
Pause, Reset, yield, timeout, arrival and failure cancel/discard pending results.
Framework-owned cleanup tracking survives cleared/discarded queues and runs before
Marketboard/AR automation gates. Withdrawal, new automation and safe AR yield wait
for settlement. After AR, bell lookup and withdrawal planning are rebuilt.

Stopping and interaction range is the live bell hitbox radius plus 2.5m, shared by
housing and public bells. Approach candidates form a ring one metre inside that
range (hitbox radius plus 1.5m). Bounded nearest-mesh projections avoid targeting the bell
center. Navigation and interaction share horizontal range, a separate 2.5m vertical
bound, and a full-distance limit of hitbox radius plus 2.5m. Already-in-range bells require no
path. Missing/unreachable approach paths fail without repeatedly pushing into the
center; guarded withdrawal can still check whether interaction is possible.
The vertical bound and approach margins require native acceptance at awkward bells.

**vnavmesh ownership limits:** `Path.Stop` does not cancel pending SimpleMove
requests. This navigator no longer submits those requests or calls global
`Nav.PathfindCancelAll`. The inspected provider's FollowPath can nevertheless
trigger AsyncMoveRequest's global RetryOnStuck handler, including for direct paths.
No operation ID, retry-specific cancellation or retry-setting IPC exists there.
Cleanup stops only an exact remaining suffix of the submitted waypoint list.
If a different path or pending SimpleMove appears, cleanup waits until idle and
blocks handoff rather than cancelling unknown movement. This can require manual
vnavmesh recovery if an automatic retry remains stuck. An identical path submitted
by another caller is indistinguishable; waypoint matching is evidence, not perfect
ownership. No global movement/retry configuration is modified.

Navigation logs use `[RetainerBellNavigator]`; admission and blocked-cleanup
messages are bounded to one per five seconds. Unload cancels owned queries and
attempts matching-path cleanup synchronously, warning if unresolved; no asynchronous
plugin cleanup is possible after unload.

Follow-up automated evidence: 528 acquisition/source tests + 106 actual-plugin
compatibility tests pass (634 total). Forced Debug/Release builds and
`git diff --check` pass; only the two existing submodule warnings remain.
Native path following is not executed by
these tests. Required live checks: AR-ready startup stays stationary; normal
approach stops within interaction range; newly-ready AR waits for settlement;
Pause/Stop during pending/active navigation never resumes; elevated bells reject
wrong-floor projections; withdrawal continues into acquisition without lost progress.

Controlled native evidence (2026-11-04):

- 12:52:07: withdrew 108 NQ Hardened Sap from a stack of 198 under scoped AR
  suppression; cleanup, zero-shortage reconciliation and crafting startup followed.
- The intermediate hitbox + 3m boundary failed native bell access at 13:01:56
  (`Timed out waiting for OccupiedSummoningBell`) despite managed navigation success.
  It was replaced with hitbox + 2.5m; do not treat geometry alone as native confirmation.
- Public bell at 13:06:40-46: query/approach settled in 2.73 seconds, reached withdrawal
  planning, needed no pulls, reconciled with zero shortages and began craft preparation.
- Housing bell at 13:07:37-42: same sequence, approach settled in 1.54 seconds.
- Current-range AR-ready handoff, Stop/Pause during navigation, awkward-floor geometry,
  borrowed suppression and unload recovery remain pending native acceptance.

Reviewed source in `ffxiv-knowledge/reference-plugins/AutoRetainer`:

- `AutoRetainer/Modules/EzIPCManagers/IPC_PluginState.cs`
- `AutoRetainer/Modules/Multi/MultiMode.cs`
- `AutoRetainer/Modules/IPC.cs`
- `AutoRetainerAPI/AutoRetainerAPI/Configuration/MultiModeType.cs`

Read-only calls: `AutoRetainer.GetRegisteredCIDs`,
`AutoRetainer.GetOfflineCharacterData`, and `AutoRetainer.PluginState.` prefixed
`GetClosestRetainerVentureSecondsRemaining`, `AreAnyEnabledVesselsReady`,
`GetMultiModeStatus`, `IsBusy`, and `CanAutoLogin`.

The local processing enum preserves AR wire values Retainers=0, Submersibles=1,
Everything=2. Dalamud's call gate converts cross-assembly nullable enums/DTOs through
JSON (`CallGateChannel.CheckAndConvertArgs` / `ConvertObject`). No AR DLL dependency
or reflection into AR internals is introduced.

**Do not substitute `EnableMultiModeSingle()`:** in reviewed source it invokes a
command that does not initialize actual single-pass tracking. Completion is scoped
to the active owned request; AR exposes no caller-provided operation ID. `IsBusy ==
false` alone never completes an owned pass. Single-pass completion leaves the player
on the last character, so explicit original-character return remains necessary.

## Automated verification

The acquisition test project links the production coordinator/readiness sources.
Coverage includes mode routing, threshold boundaries, nullable readiness, disabled/
excluded characters, safe-yield gating, synchronous completion, idle busy gaps,
external modes, return acceptance versus actual login, stable readiness, unexpected
character changes, missing IPC, timeouts, manual pause/stop, owner changes, repeated
readiness, and intentional Quick Synthesis attempted-count handling.

These tests do not host Dalamud or emulate native shop/craft/AR execution. Passing
tests/builds are not native acceptance evidence.

Automated evidence (2026-11-03):

- Acquisition suite: **329 passed** (including 58 new coordination/readiness/result
  cases linked to production sources).
- Actual-plugin compatibility suite: **38 passed** (including 10 new configuration,
  wire-format, DTO projection, and actual queue-parking cases).
- Forced Debug and Release solution builds passed. Main plugin introduced no
  warnings; only the two existing nullable warnings in unchanged ElliCon/ElliLib
  remain. See `warning-remediation.md`.
- Compatibility parking tests reference installed SDK Lumina/ClientStructs managed
  assemblies using configurable `DalamudLibPath`; they do not invoke native game
  functions.

Commands (repository root, Windows .NET from WSL):

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" build GatherBuddy.sln --configuration Debug -p:Platform=x64 --no-incremental --nologo
"/mnt/c/Program Files/dotnet/dotnet.exe" build GatherBuddy.sln --configuration Release -p:Platform=x64 --no-incremental --nologo
"/mnt/c/Program Files/dotnet/dotnet.exe" test tests/GatherBuddy.Acquisition.Tests/GatherBuddy.Acquisition.Tests.csproj --configuration Debug --nologo
"/mnt/c/Program Files/dotnet/dotnet.exe" test tests/GatherBuddy.Compatibility.Tests/GatherBuddy.Compatibility.Tests.csproj --configuration Debug --nologo
```

## Native acceptance — partial live verification

User-provided logs on 2026-11-03 confirm the normal Vulcan handoff for run
`a7b8cd5b-a7c5-42aa-801a-041a863b1e63`, handoff
`0d82c45a-0a05-4b85-8618-ffc3696a9acf`:

- 16:38:40–41: ready venture detected; Vulcan yielded; single pass started. User
  confirmed AR was processing.
- 16:58:17: single-pass completion observed; original-character return requested.
- 16:59:10–21: original character/readiness settled; handoff released normally.
- 16:59:21–25: same run reconciled materials and resumed Iron Ingot craft 1/620,
  entering live synthesis with the cached Raphael solution.

This confirms normal pass completion, original-character return, and automatic
queue resume. The supplied excerpt does not establish craft completion or native
Stop/cancellation behavior. Repeated legacy-movement log messages are separate from
handoff success and have not been changed here.

Use small lists, low purchase caps, selected test characters, and known AR settings.
Capture `[AR Handoff]` transitions plus `[Vulcan Crafting][run][Result]` and purchase
receipts. Verify inventory/spending, not just displayed state.

- [ ] Existing config: disabled remains disabled; enabled defaults to Ventures;
      GBR/Vulcan controls edit the same values and survive reload.
- [x] Verify typed single-pass IPC and completion event against installed AR (live
      normal Vulcan pass above).
- [ ] Standalone gathering: Ventures, Deployables without ready ventures, Both.
- [ ] Fishing/spearfishing exit, AutoHook quiescence, gathering-node exit.
- [ ] Timed-node priority for both work categories; Diadem deferral.
- [ ] Materials-ready Craft Only list yields without a gathering phase.
- [ ] Vulcan child gathering produces exactly one shared handoff.
- [ ] Normal synthesis settles before AR starts; confirmed output retained.
- [ ] Interrupt Quick Synthesis at zero/partial progress; unattempted work retained;
      failed attempted crafts still require inspection.
- [ ] Purchasing: pause before submission and during submitted batch; exact spend,
      reserves, run cap, cleanup, allocations and retry budget preserved.
- [ ] Retainer withdrawal, NPC/self repair, and materia extraction settle safely.
- [ ] Solver/job-switch/travel stages resume with fresh readiness and correct job.
- [ ] Alternate-character stock never satisfies original gather/craft requirements.
- [ ] AR finishes on another character; return content ID and loaded bags verified.
- [ ] Manual Pause/Stop, setting disable, rejected return, missing IPC and manual AR
      interruption do not release alternate-character automation.
- [ ] Stop during owned Ventures/Deployables/Both passes stops further scheduling,
      drains submitted work, closes owned menus, returns to original character, and
      allows a new queue without requiring a single-pass completion event.
- [ ] Stop before pass submission, during login/cleanup/return, and after a failed
      handoff; repeated Stop is safe and never resumes the discarded queue.
- [ ] Stop while merely observing external Multi-mode leaves that mode untouched.
- [ ] Existing external mode is not restarted, converted, aborted, or disabled.
- [ ] Unrelated shopping/collectables/navigation blocks without being stopped.
- [ ] Persistent eligibility mismatch triggers repeat-pass protection/recovery.
- [ ] Small full mixed acquisition/crafting list completes without duplicate output,
      purchases, saved-list changes, or resurrected completed work.
