# Vulcan Phase 6: live verification

Personal experimental fork, not an official release. Unit tests/builds do not prove
native currency, travel, recipe-window, or callback behavior. No test below is
marked passed merely because its pure helper has automated coverage.

## Recorded evidence

Committed implementation: `42188db` (Phase 5, unified GC reserve, inventory recovery,
travel/preparation gate). Logging throttling: `d653df9`. Raphael setup: `62967c6`.

User-provided run on 2026-11-03, 13:59:50–14:02:27:

- List `Bronco-type Aftcastle x1`, ID 3503, 438 expanded crafts, ListOverride enabled,
  retainer restock disabled. Run/session `a46c3d10-186b-4d2e-84fa-8b933536187a`.
- All 13 unique Pure Raphael solves succeeded.
- Bought 55 Iron Ore for 990 gil; receipt accounting certain and cleanup completed.
- Started gathering two material types, with no unresolved shortages reported.
- Recovered through zone transitions and gathered before the crafting handoff.
- Preparation waited for Lifestream (89 repeated diagnostics suppressed), rejected
  a 300 ms clear gap before another transition, then waited 500 ms stable readiness.
- One equip request for gearset 24/job 9 at 14:02:13.866, after travel recovery.
- Iron Ingot recipe 43 finished at 14:02:21.859; queue advanced to craft 2/438.
- User stopped during second craft preparation. The excerpt shows no subsequent
  synthesis start, then return to IdleNormal. This is not full-list completion.

This demonstrates the ordinary integrated acquisition-to-first-craft path. It does
not demonstrate blocked-work bypass, retry spending, reserve boundaries, failed
Quick Synthesis accounting, or every cancellation/ownership scenario.

## Status matrix

| ID | Scenario | Native status |
| --- | --- | --- |
| V01 | Gil purchase, certain receipt, cleanup | Observed: 55 Iron Ore / 990 gil |
| V02 | Residual gathering, zoning recovery, travel stability | Observed in recorded run |
| V03 | Pure Raphael craft completion and next queue entry | Observed once; list stopped on entry 2 |
| V04 | Blocked precraft bypassed by independent work | Pending |
| V05 | Dependent final recipe remains blocked | Pending |
| V06 | Recheck resumes only retained work, no purchases | Pending |
| V07 | Explicit retry uses current remainder and cumulative gil cap | Pending |
| V08 | Unified current-GC reserve boundary and gil fallback | Pending |
| V09 | Global/custom/disabled policy; zero persists after reload | Pending |
| V10 | Mandatory HQ/NQ and shared stock | Pending |
| V11 | Multi-output precraft credits one craft, correct output units | Pending |
| V12 | Quick Synthesis success, failure, partial/interrupted batch | Pending |
| V13 | Normal failure/cancellation retains unconfirmed work | Pending |
| V14 | Pause/resume/cancel at travel, gathering, preparation, purchase | Partial: only recorded preparation stop; broader checks pending |
| V15 | Already-correct-job preparation waits for travel | Pending |
| V16 | Manual gather stop/deletion; explicit resume; zoning cancellation | Pending |
| V17 | Repair, materia, collectables and Standard Solver regression | Pending |
| V18 | Small mixed list completes with no lost/duplicated work | Pending |
| V19 | Entire workshop list completes without manual recovery | Pending |
| V20 | Housing-source vendor navigation | Pending |

## Controlled test setup

1. Use a small saved list on this experimental installation. Keep the 438/1,389
   craft lists for final acceptance, not first fault isolation.
2. Record active policy origin, allowed currencies/priority, gil purchase cap, gil
   reserve, unified GC reserve, solver, and retainer setting before starting.
3. Use a low purchase cap and protective reserves; teleport fees are extra. Disable
   retainers when deliberately withholding materials so restock does not erase the
   test shortage. Choose a genuinely unavailable non-gatherable input for bypass
   tests; a gatherable shortage may be resolved by gathering before crafting.
4. Record relevant NQ/HQ stock and currency balances. Stop if completed work repeats,
   quantities disappear, or purchase spending exceeds the configured cap. Purchases
   already submitted can settle; Stop does not roll back currency or acquired items.

## Priority test sequence

### A. Partial progress and recheck (V04–V06, V18)

- Build a small list containing a blocked precraft, its dependent final recipe,
  and an independent recipe with all immediate inputs owned. Disable purchases or
  make the blocked input unavailable under policy. Prevent retainer/gather fallback
  from satisfying it accidentally.
- Expect independent work to complete, dependent work to remain, and the queue to
  enter Blocked, not Complete. Confirm UI separates confirmed crafts, skips, and
  remaining work. Do not use explicit Skip to manufacture a blocked test.
- Add the missing input, close conflicting interactions, then click **Recheck
  materials (no purchases)**. Expect only remaining entries to resume, with no
  vendor purchase. Let this entire small list finish.

### B. Explicit acquisition retry (V07)

- Arrange an initial purchase followed by retained blocked work with a purchasable
  remainder. Record confirmed spending before retry; keep the captured run cap low.
- Close crafting/vendor windows and click **Retry acquisition**. Expect a fresh
  acquisition session under the same run ID, current-inventory planning, unchanged
  policy snapshot, and no resurrection of completed precrafts.
- Total purchase gil across attempts must stay within the original run cap. Do not
  count teleport charges as purchase-cap spending. If funds/cap prevent buying the
  remainder, expect gathering fallback or retained shortage, not a route loop.

### C. Unified GC reserve and policies (V08–V09)

- Confirm one GC seal reserve in global and custom list settings, with correct
  read-only GC/currency. Set reserve near actual balance and request a small material
  purchase whose full cost would cross it. Only the affordable portion may be bought;
  current-GC balance must remain at or above reserve.
- Repeat with gil fallback allowed, then purchasing disabled. Check inheritance
  versus Custom; edit settings during a run and verify the active snapshot does not
  silently weaken. Set reserve zero explicitly, reload, and verify zero persists.
- GC transfer is not required for initial acceptance. Alternate membership/native
  transfer behavior remains unverified until actually exercised.

### D. Output, quality, and lifecycle checks (V10–V17)

- Test mandatory HQ with only NQ available, strict NQ with only HQ, shared inputs
  sufficient for one craft, and a multi-output precraft feeding another recipe.
- Test normal success, deliberately failed/cancelled synthesis, small Quick Synthesis
  batches, and interruption. Credit only actual successful output. Inspect uncertain
  outcomes before explicitly resuming to avoid repeating an already-produced item.
- Pause/resume and cancel independently during travel, gathering, preparation, and
  purchasing. No new queue action may follow cancellation; submitted purchases may
  finish through existing settlement/cleanup.
- Enter apartment during gather handoff and pre-craft waits. Stop gathering manually
  with targets remaining, optionally delete the temporary list, and confirm only
  explicit Resume restarts it. Repeat already on the correct crafting job.
- Exercise repair/materia/collectables and Standard/Pure Raphael separately; record
  exact interruption and resume points instead of inferring coverage from a list run.

## Evidence and result diagnostics

Capture logs from queue start through Blocked/Complete/Stop. Do not filter out all
crafting lines by selecting only `[Vulcan Acquisition]`.

After result settlement the queue emits one Debug diagnostic:

```text
[Vulcan Crafting][run-id][Result] recipe=...; outputItem=...; before=...; after=...; yield=...; requested=...; attempted=...; confirmed=...; cancelled=...; index=...->...; confirmedTotal=...; explicitlySkipped=...; remaining=...
```

This reports the existing inventory-delta confirmation decision, not a new proof
source. Inventory changes from manual movement/other automation can make inference
uncertain; keep the test isolated and record them. Quick Synthesis attempt counters
and opening a recipe are not successful crafts. Counts refer to expanded craft
entries, not output item units; explicit skips are not confirmed successes.
Shortage lines are labeled as the last reconciled snapshot, not a continuously
updated inventory feed; use Recheck after manually adding materials while blocked.

Use this result template:

```text
Scenario ID / build commit:
List / run ID / acquisition attempt IDs:
Policy origin, allowlist/priority, cap/reserves, solver/retainers:
Initial relevant NQ/HQ inventory and balances:
User actions, including manual inventory changes:
Expected / actual remaining work and spending:
Final UI state and confirmed/skipped/remaining counts:
Log interval / pass, fail, or inconclusive:
```

## Exit criteria and separate work

All safety scenarios must be demonstrated, followed by full completion of one small
mixed list and then one workshop list without manual recovery. Preserve explicit
unknowns, including housing. Automated suite and Debug/Release builds must remain
passing; known warnings (including MessagePack vulnerabilities) are not suppressed.

Successful Raphael lifecycle log reduction and missing audio-endpoint handling for
Honk Mode remain separate maintenance patches. Do not expand scheduling behavior
without a demonstrated gap from these tests.
