# Adaptive shadow diagnostics

## Status and activation

Implemented; **schema-5 cache-hit/Good native evidence received; broader acceptance pending**.
Shadow diagnostics cannot execute actions. A separate selectable
[Adaptive (Experimental) option](adaptive-option.md) uses the
[live host](adaptive-live-host.md) to execute when selected, without separate arming,
one action at a time. While shadow owns the lane, Standard/Raphael/Progress Only/macros
retain control. Shadow permission remains runtime-only; no automatic opt-in is added.

1. Open **Vulcan Debug → Adaptive Shadow**.
2. Enable **Enable for next managed craft** before starting ordinary synthesis.
3. Start a managed non-expert craft with the existing selected solver.
4. Inspect historical analysis; use **Copy bounded trace** to export.

Enabling mid-craft does not attach. Quick synthesis is excluded. Disable/reload/logout
revokes opt-in; logout revokes it even while waiting for the next craft. Cancel,
finish, disposal, player/job changes, or a new craft revoke the current session.
The diagnostic goal is **full raw quality**, not the active list/solver policy.

Current export schema is **6**, adding opt-in experimental native callback acknowledgements
and terminal scalar evidence. Signature/layout validation is pending; shadow execution
remains disabled. Activation, risks, and strict checks: [`adaptive-native-confirmation.md`](adaptive-native-confirmation.md).
Schema 5 added bounded seed provenance, alignment availability,
exact-root replay assessments, replay cost, and final-versus-seed comparison. Schema 4
added equipped-tool 150%/175% Good capture and expected-versus-observed mismatch fields.
Historical scheduling remains as below. Seeding contract and offline results:
[`adaptive-seeding.md`](adaptive-seeding.md).
The supplied schema-5 run retains 16/16 full-quality plans with no mismatch or
cancellation; existing solver remains executor and final terminal state is unobserved.
See the seeding report for frozen evidence, timings, and remaining native cases.
First experimental schema-6 capture matched 16 acknowledgements/outcomes and received
a terminal packet. Terminal validation now preserves input condition rather than
modeling a nonexistent next turn; packet rejection differences persist under
`Summary.NativeEvidence.LastMismatch`. See the native-confirmation report for scope
and post-fix acceptance steps. Execution remains disabled.
Tool detection, audited metadata, restrictions, and acceptance:
[`adaptive-tool-bonus.md`](adaptive-tool-bonus.md).

## Observation contract

- A separate strict reader captures player stats, recipe metadata, addon scalars,
  CP, and supported status parameters. No default stats or predicted-state fallback.
- Fresh step one with Normal condition and no existing crafting buffs is required.
  Ingredient quality/current CP are captured as observed, not estimated.
- Analysed roots require two identical native samples on different framework
  frames, outside action execution. New/torn/missing reads cannot supply new roots.
- Attempts by the existing executor are recorded separately from its reported
  result. That executor currently reports success after calling `UseAction` without
  checking its return. **Neither a request nor reported success confirms execution.**
- Every supported observed field, exact step number, and forced condition must
  agree with the requested action's model before history advances. Normal may
  transition to Good/Excellent. Combo and remaining Trained Perfection availability
  are explicitly tracked history, not native scalar fields.
- Multiple outstanding requests, unsupported actions, untracked transitions, or
  stable model mismatch block diagnostics. No repair by copying predicted fields.
- Missing reads block after three seconds; unresolved attempted outcomes block
  after eight seconds. Pause revokes historical work, excludes paused time from the
  pending deadline, and requires fresh repeated samples on resume.

There is no native action-ID confirmation hook. Matching effects cannot distinguish
all possible actions. All exports retain `NativeActionIdentityConfirmed = false`;
request-correlated history is diagnostic evidence only, not execution authority.

Search uses a separate bounded historical-only runner, not live coordinator Ready
publication. Workers cannot publish directly; revoked results are discarded.
Shadow never creates/consumes execution leases. UI opt-in
dispatches to the framework thread. UI/export read immutable published views and
detached arrays, not native services or mutable event lists. At most 64 events and
16 historical results are retained. Exports omit account/character IDs but include
generated craft-session IDs, recipe/stats, and craft data.

## Schema 3: historical analysis, never a current recommendation

The runner searches accepted immutable captured states even after the existing
solver requests its next action. Every result is labelled **Historical shadow
analysis — not current; never executable**, including results that arrive before
the next request. There is no current action, Ready snapshot, execution lease, or
historical-to-live promotion path in the shadow UI/export.

One worker plus one latest pending observation is allowed. New pending roots
replace older pending roots with explicit skip counts/events; they do not cancel
the current historical search. The facade reuses this lane across crafts, so a
cancellation-ignoring worker must drain before any replacement can start. No action
delays, native reads from workers, or extra concurrent live-ready shadow lane.

Pause, mismatch/ambiguity, unavailable-read deadline, disable, craft end, logout,
context changes, and disposal revoke historical work. Late results after revocation
are not retained. Resume requires fresh repeated native samples. Ending never waits
for a worker, and unresolved work at teardown is not called completed analysis.

`Data.HistoricalResults` contains up to 16 immutable result records:

- `Source`: original craft session/revision/generation, immutable craft/goal/limits
  and model state, captured native frame, and observation time.
- `ExistingSolverAttempt` / name and attempt time inside Source, when the existing
  executor requested an action for that source revision. This is request evidence
  only, not native action-ID confirmation or evidence that Adaptive chose it.
- Host dispatch/result-observation times and `AgeAtRetention`. These are not the
  actual worker CPU start/completion times or the result's age when later exported.
- Search status/name, reason, budget diagnostics, and `ReplayValidation`.
- A proposed first action and `ValidatedPlan` only when a Found result independently
  replays to completion from the exact captured root within the length bound.
  Final state/quality/target flags are recomputed, not trusted worker metadata.
  Unknown, cancelled, faulted, empty, or invalid results have no proposal.
- `ValidatedFinalQuality` and `DesiredQualityReached` make captured-state simulated
  quality inspectable. They are not actual live craft outcomes, global optimality,
  expected HQ odds, or guarantees of practical live decision latency.

Full plan length remains bounded by search limits (default 64, maximum 256).
Retained historical results and events are separately bounded; exports detach
both histories and their counters on the framework thread.

### Durable finished summary

`CorrelatedActions` survives session teardown. `Data.Summary` is outside the event
ring, so its counts and last stable frame survive event eviction:

- `SessionEnded` / `SessionEndReason`: diagnostics stopped (including disable,
  logout, missing native crafting flag, or disposal).
- `ManagedCraftEnded` / `ManagedEndReason`: the managed Finish hook reported an
  end. This is not evidence of a native terminal state. A late Finish report can
  annotate a detached summary without changing its observed outcome.
- `TerminalObservation`: `Unobserved`, `ObservedSuccess`, or `ObservedFailure`.
  Only repeated, matched native samples can establish an observed terminal result.
  One terminal sample followed by addon loss stays `Unobserved`.
- `BlockedReason`: retained even when the final top-level status says managed
  craft finished. Finishing must not erase an earlier diagnostic mismatch.
- `LastMismatch`: retained expected state, observed projection, and per-field
  differences (schema 4); tracked history fields are not native action confirmation.
- `TotalEvents` / `DroppedEvents`: lifetime count and exact event-ring eviction
  count. The retained event sequence need not start at the beginning of the craft.
- `HistoricalAnalysis`: submitted/skipped observations, dispatched searches,
  cancellation requests, observed results, revoked results discarded, retained
  historical result count, validated-plan and desired-quality-plan counts. Counts
  are lifetime totals for this craft, not only the retained result ring.
- `HistoricalAnalysis.DroppedResults` / `DroppedLifecycleEvents`: result-ring and
  runner lifecycle-queue overflow counts. Last lifecycle event and last observed
  result diagnostics survive event eviction. Revoked prior-session worker drains
  are generic discard evidence, never prior craft inputs/results in the new history.

Every event includes a sequence and monotonic session-relative host timestamp.
Requested/proposed actions include names alongside numeric identifiers. Numeric
requested actions are solver enum values, not job-mapped native action IDs.

Search lifecycle events distinguish `SearchQueued`, `SearchDispatched`,
`SearchDispatchFailed`, `HistoricalObservationSkipped`, `SearchCancellationRequested`,
`SearchResultObserved`, `RevokedWorkerDrained`, `HistoricalAttemptLinked`, and
`HistoricalResultRetained`. Retention includes replay outcome/reason; no schema-3
`Recommendation` or live Ready event exists.

Timing is **host-observed**, not profiler timing: dispatch means scheduler invocation,
not actual worker CPU start; result observation means host polling, not task completion.
Workers do not write trace data or publish recommendations. Search-reported elapsed
time is included separately when a result exists. Runner lifecycle queue is bounded
to 64 entries; summary exposes overflow independently from the session event ring.

Only shadow scheduling changed. Live `AdaptiveSolveCoordinator` freshness,
cancellation, replay, and one-use leases are unchanged and remain disconnected from
game execution. Do not weaken those guarantees to use historical results as actions.

## Verification and limits

233 pure Adaptive tests pass in Debug/Release, including correlation/evidence cases for sample
stability, request/result ordering, mismatch, history ambiguity, combos, non-step
actions, terminal projection, deadlines, pause, stale work, finished summaries,
historical requests without cancellation, latest-only pending work, exact-root replay,
no-plan/fault/malformed schedulers, shared cross-session draining, bounded immutable
exports, and default historical scheduler operation without native services.
169 actual-plugin compatibility tests include compiled-boundary checks for
strict-reader separation, UI dispatch/detachment, lifecycle hooks, no persisted
shadow property, no live-ready shadow contracts, and no shadow execution/lease/
registration calls, plus actual-plugin managed export and historical replay regressions.
Tool coverage includes strict English effect detection, 150%/175% Good arithmetic,
supplied-sequence correlation, context revocation, scratch/replay, and identity.
566 acquisition tests pass (968 distinct tests).
Forced Debug/Release builds pass with only two existing submodule
nullable warnings; `git diff --check` passes.

User-supplied schema-1 traces show matching retained CP/durability/buff/IQ/quality
effects, Excellent→Poor→Normal, and quality capping on a longer level-100 craft.
The final addon disappeared before two terminal samples; inventory separately
confirmed output. Those traces exposed the finished-count reset and absence of
recommendation events. A schema-2 trace then showed 16 dispatched/cancelled/rejected
searches, zero recommendations, no blocked reason, and no budget exhaustion in the
retained results. Revision 23 was invalidated 95.2 microseconds after dispatch by the
next executor request, confirming cancellation starvation in that run. Schema 3
then retained nine replay-valid full-quality plans from one craft starting at 6000
quality; no normal-request cancellations/skips. Search-reported times were about
44–89 ms, retained at about 67–102 ms. This is partial native diagnostic evidence,
not nine independent live Adaptive successes or a new general benchmark.

That trace blocked at the next Good Preparatory Touch: observed quality 9337 versus
the ordinary model's 9045. User confirmed a 1.75× Cosmic tool. Schema 4 captures
the tool effect explicitly rather than altering predictions to fit observations.
**Schema-4 native acceptance has not run.** No native action-ID confirmation exists,
and broader reader/recipe/history acceptance remains pending.

Managed tests are not live-reader parity tests. No new performance benchmark
was measured for the shadow facade; earlier offline tuning numbers do not establish
native frame cost, UI cost, latency, peak memory, or live search quality.

## Required native acceptance

Do not enable Adaptive execution based on these tests alone.

1. Verify Synthesis AtkValue indices **15/5/9/7/12**, integer types, condition enum,
   CP, attributes **70/71/11**, recipe/job identity, and status IDs/parameters against
   the installed game build. A rejected layout is a diagnostic failure, not fallback.
2. Compare manual observations and traces across starting ingredient quality,
   partial CP, level/unlock boundaries, and supported recipe types. Confirm that
   repeated reads cannot promote mixed pre/post-action fields.
3. Exercise Basic→Standard/Refined chains, standalone Standard, buffs/ticks,
   repairs, Trained Perfection use/consumption, Final Appraisal's unchanged step,
   Normal→Good/Excellent and Excellent→Poor, quality cap, and terminal durability.
4. Verify terminal addon lifetime. The addon may disappear before two stable
   terminal samples; ending with an unavailable final outcome must not be called
   a confirmed completion. Verify rejection/mismatch traces without guessing history.
5. Exercise pause/resume, manual/untracked input, failed native requests, cancel,
   logout while active/waiting, reload, job changes, and consecutive crafts. Existing
   automation must remain unchanged with diagnostics on or off.
6. Verify default-off behavior, runtime reset, bounded exports, clipboard data,
   framework/UI ownership, and bounded search/frame impact under real game load.
7. Repeat a longer craft with schema 4. Verify equipped item ID/detection source and
   150% versus 175% Good percentage, Good correlations, expected/observed mismatch
   fields, unknown-effect rejection, and tool-ID-change/unavailable revocation.
   Check source identities/states, historical
   plan validation/final quality, requested-action links, age at retention, skip/drop
   counts, and absence of normal-request cancellation. Verify no Ready/current
   recommendation appears, no existing action is delayed, and revoked-session
   results do not leak across crafts. Completion/target quality for every captured
   root is not guaranteed; skipped observations are not analysed turns.

Future execution still needs active-context goals, native action availability at
the execution point, one-use lease wiring, controlled trials, and explicit opt-in.
