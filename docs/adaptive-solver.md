# Adaptive Solver: offline engine and lifecycle controller

## Status

**Current: selectable Adaptive (Experimental); selection authorizes execution and
automatic queue progression, without separate runtime arming.** See
[activation and compatibility](adaptive-option.md). Existing defaults
remain unchanged. The slice descriptions below record implementation history;
their earlier "not selectable" statements describe those stages, not current UI.

The offline engine slice exists: simulation audit tests, immutable
inputs/goals, pure simulation adapter, bounded completion planner, bounded
beam-search engine, continuation replay, exhaustive toy comparisons, and a
synthetic per-turn benchmark.

The second slice adds the pure async decision coordinator and one-use execution
leases, with controlled worker-ordering tests. Execution leases remain disconnected
from native execution and solver selection; the fifth slice adds read-only observation.

The third slice adds pinned independent Rust reference export and frozen action
fixtures, real-recipe comparisons, corrected quality rounding/Adaptive combo
eligibility, and narrower completion probes. Results and limits:
`docs/adaptive-reference-results.md`.

The fourth slice profiles/tunes depth and allocations: worker-local simulator
scratch, retained-node arenas, resource-diverse deep rollouts, and exact quality
cash-out/progress probes under the original shared budgets. Fixed corpus:
24/24 complete, 20/24 full target; 24 additional holdouts complete, 14 full target.
Current results and limits: `docs/adaptive-search-tuning.md`.

The fifth slice adds runtime opt-in stable native capture and shadow recommendations.
See `docs/adaptive-shadow.md` for activation, correlation limits, and native acceptance.
The sixth slice preserves ended-session evidence and adds bounded host-observed
search lifecycle timing without changing scheduling or enabling execution.
The seventh slice replaces shadow's live-ready scheduling with one bounded
historical-only analysis lane. Existing executor requests no longer cancel captured
root searches; results are explicitly historical and cannot become live decisions.
Live coordinator/execution safeguards remain unchanged. The eighth slice captures
equipped-tool 150%/175% Good bonuses and field-level mismatch evidence (model 2,
export schema 4); see `docs/adaptive-tool-bonus.md`. Native acceptance is pending;
captured-state plans are not live Adaptive craft outcomes.
The ninth slice seeds historical searches with detached cached Raphael suffixes and
aligned prior Adaptive continuations (schema 5). No new Raphael solve is requested.
See `docs/adaptive-seeding.md` for replay/alignment guarantees and measured comparisons.
Subsequent schema-5 native trace confirms a cache hit, 16/16 full-quality historical
plans, and enhanced Good correlations without mismatch. Compact fixture and 21 new
regressions preserve this evidence; terminal/action-ID confirmation and broader
native cases remain pending. Existing solver, not Adaptive, executed the craft.
The next slice adds opt-in experimental native crafting callback capture and schema-6
acknowledgement/terminal diagnostics; see `docs/adaptive-native-confirmation.md`. It does not
add a selectable Adaptive solver or substitute native CP/status observations.
First schema-6 native capture matched 16 action IDs/outcomes and delivered terminal
scalars. Corrected terminal condition validation to preserve input condition; added
durable packet-only mismatch diagnostics. A supplied post-fix Normal-terminal run
now admits 17 acknowledgements/16 full-frame outcomes and observed quality11550→12000,
without mismatch or invented final CP/buffs. Compact regression preserves that scope;
post-fix Poor-terminal and broader native lifecycle acceptance remain pending.
Subsequent slice implements an explicitly armed runtime-only live trial host:
`docs/adaptive-live-host.md`. No additional native cases were required before that
implementation. First supplied controlled live run now completes at12000 quality with
16 acknowledgements,15 full-frame correlations,and matched terminal Groundwork at zero
durability. Scoped fixture and live schema3 diagnostics preserve that evidence without
reconstructing the evicted prefix. Broader native/live acceptance remains pending.
Second supplied live schema3 run also completes at12000 with18 acknowledgements/17 full
frames and retained Manipulation ticks; no controlled A/B claim. Live schema4 now adds
bounded per-root proposal/search/transaction/timing history without changing optimization
or execution. See `docs/adaptive-live-host.md` for bounds,timing semantics,and evidence limits.
The supplied schema4 trace retains all16 roots and completes at12000 quality with
16 acknowledgements/15 full-frame correlations. The next offline-only slice adds
proven redundant Final Appraisal filtering, comparator-safe duration bounds,
exact-state path-label handling, and complementary completion probes under the
same budgets. Captured roots1–2 now find full-target continuations offline;
roots12–15 use about55% fewer combined transitions. Fixed corpus reaches21/24
full targets (24/24 complete); former holdout scenarios16/24 (24/24 complete).
See `docs/adaptive-search-tuning.md` for deterministic results, limitations, and
why the quality-first comparator and execution safeguards remain unchanged.
The following slice adds seed-first runtime trial preparation: reuse an exact immutable
Raphael cache candidate or queue/promote its generation before synthesis; failure or
timeout falls back to unseeded search. A prepared macro is consumed once and independently
replayed against the actual stable starting root/active goal before it enters bounded
search. No mid-craft cache injection or persisted Adaptive selection was added. See
`docs/adaptive-live-host.md` for gating, lifecycle, and pending native acceptance.
The first supplied seeded cache-hit trial now completes at12000 with17 matched
acknowledgements/16 full-frame correlations and all17 roots retained. Follow-up
offline work adds stronger proven progress bounds and explicit search-stop
diagnostics. Smaller seeded allowances were measured but not enabled in live
defaults because broader cases lost useful improvements. See
`docs/adaptive-seeded-search-budget.md`; execution and saved settings stay unchanged.

**Adaptive (Experimental) is a saved/selectable solver mode. Selection permits
execution; each craft automatically prepares fresh native capture and seed.**
Startup conflicts/failures block synthesis without fallback. Standard, Raphael, Progress
Only and macro defaults remain unchanged. Historical shadow mode still cannot
execute. Trial errors stop without fallback. Macros and uncaptured collectability
targets are rejected rather than guessed.
Shared simulator corrections below do affect callers of the existing simulator.

Tracking plan: `/home/blake/.opencode/plan/adaptive-solver.md`.

## Simulation audit and corrections

Fixtures use explicit stats yielding base progress 102 and base quality 135,
then independently calculate action potency, rounding, costs, and buff effects.
Source inspection of the pinned Raphael v0.29.2 simulator originally cross-checked
Refined Touch and Trained Eye, but native verification later exposed upstream
errors in standalone Refined legality and Trained Eye durability. See
`docs/adaptive-action-audit.md` and `docs/adaptive-trained-eye.md`. The level11
Inner Quiet gate is separately tested. The third slice executed pinned Rust
to export22504 action trials across12 real-recipe/stat profiles. Current parity
claims exclude the documented native-backed errata; broader native acceptance
is incomplete. Projection limits are explicit in `adaptive-reference-results.md`.

Corrections in `Vulcan.Simulator.cs`:

- Reject None, pseudo, unknown, and terminal-state actions.
- Allow standalone Refined Touch; Basic Touch grants its extra combo IQ stack,
  not permission to execute. Basic→Refined reaches3 total IQ from zero.
- Trained Eye fills remaining quality instead of adding maximum quality, costs
  250 CP and zero durability, and does not consume Trained Perfection. Earlier
  audit/pinned Rust expectations incorrectly charged10 durability; the supplied
  recipe5282 native packet exposed this. See `docs/adaptive-trained-eye.md` for
  the explicit frozen-reference erratum and regression scope.
- Do not grant Inner Quiet below level 11.
- Quick Innovation does not advance step/condition or tick existing buffs/repair.
- Hasty Touch grants Expedience only at level96+; failure semantics remain unverified.
- Daring Touch consumes Trained Perfection as a durability-cost action (it is
  still excluded from Adaptive's reliable-action allowlist).
- Quality multipliers use exact integer percentages with a single final floor,
  avoiding one-point float-rounding errors exposed by the Rust reference.

Adaptive separately tracks valid Standard Touch combo eligibility rather than
assuming last action implies a chain. Legacy live-state capture after Standard
Touch requires explicitly confirmed chain history; ambiguous captures fail closed.

Tests also cover first-step/level/unlock/CP gates, combo discounts, double IQ,
Byregot consumption, Innovation/Great Strides, repairs, Manipulation timing,
Final Appraisal, Groundwork, and forced condition transitions. Successful
completion on last durability is classified before depletion failure, with
mandatory quality still required.

Unaudited specialist/mission/expert mechanics remain excluded. In-game state
prediction comparisons remain mandatory before automated use. In particular,
the current game loop's simulated-state fallback and synchronous recommendation
consumption are not suitable integration shortcuts.

## Core behavior

`AdaptiveCraftSnapshot` owns only immutable scalar data. It rejects expert,
Cosmic, mission, special-condition recipes, invalid arithmetic inputs, and
levels outside this prototype's 1–100 contract. `AdaptiveSearchState` uses value
equality over all supported future-relevant mechanics. Later step numbers are
normalized to first-step eligibility; combos and free-action buff changes are
not discarded. Unsupported active mechanics are rejected rather than silently
projected into a different model. Specialist availability is not used because
specialist actions are outside the action set.

`AdaptiveCraftGoal` separates mandatory quality from desired quality, resolves
HQ chance through the existing HQ curve, supports collectability tiers, and
offers an explicit strict-target flag. Goals and actual initial quality are
inputs, not reads from settings or the guaranteed ingredient-quality estimate.

`AdaptiveSimulation` uses an explicit reliable-action allowlist and calls the
shared production simulator. Current condition is exact. Unknown future
conditions are Normal, except forced transitions such as Excellent → Poor and
condition-preserving Final Appraisal. This is not probability-weighted search.
Duration is a simple animation-time tie-break model, not measured game latency.

`AdaptivePlanValidator` replays every action, rejects extra actions after
completion, and accepts only successful complete plans. Strict goals reject
below-desired completion; mandatory-quality shortfalls are always failures.
Returned action lists own their storage and cannot be modified by the caller.

`AdaptiveCompletionPlanner` constructs completion routes. Below mandatory
quality (or a strict desired target) it can search quality actions; otherwise
it uses progress/resource actions. Bounded failures are Unknown, never proofs.
Only an already terminal unsuccessful initial state yields ProvenImpossible.
Positive completion caching is not implemented yet.

`AdaptiveSearchEngine` first replays a supplied continuation and tries a bounded
baseline completion. It then expands individual actions into a beam, using
parent links, exact-state deduplication, and round-robin root-action diversity.
Top retained states receive bounded completion suffix probes. Complete plans
are replayed before becoming incumbents. Partial-state heuristics do not
authorize action execution.

All stages share a cancellation token, transition/node limits, and optional
monotonic wall-clock budget. Node accounting is conservatively cumulative across
baseline, main search, and probes; it is not a measurement of peak process RAM.
Horizon/frontier limits also make search approximate even if the global budget
is not exhausted. Exact deduplication and beam selection do not certify global
optimality. No speculative upper-bound or broad resource-dominance pruning is
implemented yet.

Results distinguish Found, Unknown, ProvenImpossible, and Cancelled. Cancellation
returns no executable plan. A budget-expired search may return an already
validated incumbent. No unchecked Standard/greedy fallback is executed.

## Tests and benchmark

Run the game-independent production-source tests from repository root:

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test tests/GatherBuddy.Adaptive.Tests/GatherBuddy.Adaptive.Tests.csproj --configuration Release --nologo --logger "console;verbosity=detailed"
```

Tests compile actual production sources without Dalamud or simulator stubs.
Coverage includes simulation fixtures, immutable ownership, goal conversion,
allowlists, terminal classification, strict/mandatory targets, completion with
repair, continuation validation, conditions, cancellation, budget limits,
determinism, exhaustive small-craft comparison, and a 40-state seeded sweep.
The exhaustive oracle has no beam/deduplication/pruning logic; it shares the
simulator, so it verifies search coverage rather than independent game accuracy.

The synthetic smoke benchmark performs repeated per-turn search for 12 cases:
levels 18/50/100, durability 40/80, and all-Normal/seeded ordinary conditions.
It replays returned plans and supplies the continuation to the next decision.
No game or executor is involved.

Initial Release observation (machine-specific, not a performance guarantee):

- 12/12 completed, 12/12 reached target, 0 blocked, 76 decisions.
- p50 6.85 ms, p95 15.71 ms, p99 31.51 ms.
- Approximately 186 MB allocated over the entire benchmark, not peak RAM.
- Maximum cumulative stored nodes/decision: 5,000 (configured bound).

These are synthetic smoke results, not real-recipe coverage, measured game
quality, probability estimates, or a comparison against Standard/Raphael.
Tests deliberately avoid asserting wall-clock timings. Deterministic tests
disable time cutoff but retain bounded transition/node/frontier/depth limits.

Verification: 334 Adaptive tests pass in Debug and Release, 566 acquisition tests
pass, and 184 actual-plugin compatibility tests pass (1084 distinct tests total).
Compatibility smoke tests exercise Standard, Progress Only/time estimation, and
the offline engine in the actual plugin assembly. Forced Debug/Release solution
builds and `git diff --check` pass. Only the two existing nullable warnings in
unchanged ElliCon/ElliLib submodules remain.

## Async decision lifecycle (execution not wired to crafting)

`GatherBuddy/Crafting/AdaptiveSolveCoordinator.cs` has no game-service references
or action-execution callbacks. Its default scheduler runs the pure search on the
thread pool. Methods serialize access; workers do not mutate recommendations.
The future host must call `Poll`, lease consumption, and native checks/execution
on the game thread. A completed task alone never publishes an action.

`Begin` creates a new GUID session, capturing immutable craft/goal/limits. Each
authoritative observation has a monotonically increasing host revision. Duplicate
frames at the same revision/state do not restart work. Reusing a revision for a
different state fails the session. Older/foreign-session observations are
ignored. Non-step actions must still get a new observation revision. Revisions
are not timestamps or an increment on every frame.

Each decision identity includes session, observation revision, generation, full
state, craft, goal, and settings; it does not rely on hash uniqueness. Cancelled
work must finish before replacement work starts, even if a scheduler ignores
cancellation. Only the latest pending request is retained. `Poll` rejects stale
results, observes worker errors, and independently replays any Found plan before
publishing Ready. Unknown, invalid, failed, or cancelled results expose no action.

`TryCreateLease` reserves one decision; `TryConsumeLease` checks the same session,
revision, structural live state, and reservation instance, then consumes once.
An execution-time state mismatch revokes readiness. Do not consume a lease before
an execution delay; re-read live state and check native action availability at
the actual execution point. **This is a pure one-use lease, not a native execution
guarantee.** Host code can only preserve this safety by obeying that contract.

Consumed leases enter AwaitingObservation. Reporting accepted execution is not
proof of success and does not advance the continuation. Only a newer, changed
authoritative state matching the predicted supported mechanics advances to the
tail. A Normal condition may randomly become Good/Excellent; forced transitions
and Final Appraisal's preserved condition must match. Other mismatches fail the
session, discard the continuation, and require investigation/new session.
Observations arriving before the execution response are held, not acted on.
Rejection retains the whole unconsumed plan as a candidate and requires a newer
read before replanning; it never silently skips the rejected action.

Missing/invalid live state blocks work rather than falling back to simulation.
Missing data during in-flight execution preserves uncertainty until the host
obtains a confirmed outcome. Pause revokes reservations and cancels work; resume
requires a read newer than observations collected before/during pause. Unresolved
in-flight execution is not erased by pause. Stop/finish/new sessions invalidate
leases and late callbacks. Dispose cancels without blocking and arranges to
observe eventual faults/dispose the cancellation source without publishing.
An uncancellable old worker can delay replacement, but cannot authorize an action.

Lifecycle tests control worker completion deterministically, including workers
ignoring cancellation. Coverage includes duplicate/reordered observations,
consecutive crafts, one-use/concurrent lease consumption, delayed execution,
accept/reject ordering, pause/resume and late responses, missing data, forced
condition mismatches, invalid plans, malformed schedulers, stop/disposal, and a
full simulated craft. An actual-plugin compatibility test exercises publication
and lease consumption; a default-scheduler test runs without native services.

Pure coordinator tests do not demonstrate native integration. The shadow host now
wires framework observation, bounded missing-read/pending-outcome waits, and lifecycle
cancellation without execution leases. Compiled-plugin boundary checks cover its
read-only call paths; native acceptance and atomic execution remain unverified.

## Original staged gates and current remaining acceptance

The list below is the original staged checklist, not a claim that all listed work
is still missing. Active-goal capture, exclusive live host, and experimental
selection/override/serialization wiring now exist. Broader native acceptance,
generated-seed lifecycle coverage, independent parity and profiling remain open;
see [current option limits](adaptive-option.md).

1. Broader independent simulation coverage and native observed-state parity.
2. Expand the initial real-recipe/Standard/Raphael comparison corpus. Improve
   quality-search depth and allocation traffic; add peak-memory profiling.
3. Goal resolution from active execution context, captured per craft.
4. Wire the tested async coordinator to the game-thread host with session/revision
   identity, native checks, cancellation, logout/stop, and bounded live-read retries.
5. Stable live-state capture; wait rather than plan from simulated fallback.
6. Settings/override/serialization/preview/estimator integration.
7. Native acceptance of implemented shadow mode, then controlled trial crafts,
   then opt-in release.

Automatic progression is enabled at explicit user request, not proof that all
native acceptance gaps closed. Keep experimental labeling and fail-closed safeguards;
do not make Adaptive default based solely on offline tests.
