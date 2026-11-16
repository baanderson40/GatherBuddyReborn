# Adaptive runtime-only live trial host

Status: experimental host implemented. **Selecting Adaptive permits automated
game actions, including consecutive queue crafts; no separate arming.** First supplied controlled live trial completed
at full quality with matched acknowledgements/observations. This narrow acceptance is
not blanket native validation. Standard/Raphael/macros and saved defaults remain unchanged.

**Current selection integration:** [Adaptive (Experimental)](adaptive-option.md)
is available globally and in recipe/list overrides. Selection authorizes execution.
General settings expose stop, status, seed status and trace export; no permission
checkbox remains in General or Debug. Each craft automatically prepares fresh
capture/seed after worker drain. Later dated sections below
describe historical patches and their then-current verification counts.

Selectable-option follow-up verification:557 Adaptive tests passed per Debug/Release,
309 compatibility and642 acquisition tests passed in the current working tree.
Forced sequential Debug/Release builds passed with only the two existing submodule
nullable warnings. No new native acceptance or commit/push accompanies this option.

## Activation and current limits

Confirmed action-rule follow-up: shared simulation accepts standalone Refined
Touch with one IQ stack, gives its extra stack only after Basic Touch, treats
Quick Innovation as non-step, and gates Hasty Touch's Expedience at level96.
Specialist/probabilistic actions remain excluded from Adaptive. Standalone Refined
is accepted in observations/seeds but omitted from generated search where Basic
Touch dominates it. See `adaptive-action-audit.md`; mismatch checks are unchanged.

Trained Eye follow-up: supplied recipe5282 native evidence exposed an incorrect
10-durability simulator charge. Both shared cost classifications now use zero;
strict packet checks are unchanged. The captured acknowledgement matches after
correction, but no post-fix in-game completion is claimed. See `adaptive-trained-eye.md`
for the frozen failure and explicit pinned-Rust erratum. No AR handoff logic changed.

1. Select Adaptive in Vulcan Settings → General or use a recipe/list override.
2. Start/resume crafting. Each Adaptive ordinary craft disables shadow diagnostics,
   waits boundedly for drained lanes, installs fresh capture once, and prepares its seed.
3. Subsequent queue crafts repeat preparation automatically. Failure blocks synthesis;
   existing solver/macro never takes over an uncertain or rejected Adaptive craft.
4. Copy the bounded live trace after finish or stop. **Stop live trial (no fallback)**
   revokes execution; manually cancel or finish a stopped craft. It does not retry actions.

The native hook is experimental and patch-sensitive. Incorrect signatures/layouts can
destabilize the client; managed checks cannot make unsafe native memory access safe.
Broader unattended queue acceptance remains incomplete. Automatic progression was
enabled at explicit user request with those limits acknowledged. No coding-agent
native trial was run.

Automatic-startup follow-up:574 Adaptive tests passed per Debug/Release,277
compatibility and642 acquisition tests passed. Forced sequential Debug/Release
builds passed with only the two existing submodule nullable warnings. Startup
tests cover fresh one-use tickets per craft, bounded worker drain, capture failure
without retry, stop/context drift and between-craft recipe-log flags. These are
managed/compiled checks, not new in-game acceptance. See `adaptive-option.md`.

Implemented policy mapping uses the effective per-craft solver override: Progress Only
retains recipe mandatory quality; Standard uses its actual HQ curve percentage or
mandatory recipe quality; Raphael uses its full-quality objective (mandatory-quality
recipes retain their required target). Explicit or associated recipe macros reject
startup because they have no declared quality objective. Live collectability goals
now use local reward-sheet thresholds; missing/ambiguous data still rejects before
synthesis. Tier1 is mandatory, selected Adaptive tier desired (default3). No guessed
collectible policy or new strict-target setting. See `adaptive-collectables.md`.

Live startup now attempts a Raphael seed before managed ordinary synthesis: exact
immutable cache lookup first, otherwise enqueue/promote through the existing shared
Raphael coordinator and wait before clicking synthesis. Failure/unavailability or the
configured preparation deadline permits unseeded Adaptive search. This is not a
full-quality requirement or guarantee. Historical shadow lookup remains cache-only.

Implementation: `AdaptiveLiveTrial.cs` (pure transaction), `AdaptiveLiveDiagnostics.cs`
(framework/native facade), `AdaptiveLiveGoalPolicy.cs`, and exclusive branches in
`CraftingGameInterop.cs`. Shared `AdaptiveShadowSession` confirmation-only mode creates
no historical runner or searches and uses the active goal. Shadow defaults are unchanged.

## Ownership and startup

- Separate framework-thread trial host, not an execution branch in historical
  shadow analysis. Selected Adaptive mode drives each managed ordinary craft;
  every startup has a fresh one-use capture ticket. No mid-craft adoption.
- CraftingGameInterop chooses exactly one executor owner before the first action.
  Trial ownership excludes the existing macro/solver executor and historical searches.
  Cancelling a worker is not draining it: wait for completion before switching lanes.
  Refuse startup if ownership or lane quiescence cannot be proved.
- Capture the active list/override goal using the existing policy's actual mandatory,
  desired, and strict-target semantics. Diagnostic full raw quality is not that policy.
  Missing or unsupported policy blocks startup; policy changes revoke the session.
- Require matching fresh StartPrepare/StartInfo, a never-attached native stream, strict
  step-one capture, supported tool evidence, and two identical independent host-frame
  samples. Bind recipe/job/tool/stats/goal, session identity, and native stream watermark.

## One action transaction

1. Feed a confirmed immutable root to `AdaptiveSolveCoordinator.Begin/Observe`; only
   framework `Poll` may publish a replay-validated decision. Keep current bounded search
   limits and stale-result rejection. Cache seeds remain optional, never a state source.
2. Wait through existing timing/readiness rules before reserving an execution lease.
   Immediately before consuming it, drain native evidence, recapture all supported
   state/context, check ownership/pause/session/revision, resolve the exact job action ID,
   and query native action availability. State drift revokes the decision, not patches it.
3. Register the expected request and exact native ID before the native call, then consume
   one lease and invoke one action on the same framework turn, with no await/delay between
   final checks and call. If request registration or lease consumption fails, send nothing.
   A consumed lease is never reusable, including exceptions or ambiguous native returns.
4. Record native return as raw diagnostic data. **Both true and false leave the same
   outstanding transaction waiting for outcome; neither confirms acceptance/execution.**
   Existing `CraftingActionExecutor.TryExecuteActionAsync` still returns true after
   `UseAction` without checking its return. That legacy behavior is unchanged. Live
   confirmation has separate invocation/return APIs: it never fabricates a successful
   legacy `ReportRequest` to make false eligible for matching.
5. Hold the coordinator's next observation until exact operation/ID/context/sequence and
   packet results match, then independently confirm CP/status/full frame with two equal
   samples on different framework frames. Do not feed unchecked observations into the
   coordinator: its pure model comparison does not itself require native acknowledgement.
   Non-step actions still advance the request ordinal, not guessed step cursors.
6. Match confirmed request ticket and action ordinal to the outstanding lease. Only
   after full-frame confirmation report confirmed execution to the coordinator, publish
   a newer authoritative revision, and replan. Never execute a stored multi-action suffix
   without repeating this transaction. Coordinator reporting is not called from dispatch.

`TryDispatch` returns explicit `NotStarted`, `AwaitingOutcome`, or `Stopped`, not the
native bool. The immutable last transaction records ticket/action/exact native ID,
ordinal/start time, invocation marker, nullable raw return, acknowledgement/full-frame
flags, outcome, and failure reason. One consumed lease remains outstanding after either
return value. `UseAction returned False; awaiting native outcome. No retry permitted.`
is a waiting state, not a failed craft or a reason to send another action.

An occupied `ActionManager.ActionQueued` before dispatch blocks sending. The host reads
only that queue flag; it never clears queue fields or assumes queued ID proves ownership.
Last pre-dispatch queue flag is diagnostic only, not action confirmation. Timeout remains
eight seconds from original request; it is not extended by return value or queue changes.
Acknowledgement arriving past deadline is rejected even if no intervening framework tick
ran. Missing evidence ends as **Unconfirmed**, not a proven native rejection.

Terminal packets are a separate scalar-only outcome, not `AdaptiveSearchState`.
Admitted terminal evidence plus managed end stops the coordinator; do not invent final
CP/status/combo or publish another search root. Preserve experimental labels until an
explicitly reviewed native authority contract exists.

## Fail closed and lifecycle

- Pre-dispatch refusal, exception, acknowledgement timeout/loss, unexpected action, mismatch, abort,
  addon loss, ownership/context change, or unsupported effect stops trial execution.
  No automatic retry or fallback to another executor in that craft. Coordinator's pure
  rejection/replan support is not permission for the host to retry an ambiguous request.
- Pause revokes ready work and leases. Outstanding actions remain unresolved; resume
  cannot clear uncertainty. Reconcile acknowledgement and fresh independent state or
  stop. Always require a fresh post-resume observation before another action.
- Stop/logout/disposal/new craft revoke ownership, callbacks, pending roots, and leases;
  stale worker completion cannot publish into the next session. Bound event buffers and
  sticky overflow behavior remain intact.

## Automated checks and acceptance limits

Fake-host and compiled-plugin boundary tests cover exclusive ownership, worker drain,
active goal mapping, state change between readiness and consumption, unavailable action,
duplicate lease, ambiguous return/exception, acknowledgement-only
and frame-only rejection, non-step action, timeout, pause/in-flight resume, terminal without
final frame, consecutive sessions, and no-cache completion. Compiled-plugin boundary
checks inspect startup exclusion, drain guards, native availability checks, and
registration→consumption→dispatch ordering; they are not native execution tests.
Callback tap queues even synchronous native delivery; facade drains only after actual
request disposition has been reported. No direct callback invokes the transaction host.

Native coverage still lacks a fresh post-fix Poor-terminal run and broader non-step,
rejection/failure/abort, pause/resume, missing-cache, and consecutive-craft evidence.
These cases are not prerequisites to implementing this explicitly armed experimental
host, per user instruction. One supplied controlled trial now supports native one-action
observe/replan for that craft; broader live acceptance remains open. Historical search
success while another solver executes cannot establish live acceptance. No selectable/
persisted release mode is added.

Live schema2 added the detached last transaction, outstanding marker, and last pre-dispatch
queue flag. Enum outcomes include readable names. NativeLayoutValidated remains false;
shadow schema6 and its false-authority fields are unchanged. Managed end remains separate
from confirmation-session end: a stopped
confirmation session remains revoked even if managed teardown occurs later. Later
protocol failure after an admitted terminal packet cannot be promoted back to coordinator
completion at teardown. Raw terminal evidence and durable mismatch both remain available.

Initial live-host verification: 365 Adaptive tests pass in Debug/Release, 188 actual-plugin compatibility
and 566 acquisition tests pass (1119 distinct). Thirty new pure cases and four compiled
plugin boundary cases cover this slice. Sequential forced Debug/Release solution builds
pass with only the two existing unchanged ElliCon/ElliLib nullable warnings. Tracked and
untracked text whitespace checks pass. Logs:
`/tmp/opencode/adaptive-live-{debug-tests,release-tests,compat-tests,acquisition-tests,debug-build,release-build}.log`.
No native game trial, independent enhanced-Good Rust parity, or live performance claim.
No commit/push.

## Native return evidence and scope

Local `ffxiv-knowledge/platform/FFXIVClientStructs` revision
`b69073ae2d789527fc1b9a78aa7aa590e0f299dc`, `FFXIV/Client/Game/ActionManager.cs`,
documents action queueing but leaves `UseAction` return semantics empty. Installed
`FFXIVClientStructs.xml` has the same empty returns documentation. Compiled-plugin test
checks the installed `ActionQueued` bool at offset0x68 against that source; this audits
the binding, not the current client's native behavior.

Local `ffxiv-knowledge/runtime-providers/ffxiv_bossmod` revision
`3bbbdf5e0ba0b1f201c5f76465df63ba63dc96c4`, `notes:14–17`, records true for initiating/
queueing combat actions and false for further calls while one is queued. These are
combat observations, **not proof that crafting returns false by default or that any
specific return proves execution/nonexecution**. No native crafting return trace was
collected by this patch. Neutral handling avoids relying on undocumented semantics.

Return-neutral correction verification: 380 Adaptive tests pass in Debug/Release,
189 compatibility and 566 acquisition tests pass (1135 distinct). Net15 pure and1
compatibility increase. Sequential forced Debug/Release solution builds pass; only two
existing unchanged submodule nullable warnings remain. Tracked diff and72 untracked
text files pass whitespace checks. Legacy executor has no diff. Logs:
`/tmp/opencode/adaptive-disposition-{debug-tests,release-tests,compat-tests,acquisition-tests,debug-build,release-build}.log`.
No native execution test, return-value contract claim, or commit/push.

## First supplied live acceptance and scoped regression

User-supplied live schema2: coordinator session `a87d759b-8e25-49de-8ee6-f38c64fc202b`,
confirmation session `b42f2ab2-fe8b-497b-a18a-054ffaeb37a5`, CUL recipe5618/tool51785/175%.
These separate session IDs are intentional. Effective Raphael policy supplied desired
raw quality12000; Raphael did not execute or provide a cached route. Source reports:

- 16 matched acknowledgements,15 full-frame correlations,terminal transaction16 confirmed;
  no outstanding transaction,block,frame/packet mismatch,prediction mismatch,or rejected result.
- Zero historical submissions/searches/results. Final live search replays a one-action
  continuation at target; this is not a complete per-root search/performance history.
- Final CUL Groundwork100410,stream1/sequence19/op9,input step16/Normal,flags30(0x1e):
  progress6600,quality12000,durability0. Completion at zero durability is valid because
  progress reaches target on that action. Modeled5118+1706=6824 is normalized to6600.
- Last actual repeated frame remains step16/progress5118/quality12000/durability20/CP110/
  Veneration1. Final transaction correctly has AcknowledgementMatched=true and
  FullFrameCorrelated=false. No final CP,buffs,combo,or full frame is inferred.
- Finish sequence20 follows admitted terminal; ManagedCraftEnded=true. Source event ring
  retains64/163 and evicts99 normally. Six retained invocation/return pairs are true;
  earlier returns,including Byregot's,are evicted. This disproves always-false crafting
  returns but does not establish a universal return contract or native false-return handling.

Fixture `Fixtures/live-schema2-groundwork-success.json` is explicitly a compact source
projection, not a complete export. It preserves seven input frames (steps10–16), seven
raw packet projections (sequences13–19), six known returns plus an unknown null, final
transaction/Finish, and source summary totals. It does not reconstruct the evicted fresh
start or first nine input roots. Independent suffix replay checks six full-frame
transitions plus terminal scalars only; it does not reproduce all16 source acknowledgements
or15 correlations. Test availability/combo projection values are explicit and irrelevant
to retained actions, not claims of captured prefix history.

Separate synthetic fresh-start live-controller tests exercise zero-durability terminal
completion,actual managed end,duplicate teardown,return true/false,and last-frame honesty.
They are not reconstructed native prefix evidence. Supplied outcome is one supported
craft,not unattended readiness,global optimality,175% Rust parity,or controlled Raphael A/B.

## Live diagnostic schema3

Live exports now use a detached `AdaptiveLiveEvidenceTrace` rather than serializing the
historical trace wrapper. `EvidenceUsage` labels live confirmation; `AnalysisUsage` and
`HistoricalResults` are absent. The zero historical counters remain separately auditable
in Summary. Nonempty historical results/submissions cannot be relabelled as live evidence.
Shadow exports stay schema6 with existing historical labels and behavior.

Live correlation/terminal/end events no longer say "NOT native action-ID confirmation",
"Execution remains disabled",or "Shadow terminal observation". They explicitly retain
experimental layout/validation limits. `NativeEvidence.ExecutionAuthorized=false` remains
evidence-only authority,not a statement that the armed host never sent actions; usage text
explains that distinction. NativeLayoutValidated stays false.

Successful managed end updates both live Status and confirmation end reason to
"managed craft ended",not "awaiting managed end". Repeated teardown is idempotent and
does not downgrade coordinator completion. Failed/unconfirmed trial reasons remain intact.
The fixture retains the original stale source status rather than rewriting history.

Acceptance-patch verification:390 Adaptive tests pass in Debug/Release,193 compatibility
and566 acquisition tests pass (1149 distinct;10 pure and4 compiled-plugin cases added).
Sequential forced Debug/Release solution builds pass with only the two existing unchanged
submodule nullable warnings. Tracked diff,75 untracked text files,andtracking plan pass
whitespace checks. Legacy executor diff remains zero. Logs:
`/tmp/opencode/adaptive-live-acceptance-{debug-tests,release-tests,compat-tests,acquisition-tests,debug-build,release-build}.log`.
No new coding-agent native trial or commit/push in that evidence/diagnostic slice.
Per-root live search observability is implemented in the next slice below; optimizer
tuning remains separate.

## Live schema4: bounded per-root decisions

`LiveAnalysis` now retains up to64 immutable root records, independently of the64-event
confirmation ring. Each record binds full coordinator session/revision/generation identity,
confirmation observation revision,confirmed action ordinal,actual native input frame,
history-derived search-state projection,active goal,and unchanged search limits.
Repeated stable samples do not create roots; terminal packets do not manufacture roots.

The coordinator's opt-in diagnostic seam copies already replay-validated publication
metrics/actions. It does not schedule search,replay again,or change rankings/budgets.
Record fields separate submission,worker dispatch (not CPU start),host-observed result,
publication phase/status/reason,validated proposed route/final quality/target/action count/
modeled duration,search diagnostics,seed replay/comparison,and revocation. Found-but-replay-
rejected,faulted,cancelled,stale,and discarded results cannot manufacture a validated plan.
Proposals carry Executable=false; they are past decisions,not leases or observed outcomes.

Exact decision identity links the one-use transaction; ticket conflicts never replace
the original transaction. Proposed action and actually invoked action remain distinct.
Each record retains raw native return,acknowledgement/full-frame/terminal timestamps,
transaction outcome,and close reason. True/false still await the same native evidence.
Pause/unavailable state/stop revoke undispatched recommendations without labelling them
as executed. Older-session results cannot attach to current roots; late evicted-root
events are explicitly unlinked rather than guessed from action ID or step index.

Bounds:64 roots,64 actions per diagnostic plan,two seed assessments,64 coordinator
notifications,and64 unlinked lifecycle entries. Separate total/retained/evicted counters
cover root eviction,unlinked eviction,and coordinator notification loss. Diagnostic
text is bounded to1024 characters plus marker. Oversized/malformed diagnostic plans or
seed batches are omitted with a warning,not silently accepted or used to stop/tune the
solver. Production plan bound remains64; diagnostic omission tests with larger offline
limits prove logging cannot change validated execution choices. No search trees/nodes
are retained. Snapshots detach read-only arrays and survive subsequent updates/disposal.

### Timing semantics

All root timestamps are session-relative host monotonic times. Production coordinator
notifications capture host-observation/publication times at their source,not a later
history drain. Engine-reported elapsed and modeled plan duration stay separate.

- Submission→result observation includes worker waiting/work and host polling effects;
  it is not worker-start latency or exact task completion time.
- Publication→dispatch includes readiness and configured execution delay; not pure search overhead.
- Dispatch→acknowledgement/full-frame/terminal measures host confirmation latency;
  not native execution duration or network-only latency.

Diagnostics add bounded copying/serialization work; no zero-overhead,speedup,peak-memory,
or native performance guarantee is claimed. Export/UI snapshots rebuild only when
diagnostic/evidence versions change,not every unchanged frame. UI reads published managed
snapshots only. Latest root and expandable history show proposed versus dispatched action,
validated quality/target/length/modeled seconds,budget usage,seed comparison,and named host
timings. Missing observations remain null/"not observed",not zero-valued claims.

### Second supplied successful run

Fixture `Fixtures/live-schema3-manipulation-success.json` preserves coordinator session
`4c4f7b5b-a45f-42ab-9e18-d78f98d14061` / confirmation session
`53cd4342-e840-4f23-8127-6673a84b7069`:18 acknowledgements,17 full-frame correlations,
full target12000,managed end,zero mismatch/rejected result/outstanding transaction,and
zero historical searches. Final Groundwork100410/sequence21/input step18/Normal reports
6600progress,12000quality,5durability,flags278(0x116). Last actual frame remains
5118progress,12000quality,25durability,CP86,Veneration1. Retained nonterminal flags274
(0x112) and terminal flags278 preserve unknown bit0x100 without interpreting its meaning;
required success/completion/scalar checks remain unchanged.

Only seven input frames steps12–18,seven packets15–21,six known returns plus an evicted
unknown return,final transaction/Finish,and source summary are frozen. Earlier prefix
is not reconstructed. Regressions replay retained Manipulation recovery/expiration and
terminal scalar normalization; they do not reproduce all18/17 source totals or invent
missing per-root search history. Source schema3 verifies managed-end/live-label fixes.
The18-action and earlier16-action runs differ in route/conditions and are not controlled
A/B or proof of optimizer superiority. Good here is consumed by Innovation,not new
enhanced-Good quality arithmetic evidence. Broader native acceptance remains pending.

Schema4 verification:415 Adaptive tests pass in Debug/Release,198 compatibility and566
acquisition tests pass (1179 distinct;25 pure and5 compiled-plugin cases added). Sequential
forced Debug/Release solution builds pass with only two existing unchanged submodule
nullable warnings. Tracked diff,78 untracked text files,andtracking plan pass whitespace
checks. Legacy executor diff remains zero. Logs:
`/tmp/opencode/adaptive-live-analysis-{debug-tests,release-tests,compat-tests,acquisition-tests,debug-build,release-build}.log`.
No new in-game trial,optimizer tuning,saved settings,default solver changes,commit,or push.

## First supplied schema-4 history and offline search follow-up

Coordinator session `ea732efa-7fde-43d2-b211-31fce4326013`, confirmation session
`46855015-d43e-49fc-97c0-0aecd81fc4fa`, recipe5618/CUL/tool51785/175%:

- All16 root proposals link to their matching invoked actions; all16 raw returns
  are true, but remain diagnostics rather than execution proof. Source reports
  16 acknowledgements,15 independent full-frame correlations,managed end,and no
  mismatch,rejected result,unlinked event,root loss,or historical search.
- Good root3 Preparatory Touch increases quality1620→3090. Excellent root10
  switches the proposed Byregot continuation to Trained Finesse, reaching observed
  quality12000 while preserving IQ10/durability40. These are actual replanning
  witnesses, not evidence that future conditions were predicted.
- Terminal Groundwork100410/stream1/sequence19/op9 retains input step16/Normal,
  progress6600,quality12000,durability0,flags30. Last real frame remains
  step16/progress5118/quality12000/durability20/CP80/IQ10/Veneration1.
  No terminal CP/status/full-frame values are inferred.
- Confirmation ring evicts99/163 events; separate root history retains16/16 with
  zero coordinator/lifecycle loss. Compact fixture preserves complete root/plan
  history and transaction metadata, not omitted earlier callback packets.
- Search elapsed spans25.5145–73.3837ms for roots1–15; final root0.0496ms.
  Host submission→result observation spans33.1528–109.6781ms. First publication→
  dispatch71.4305ms is unexplained and must not be attributed to search. Other
  publication→dispatch gaps are about0.655–3.289ms. Host timings are not worker
  CPU timings or native execution duration.

`adaptive-search-tuning.md` documents the subsequent offline-only patch: narrow
redundant Final Appraisal filtering, comparator-safe bounds, exact-state label
handling, and bounded complementary completion probes. Captured-root replay now
finds full-quality continuations at roots1–2 and reduces combined roots12–15
transitions by ~55%, with unchanged limits/comparator. These are deterministic
offline comparisons, not a new native trial or a guaranteed live speedup. No
executor, native binding, lease, ownership, confirmation, timing, saved-setting,
or solver-default behavior changed. Existing native acceptance gaps remain open.

Follow-up verification:463 Adaptive tests pass in both Debug/Release (48 added),
212 current compiled-plugin compatibility cases and632 current acquisition cases
pass (1307 distinct cases across the three suites). The independent22504 Rust
transition corpus still passes. Sequential forced Debug/Release solution builds
pass with only the two existing unchanged ElliCon/ElliLib nullable warnings.
Tracked diff and84 untracked text files pass whitespace checks. Logs:
`/tmp/opencode/adaptive-pruning-{debug-tests,release-tests,compat-tests,acquisition-tests,debug-build,release-build}.log`.
Tracking plan file left unchanged; no new native trial, commit, or push.

## Seed-first managed startup (2026-11-15)

Implemented after the three-way comparison demonstrated that validated Raphael seeds
close the quality gap in the tested Normal-only cases. Runtime arming still applies
to one managed ordinary craft; no saved/selectable Adaptive mode was added.

### Before synthesis

- Only armed ordinary `StartCraft` tasks insert the preparation gate, after ingredient
  assignment and before the existing synthesis callback. Unarmed Standard/Raphael/macros
  and Quick Synthesis retain their previous task paths. Home travel and existing
  consumable/job preparation precede this gate.
- The gate reads strict current recipe/job/stats/tool evidence and active goal/policy.
  Its request uses the existing ingredient policy's **guaranteed starting-quality
  estimate**, explicitly not a native observed quality value. Specialist actions are
  disabled for this candidate because they are outside Adaptive's supported mechanics.
- Try the immutable exact-request cache sidecar. On missing/pending data, make one
  urgent enqueue/promotion request through `RaphaelSolveCoordinator`; its existing
  request-key deduplication and process-concurrency limit remain authoritative. No
  second process launcher, direct CLI invocation, forced retry, or shared cache removal.
- A ready detached candidate (at most64 actions) may be below desired quality. Failed,
  disabled, unsupported, missing executable, disappeared/cancelled request, exception,
  or timeout gives an explicit **Unseeded** reason and permits the original bounded
  search lane. A cached failed request is not automatically deleted/retried.
- Preparation timeout uses configured Raphael minutes (clamped to the existing UI's
  1–60 range), including queue/UI preparation wait. The task-manager outer guard has
  30s headroom so ordinary polling reaches the explicit fallback instead of its short
  legacy3s synthesis-task timeout. No synchronous wait, sleep, or framework-thread solve.
- Recheck the recipe selection, preparation ticket, pause state, and captured context
  immediately before synthesis. Disarmed/stale tickets abort the queued start; they do
  not silently switch that attempt to the legacy executor. Context drift discards the
  candidate and permits unseeded startup rather than requeueing forever.
- Queue-level legacy Raphael hard gates defer to this preparation only for an armed
  ordinary trial. Otherwise a pre-existing failure would skip the craft before Adaptive
  could apply its intended fallback. Existing legacy skip/wait behavior and Quick
  Synthesis exclusions remain for unarmed/quick paths. Background warmups may still
  share the same coordinator; they are never cancelled by this gate.

### Actual starting root and execution

`TryBegin` still claims exclusive ownership before the legacy executor, requires fresh
native StartPrepare/StartInfo, and attaches only once. It consumes the matching prepared
candidate once; it never queues a solve, fetches a late cache publication, or imports a
suffix after the craft starts. A managed craft lacking preparation can still start
unseeded. Preflight candidate/timeout state cannot grant an execution lease.

After two equal independent native frames confirm the real fresh starting root, the
coordinator independently replays the **whole candidate** under the actual captured
state, supported model, active mandatory/desired/strict goal, and current plan-length
bound. It also checks recipe/stats/unlock/non-specialist provenance. Actual quality
may exceed the guaranteed estimate (e.g. HQ fallback materials); it remains a separate
observed field and is never overwritten to match the solve request. Full-macro replay
from that actual root, not origin-quality metadata or guessed suffix alignment, decides
acceptance. Invalid/strict-target-incompatible candidates are dropped before dispatch
and unseeded search proceeds.

Accepted candidates go to the existing bounded worker as initial-root seeds. The
production scheduler now forwards those seed arguments. Publication still replays
the worker's selected plan; an independently replayed startup baseline cannot be
replaced by a lower-ranked worker plan. No seed or plan metadata becomes observed state.
Later decisions carry only exactly aligned prior live continuations, advanced after
confirmed native acknowledgements plus independent full-frame correlation. Pause,
cancellation, lease consumption, queue occupancy, native-return neutrality, ambiguous
outcome handling, and no-retry/no-fallback-after-dispatch rules are unchanged.

The preparation's cancellation/ticket is session-local. It drops its own candidate
without cancelling shared solves. Results arriving after timeout/start/disarm may enter
the shared cache for future use, but cannot attach to the active trial. Begin/Stop/Dispose
clear pending startup candidates; new sessions do not inherit them.

### Diagnostics and acceptance scope

Additive live schema4 `SeedStartup` shows phase, bounded reason/request key/source,
action count, queue/promotion-request flag, expected policy quality, and separately
observed starting quality. Pre-craft exports have `StartAccepted=false`; none are leases.
UI shows waiting, prepared, replay-validated, cancelled, and unseeded-fallback reasons.
No account/character identity or mutable cached action list is exported. Existing
bounded per-root seed diagnostics retain engine replay/comparison evidence.

Pure tests cover cache hit/miss/pending promotion, one request per attempt, failed or
disabled/unavailable generation, exact timeout and late-result rejection, cancellation/
rearm, drift in character/job/stats/tool/goal/policy/request/quality estimate, unsupported
or oversized seeds, clock regression, bounded reasons, native-quality independence,
actual-root strict/mandatory rejection, default scheduler propagation, preservation
against faulty worker ranking, and two-native-frame live admission. Compiled-plugin
checks inspect pre-callback gating, shared cache/queue use, no mid-craft solve, and
armed-only deferral of legacy queue hard gates. These are not native execution tests.

First supplied cache-hit seeded trial now succeeds; scope and fixture are recorded
below. Cache-miss/generated-seed native trials remain pending, including
disarm/pause/context changes while generation is in progress. Existing broader native
acceptance gaps and experimental layout labels remain. No in-game craft, automatic
mid-craft cache adoption, settings migration, commit, or push accompanies this patch.

Seed-first verification:502 Adaptive tests pass in both Debug/Release (36 added),
216 compiled-plugin compatibility cases (4 added) and632 acquisition cases pass:
1350 distinct cases. Pinned independent transition and three-way macro comparisons
remain intact. Sequential forced Debug/Release builds pass with only the two existing
unchanged submodule nullable warnings. Tracked diff and89 untracked text files pass
whitespace checks; legacy action executor diff remains zero. Logs:
`/tmp/opencode/adaptive-seed-start-{debug-tests,release-tests,compat-tests,acquisition-tests,debug-build,release-build}.log`.
Tracking plan file remains unchanged; current implementation/status is recorded here.

## Seeded native acceptance and completion-bound follow-up

Supplied schema4 coordinator session `24292641-915b-4517-9a55-1311fce7191b` /
confirmation session `9eb9e62e-09c9-41eb-9b91-18e8577ecdde` validates a17-action
RaphaelCache candidate from actual starting quality0. No generation was requested.
All17 root searches retain exact macro continuations;17 acknowledgements/16 independent
full-frame correlations lead to terminal Delicate Synthesis6600/12000/5 durability and
managed end, with zero mismatch/rejected result/history linkage loss. Last real frame
remains6209progress/11775quality/CP86; final CP/status/full frame are not fabricated.
Compact fixture retains complete roots/plans and source transaction metadata, not
evicted raw callback packets. Broader native and generated-seed acceptance stays open.

`adaptive-seeded-search-budget.md` documents the subsequent offline optimizer patch:
stronger optimistic progress/time bounds and explicit termination/effective-cap/prune
diagnostics, with exhaustive/reference regressions. Requested full limits remain40k
transitions/20k nodes/100ms. Optional immutable seeded-improvement allowances were
benchmarked at8k/16k/24k, but **remain null/disabled in the live default** because broader
cases lost useful route improvements. No condition-budget rule or comparator change.
Proven bounds alone reduce finishing-root work substantially in the earlier fixture;
the latest17-root trace benefits more modestly. None of these offline numbers is a
new native execution or dispatch-latency measurement. Execution safeguards and saved
solver/settings selection remain unchanged.

Final follow-up verification:555 Adaptive tests passed in each of Debug/Release,
216 compatibility and632 acquisition tests passed (1403 distinct). Forced sequential
Debug/Release solution builds succeeded with only the two existing ElliCon/ElliLib
nullable warnings. Fixture-test nullable warning fixed; final test logs warning-free.
Tracked/untracked whitespace checks passed; legacy `CraftingActionExecutor.cs`
unchanged. No commit/push; generated-seed and broader native acceptance remain open.
