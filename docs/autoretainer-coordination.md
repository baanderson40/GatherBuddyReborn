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
2. Request a safe yield. Drain queued native actions; finish withdrawal/repair/
   extraction task chains; finish normal synthesis; request Quick Synthesis exit
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
   original saved list and resurrect completed crafts.

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
- Ordinary AR `IsBusy` can include GBR-owned Lifestream travel. Without Multi-mode,
  busy alone defers readiness polling; it is not treated as an external pass.
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

## IPC source contract

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
