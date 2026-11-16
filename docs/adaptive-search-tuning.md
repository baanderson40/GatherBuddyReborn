# Adaptive search depth and allocation tuning

## Status

The original tuning slice below changed only the offline optimizer; later
runtime-only host work is documented in `adaptive-live-host.md`. That slice added
no live execution, solver selector, settings, or native host wiring. It retained the same 40,000-transition / 20,000-node
corpus work limits and the same recipe data, goals, stats, conditions, and seeds.
No recipe IDs or reference macros are passed to production search as hints.

## Diagnosis

Pre-tuning decision traces are retained in benchmark output. On the difficult
recipes, the first chosen plan projected only 225–266 quality despite the much
higher target. Later turns spent resources on repeated setup, premature repairs,
or quality actions that left no CP for Byregot's. The wide search spent its
budget before seeing long quality/repair/progress combinations. Completion
probes discarded quality cash-out options and their narrow ranks undervalued
Veneration because it has no immediate progress gain.

Allocation profiling isolated two costs:

- Each simulated legal edge constructed input/output `StepState` objects.
- All generated nodes, including discarded beam candidates, were retained in
  parent arenas and closed sets; geometrically growing containers added traffic.

An intermediate scratch-only run preserved the old 18/24 target result while
reducing measured average allocation from ~12.44 MB to ~6.74 MB per decision.
The remaining depth/retention changes reduced it further.

## Changes

- `Simulator.ExecuteInto` shares existing simulator arithmetic with the public
  allocating API, but writes into distinct caller-owned storage. Every output
  field is assigned, including failed-action fields; aliased input/output is
  rejected. Worker-local scratch objects are never stored on a shared simulation.
- All search stages and replay validation share one per-search workspace. Scratch
  state is reset between heuristic evaluations and real transitions. Existing
  public simulation remains independent and usable concurrently.
- Only retained beam nodes enter long-lived parent/closed storage. Reusable
  per-layer candidates are deduplicated separately. Node-admission counters are
  still cumulative work limits, not measurements of resident RAM.
- A narrow deep continuation pass explores up to 32 actions, capped by maximum
  plan length, using at most 60% of the total transition allowance. The main beam
  still uses its configured horizon. All stages share the original total limits,
  cancellation token, and monotonic time budget; this is not extra search work
  beyond those limits.
  Transition counters count search-edge expansions, not mandatory replay or
  scalar heuristic calculations, as before. Replays are length-bounded and
  included in elapsed/allocation measurements; wall-clock limits are not hard
  real-time deadlines.
- The deep pass retains both quality-leading and resource-preserving states,
  alternating exact probes between them. The main beam retains root diversity.
  Rank values for future quality/buffs are optimistic guidance, not bounds and
  not executable recommendations.
- Short cash-out probes compare actual Byregot's / Great Strides / Innovation
  setup sequences and their constructive progress suffixes, including forced
  condition transitions. Full root replay remains mandatory for incumbents.
  A quality estimate alone never authorizes a plan.
- Completion ranking values Veneration's future contribution, not just current
  progress. A one-action target-reaching finish is checked first, comparing
  available one-action resource ties without allocating a whole search tree.
- Diagnostics now expose deepest explored layer and deep-pass transition count.

All closed-state/rank/beam decisions remain approximate. Unknown is still not
proof of impossibility. No greedy or unchecked native fallback was introduced.

## Fixed-corpus results

All 24 original runs still complete. Full raw-quality targets improve from
18/24 to 20/24. Every original target-reaching case is retained. All six original
misses improve, enforced by pre-tuning regression floors in the tests:

| Craft/scenario | Target | Before | After |
|---|---:|---:|---:|
| Lunar Adamantite Ingot, Normal | 7,800 | 5,600 | 6,838 |
| Lunar Adamantite Ingot, seed 2026 | 7,800 | 6,020 | 7,800 |
| Ipe Lumber, Normal | 12,000 | 8,018 | 9,495 |
| Ipe Lumber, seed 2026 | 12,000 | 10,678 | 12,000 |
| Tacos de Carne Asada, Normal | 12,000 | 7,447 | 9,282 |
| Tacos de Carne Asada, seed 2026 | 12,000 | 8,291 | 10,379 |

Selected recipes still fall short. This is not an optimality or real-game HQ
success claim; targets are raw quality and the corpus uses bounded simulation.
Standard and frozen Raphael baselines remain comparison-only.

## Performance observation

Same-machine Release runs, cutoff disabled for deterministic work comparisons:

| Metric | Before | After |
|---|---:|---:|
| Simulated crafts completed | 24 | 24 |
| Adaptive decisions | 224 | 235 |
| Total allocation traffic | ~2.79 GB | ~0.68 GB |
| Mean allocation/decision | ~12.44 MB | ~2.89 MB |
| Per-craft decision p95 range | ~13–88 ms | ~0.01–50 ms |

Mean measured allocations fall about 77%. These counters include replay and
trace/test overhead, are not peak RAM, and vary with runtime/JIT/GC effects.
The p95 range is across individual craft runs, not aggregate p95 and not a
wall-clock guarantee. Decisions/actions increase on some harder crafts to obtain
more quality; action-count improvements are not promised.

The warmed edge-allocation microtest observed ~1.70 MB for 8,192 allocating
transitions versus ~8 KB incidental traffic with scratch storage. It asserts a
large relative reduction, not literal zero runtime allocations. Fixed-corpus
tests also guard allocation traffic with a conservative 6 MB/decision ceiling;
no elapsed-time assertion is used.

## Verification and holdouts

- Original 22,504 independent Rust action projections still match. Allocating
  and scratch execution are compared for every reference transition.
- Exhaustive small-craft oracle, cancellation, strict/mandatory targets, budget,
  state ownership, and async lifecycle tests remain intact.
- New tests cover scratch failure reset, alias rejection, concurrent searches,
  heuristic scratch isolation, deep budget sharing, real cash-out/progress replay,
  Excellent usage, last-durability safety, and recipe-ID independence.
- Holdouts add seeds 101/7379, initial quality 0/25%, and CP unchanged/reduced
  by 20 on the three hard same-level profiles: 24/24 complete, 14/24 reach full
  raw quality. These were not used to tune coefficients. Small regression
  scenarios, not statistical rates or independent recipes.

Remaining gates: broader recipes/stats, peak-memory profiling, native observed
state and combo-history verification, stable live capture/goals, and shadow-mode
integration before automated execution. Four original runs still miss target;
quality-search optimality is not established.

## Schema-4 evidence-driven pruning (2026-11-15)

This follow-up changes only the offline search and tests/docs. Runtime limits,
independent replay, goal comparator, host execution timing/defaults, and live
transaction safeguards are unchanged. The complete supplied per-root history is
frozen in `Fixtures/live-schema4-root-history-success.json`; its native scope and
omitted callback events are documented in the fixture README.

### Proof-based changes

- Filter a Final Appraisal successor only when **every state field except CP is
  exactly unchanged** and CP decreases. This includes combo, first-step,
  condition, all buffs, and remaining-use flags. Ordinary supported mechanics
  cannot benefit from spending CP with no other effect: extra CP never disables
  an allowed action; capped Tricks gains may erase but cannot reverse it.
  Useful first casts and non-identical refreshes remain searchable. Simulator
  legality and seed replay are untouched; inefficient legal seeds are not
  rejected, rewritten, or silently repaired.
- Once an incumbent reaches target, prune nonterminal prefixes whose earliest
  possible completion loses its duration/action-count comparison. The lower
  bound deliberately assumes just one more action at the **minimum duration of
  any supported action**, currently two modeled seconds. It does not assume a
  progress finisher, resource feasibility, or heuristic potential. Equal
  time/count branches remain for resource and lexical ties. No duration bound
  is applied against a subtarget incumbent, since higher quality outranks time.
- Exact-state duplicate labels require both no-greater duration and no-greater
  depth; exact ties compare the whole action prefix lexically. Faster but longer
  paths remain incomparable because remaining plan length matters. Only one
  bounded representative is remembered per state; incomparable paths still
  enter beam selection. Missing a representative can miss pruning, never justify
  discarding an incomparable path. Lexical comparisons use bounded stack storage.
- Completion probes now keep only retained beam witnesses in their closed set;
  discarded candidates cannot suppress later paths. Per-layer duplicates are
  compared separately. Node admission remains a cumulative bounded-work counter,
  not resident-memory measurement. Completion search still returns a candidate
  at its first successful depth: it is not a fastest-duration proof.

### Bounded probe selection

Simply removing redundant refreshes changed approximate beam selection and lost
75 quality on an existing unseeded regression root. That intermediate change was
not accepted. The deep pass now retains its rank-leading completion probe and
uses a second configured probe, alternating highest achieved quality with best
economy. One-probe configurations keep the previous alternating behavior; zero
still disables probes. No coefficients, recipe-ID hints, extra workers, or larger
budgets were introduced. The original seven unseeded regression qualities remain
unchanged. Beam search remains approximate even when individual pruning rules
are sound.

### Deterministic captured-root comparison

Same immutable roots/seeds and work limits, wall-clock cutoff disabled. The
pre-change baseline reproduced every supplied proposal. Results are modeled
plans, not new observed game outcomes:

| Root | Before quality / actions / seconds | After quality / actions / seconds |
|---|---|---|
| 1 | 11400 / 19 / 53 | 12000 / 22 / 62 |
| 2 | 11430 / 33 / 81 | 12000 / 20 / 57 |
| 3 | 12000 / 17 / 47 | 12000 / 16 / 44 |
| 6 | 12000 / 13 / 35 | 12000 / 13 / 34 |

Root1 is longer because reaching target outranks speed. Root2's old repeated
Final Appraisal plan remains independently replayable; its +30 quality beat an
18-action/50-second continuation under the unchanged subtarget comparator.
The new route reaches target without that refresh chain. The published root6
improvement in the original trace is also verified: at equal 13 actions/35
seconds, final CP12→20 and durability0→30 (CP decides before durability).

| Root | Before transitions | After transitions |
|---|---:|---:|
| 12 | 40000 | 33240 |
| 13 | 40000 | 24960 |
| 14 | 40000 | 12630 |
| 15 | 40000 | 1050 |

Combined work falls 160000→71880 (~55%). These roots keep the same validated
5/4/3/2-action continuations. This is not an early-exit optimality claim or a
wall-clock latency guarantee. Root11 still exhausts its work budget.

The former 24 holdout scenarios now complete 24/24 and reach full raw quality
16/24, versus 14/24 before this patch. They were inspected during probe-selection
regression work, so they are no longer untouched holdouts for this follow-up.
Independent broader native acceptance and enhanced-Good Rust parity remain open.

Actual-plugin fixed-corpus comparison completes24/24 and reaches full quality
21/24 (previously20/24). All prior per-case quality floors and all20 former
target cases are retained. Ipe Normal increases9495→9521; Tacos Normal
9282→10053 and seed2026 10379→12000. Lunar Adamantite Normal remains6838/7800.
Standard and frozen Raphael routes remain comparison-only and unchanged. Three
original cases still miss target; no optimality or statistical success claim.

The subsequent measured three-way comparison in
`adaptive-three-way-comparison.md` separates unseeded search from fresh Raphael
and Raphael-seeded Adaptive. Fresh solves supply macros for all three remaining
cases, including full-quality Tacos after18.2s. Seeded Adaptive matches their
quality/routes in deterministic Normal-only replay. This is test/report work,
not live Raphael-seed startup wiring or a change to the unseeded corpus results.

The later seeded-native trace and completed bounds/allowance experiments are in
`adaptive-seeded-search-budget.md`. Progress-distance lower bounds now supplement
the weak one-action bound. Optional reduced allowances are explicit policy
truncation with separate diagnostics and remain disabled by default:8k/16k/24k
lost useful per-case improvements despite preserved aggregate target counts.
