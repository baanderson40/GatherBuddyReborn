# Adaptive (Experimental) solver option

Adaptive can be selected globally or through recipe/list overrides. **Selecting
Adaptive authorizes execution, including consecutive queue crafts. No separate
runtime arming or per-craft permission is required.** Native integration remains
experimental; the default solver is still Pure Raphael.

## Use

1. Reload the updated plugin. In **Vulcan → Settings → General → Solver Mode**,
   choose **Adaptive (Experimental)**. Alternatively choose it in recipe settings
   or list default precraft/final solver selections.
2. Start/resume crafting normally. Each selected Adaptive ordinary craft
   automatically waits for drained worker lanes, installs fresh experimental
   native capture before opening the recipe, and prepares a Raphael seed before
   synthesis. Early native start callbacks must not be missed or reconstructed.
3. Queue entries continue automatically after successful crafts. Every craft
   gets a new startup ticket, native capture and decision/confirmation session;
   no prior craft's seed cursor or execution lease is reused.
4. Open **Settings → Adaptive Solver** for status, collectable target, trace export,
   and **Stop Adaptive (no fallback)**. Debug retains its diagnostic view. Stop
   pauses the queue and stops preparation/execution without fallback. Manually
   finish/cancel an already-started stopped craft before resuming.

Debug no longer offers a takeover/arming checkbox. Select Adaptive through the
normal solver settings instead. Quick synthesis remains its separate existing
path. NQ-only first-craft unlock policy still forces Progress Only before normal
solver selection. Existing Standard/Raphael/macros do not automatically enable
the Adaptive native hook.

## Goals and seeds

- Native goal capture records `AdaptiveLivePolicyMode.Adaptive`. HQ-capable recipes
  target full quality; mandatory-quality recipes retain their required target;
  non-HQ/non-collectable recipes without mandatory quality use progress-only goals.
  Collectables use authoritative local reward data: tier1 mandatory, selected tier
  desired (default3). Adaptive Solver settings exposes the collectable target;
  higher selected tiers are not guaranteed. No new strict-target setting.
- Explicit/associated macros without declared goals reject Adaptive startup, rather
  than being overridden, converted into goals, or advanced by Adaptive. Existing
  macro execution remains available through legacy solver modes.
- Unsupported live contexts, including missing independently captured collectible
  thresholds, reject preparation before synthesis. No guessed tool/status/quality data.
- Seed-first preparation reuses the shared Raphael cache/queue before synthesis.
  Failed/unavailable/timed-out seeds allow **unseeded Adaptive**, never legacy
  takeover. Actual-root independent replay remains mandatory; no late seed injection.
- Full live budgets remain40000 transitions/20000 nodes/100ms. Experimental reduced
  seeded allowances remain null. Proven completion bounds remain enabled.

See [collectable support](adaptive-collectables.md) for sources, unit conversion,
rejected-start diagnostics, and remaining attended acceptance requirements.

## Routing and compatibility

`VulcanSolverMode.Adaptive=3` and `SolverOverrideMode.AdaptiveSolver=4` append to
the saved enums. Existing numeric values, absent-field defaults, and the default
Pure Raphael selection are unchanged. No migration or persisted arming field.
Existing list/recipe clone, inheritance, and serialization carry the appended value.

The confirmed-outcome host is the sole Adaptive executor; it is deliberately not
a legacy `Solver.Solve` adapter, which would bypass acknowledgement/lease ownership.
Adaptive registration removes the default Progress Only fallback definition. Managed
start and queue/direct entry points check ownership/macro conflicts before
synthesis. Conflicts pause before output tracking and preserve remaining intent.
Automatic startup waits boundedly for live/shadow workers to drain; cancellation
is not treated as completion. Capture installation is attempted once per startup;
failure/exception/timeout blocks synthesis, not a repeated hook install or legacy
takeover. The recipe log's between-craft Crafting/PreparingToCraft flags are allowed,
but active/unmanaged synthesis is not a valid attachment point.

A pre-synthesis reservation is latched: stopping or changing configuration cannot
make that craft fall through to the legacy executor after native startup. Startup
tickets are one-use and invalidated by stop/cancel. Changing solver, macro or quality
policy during preparation cancels it. Native startup still requires matching fresh
StartPrepare/StartInfo and independently repeated actual fields.

Action timing, one-use leases, stale-result rejection, exclusive worker ownership,
native queue checks, return-neutral transactions, observation correlation, and
packet-only terminal handling are unchanged. Historical shadow still cannot execute.
Adaptive time estimates use the existing generic estimate, not a promised macro
route or a live solve. Preview/estimation does not invoke the native host.

## Acceptance limits

Trained Eye's zero-durability correction and supplied Dark Rye Flour failure are
documented in [adaptive-trained-eye.md](adaptive-trained-eye.md). The shared
simulator and its pinned Rust reference both had the wrong10-cost assumption;
the raw reference remains preserved with an explicit comparison erratum. This
fix does not relax native mismatch checks or change AR handoff behavior.

The supplied additional cache-hit trace (`514f03e9-2f68-424c-a00f-8e5cf8aea2d0`)
reports17 acknowledgements/16 full-frame correlations, all17 retained roots, all
seed comparisons Tied, zero mismatches, and terminal6600/12000/5 with managed end.
Last actual frame is6209progress/11550quality/CP86. No final CP/status frame is
invented. Generations18–34 are distinct from the earlier run; this does not by
itself prove every consecutive-craft isolation case. The export lacks the latest
bound diagnostics, so it is not evidence that the newly built optimizer or selectable
option was loaded. It remains another cache-hit trial, not generated-seed acceptance.

Further supplied evidence now includes:

- CRP recipe1526 under selected Adaptive: one cache-seeded Delicate Synthesis,
  terminal105/1760/30, no mismatches or dropped events (`fb97444f-9479-4309-ad70-a1d272b8ce85`).
- CUL recipe5618 under selected Adaptive:17 roots/actions,17 acknowledgements,
  16 full-frame correlations, terminal6600/12000/5, all seeds Tied and zero
  mismatches/linkage loss (`292603d1-c1f3-4fd4-b21e-63e3e86740e6`). Current bound
  diagnostics are present and show nonzero proven-bound pruning. Confirmation ring
  evicted109 events; all roots remain. Last actual frame stays6209/11550/CP86.
- Earlier CRP startup reports `GenerationRequested=true` followed by validated
  cache retrieval and completion. This supports the queue-request/cache-handoff
  path, not independent proof of a fresh CLI process.

These traces predate removal of arming; automatic consecutive queue progression
has not yet been accepted in-game. Generation cancellation/context drift, fresh
post-fix Poor terminal, non-step actions, rejection/failure/abort, occupied native
queue, false-return outcomes, consecutive-craft isolation, and broader jobs/recipes
still need attended in-game evidence. Runtime permission was removed at explicit
user request with these limits acknowledged, not because all native acceptance
gaps closed. Keep experimental labeling and fail-closed safeguards.

Implementation and evidence details: [live host](adaptive-live-host.md),
[bounds/allowance results](adaptive-seeded-search-budget.md).

## Verification

Managed tests cover append-only saved enums/defaults, JSON round trips, recipe/list
inheritance and macro precedence, every mode/quick/ownership/macro gate
combination, active goals, and compiled pre-synthesis/queue/UI boundaries. Existing
solver, lease, native-confirmation and lifecycle tests remain in place. The earlier
offline-only enum assertion now expects the appended option while explicitly
checking all existing numeric values.

### Prior selectable-option verification (before automatic startup)

- Adaptive:557 passed in Debug and557 in Release; final logs have no warnings.
- Compatibility:309 passed; acquisition:642 passed (1508 distinct tests across
  the current working tree, including concurrent unrelated travel/retainer changes).
- Forced nonincremental solution builds ran sequentially, Debug then Release;
  both succeeded with zero errors and only the two existing nullable warnings in
  unchanged ElliCon/ElliLib submodules. No new plugin warnings.
- Tracked/untracked whitespace checks passed. Legacy `CraftingActionExecutor.cs`
  remains unchanged. Unrelated working-tree changes were not reverted.

Final logs use `/tmp/opencode/adaptive-option-` with suffixes `debug-tests.log`,
`release-tests.log`, `compat-tests.log`, `acquisition-tests.log`, `debug-build.log`,
and `release-build.log`. A Release run exposed a tiny-deadline test race: the
stopwatch could still read its initial timestamp. The test now waits boundedly
for expiration, preserving its timeout/termination assertion; production timeout
logic was not changed. No native execution, commit, or push was performed.

### Automatic startup follow-up

`AdaptiveStartupGateTests` exercise consecutive one-use tickets, worker drain,
bounded timeout, capture failure/exception without retry, stop before/after capture,
selection/context drift and between-craft recipe-log flags. Compiled boundaries
check automatic preparation before synthesis, lane exclusion, one-time handoff and
status/Stop UI without arming. Existing confirmation/lease/search suites remain.
Logs use `/tmp/opencode/adaptive-auto-` with the same test/build suffixes above.

Final automatic-startup verification:574 Adaptive tests passed in each of Debug
and Release,277 compatibility and642 acquisition tests passed (1493 distinct).
Final test logs have no warnings. Forced sequential Debug/Release solution builds
passed with zero errors and only the two existing ElliCon/ElliLib nullable warnings.
Tracked whitespace and all100 untracked text-file checks passed; legacy
`CraftingActionExecutor.cs` remains unchanged.

The obsolete `armed` boolean was removed from the selection test matrix (32
meaningful mode/quick/busy/macro combinations instead of64 permission combinations).
No search corpus floor or native execution safeguard was relaxed. Native context
read failures reject startup; only seed lookup/generation failures allow unseeded
Adaptive. No live in-game trial, commit or push was performed for automatic startup.
