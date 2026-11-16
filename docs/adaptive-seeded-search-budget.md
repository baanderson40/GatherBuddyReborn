# Seeded Adaptive search: proven bounds and allowance experiments

2026-11-15. **Stronger proven completion bounds enabled; reduced seeded-search
allowances NOT enabled by default.** The8k/16k/24k experiments save work but miss
useful improvements in broader cases. Live startup, seed replay, comparator,
40k-transition/20k-node/100ms upper limits, and execution safeguards remain intact.

## Supplied native evidence

`Fixtures/live-schema4-seeded-cache-success.json` freezes the supplied cache-hit
trial: recipe5618/CUL,5953 craftsmanship/5490 control/664CP, tool51785/175% Good,
zero initial quality. Coordinator session `24292641-915b-4517-9a55-1311fce7191b`;
confirmation session `9eb9e62e-09c9-41eb-9b91-18e8577ecdde`.

- Actual root1 accepts the17-action/45-modeled-second RaphaelCache macro to12000
  quality. `SeedStartup.Phase=Validated`, `GenerationRequested=false`, expected
  and observed initial quality0. This proves a cache-hit path, not cache-miss generation.
- All17 root proposals match their invoked actions and remain exact original
  macro suffixes. Every source comparison is `Tied`; no route improvement was found.
- Source reports17 matched acknowledgements and16 independently correlated full
  frames, zero mismatches/rejected results, zero historical searches, and managed end.
- Root7 Good Delicate Synthesis increases observed quality0→525 and progress5498→6209.
  Manipulation/Waste Not/Veneration effects remain separately captured in frames.
- Final Delicate Synthesis100330/stream1/sequence20/op9/input step17/Normal/flags22
  reports6600 progress/12000 quality/5 durability. Last real frame remains
  step17/progress6209/quality11775/durability15/CP86/Innovation1/IQ0. No final CP,
  statuses, combo, or full frame is invented from the terminal packet.
- Confirmation ring evicts109/173 events; all17 root records survive with zero
  root/coordinator/lifecycle drops or unlinked events. Earlier callback packets
  are omitted from the compact fixture, not reconstructed. Tests replay plans and
  observed non-condition transitions; they do not recreate omitted acknowledgements.

This supports seeded one-action/confirmation execution for one craft, not global
native-layout acceptance, unattended readiness, or a controlled live A/B comparison.

## Proven progress bound

The old proof assumed one further action at the minimum supported duration, two
modeled seconds. The new worker-local lazy bound instead computes:

1. Optimistic maximum progress gain `U` of any supported action through the shared
   `Simulator.CalculateProgress`, with simultaneous maximum Muscle Memory/Veneration,
   Good eligibility, and full Groundwork efficiency via free durability cost.
2. Minimum duration `T` of a supported action with positive progress gain.
3. At a nonterminal state, minimum progress-action count
   `N = ceil((RequiredProgress - Progress) / U)` and minimum duration `N*T`.

Setup/CP/unlocks/remaining-use constraints are deliberately relaxed. The current
ordinary model's maximum positive progress multiplier is10; expert/Malleable,
mission, and unsupported effects are rejected at the simulation boundary. Numeric
profiles near float-to-int overflow, negative gain, or zero maximum gain fall back
to the old weak bound instead of claiming impossibility. Roster/projection tests
guard these assumptions, including the22504 independent transition rows.

Only target-reaching incumbents authorize time/count pruning. Worse lower-bound
time is pruned; equal time/count stays for perfection/CP/durability/lexical ties.
Below-target incumbents still prioritize quality. Quality work is ignored, not
added separately: Delicate Synthesis can satisfy both objectives at once. Existing
heuristic `ProgressBuffPotential`/`FinisherPotential` functions are not proofs.

The bound is computed once per search workspace, not per node or on shared static
scratch. All search/probe stages use it consistently. Independent complete-plan
replay and existing exact-state label handling remain unchanged. A sound pruning
rule does not turn approximate beam selection into a global optimality guarantee.

## Diagnostics and immutable experimental policy

`AdaptiveSearchLimits.SeededImprovementTransitions` is optional and defaults to
null. Non-null values are **explicit policy truncation**, not proof of optimality.
The field participates in structural decision identity. No persisted settings,
selector, or live default is added. `UseProgressCompletionBound` defaults to true;
false exists for reproducible old-proof comparisons.

The effective allowance can shrink only after an INPUT seed or continuation has
independently replayed to desired target. Invalid/oversized/subtarget/missing inputs
retain the original full limits, even if later search discovers its own target
incumbent. Replay precedes activation. Immediate finishes, baseline completion,
deep/wide passes, and probes share one effective transition cap, original node
limit and wall-clock deadline. No hidden second search or new worker.

Diagnostics add scalar fields:

- `SeededInputEligible`: accepted input completion reaches desired target; does not
  mean a reduced allowance was configured.
- `EffectiveMaximumTransitions`: actual cap, bounded by original maximum.
- `ProvenBoundPrunes`: proven bound rejection count, not simulated transitions.
- `Termination` / readable `TerminationReason`: Completed, TransitionLimit,
  StoredNodeLimit, TimeLimit, SeededImprovementLimit, Cancelled.

`BudgetExhausted` retains hard transition/node/time semantics; deliberate policy
truncation has its own reason. Multiple reached criteria are resolved in order
cancellation, time, nodes, hard transitions, seeded allowance. These are end-of-
search observations, not CPU interruption timestamps. Completed means bounded
passes finished, not a proven optimum or native craft completion. UI displays
effective cap, eligibility, stop reason and prune count. All additions remain
diagnostic and fit existing bounded history; native observation fields are untouched.

## Controlled measurements

All work comparisons disable wall-clock cutoff while retaining node/transition/
depth limits. Old-proof/full-budget baselines reproduce the supplied root counters
exactly. Allocations count current-thread allocation traffic, not peak RAM; elapsed
samples include runtime/JIT/GC/scheduling and are not statistical CPU/latency results.

### Same17 captured roots and supplied seeds

| Search policy | Aggregate transitions | Result |
|---|---:|---|
| Old proof/full budget | 511707 | All captured seed floors retained |
| Progress proof/full budget (default) | 506701 | Same quality/duration/resources |
| Progress proof/8k eligible allowance | 121098 | Same quality/duration/resources |
| Progress proof/16k eligible allowance | 233098 | Same quality/duration/resources |
| Progress proof/24k eligible allowance | 339440 | Same quality/duration/resources |

Proof alone saves about1% on this seed's roots; do not describe the experimental
54% reduction from16k as a shipped live-default improvement.

On the earlier16-root fixture, the already-full-quality finishing roots12–15
benefit more from progress-distance information:

| Root | Previous proof transitions | New proof transitions |
|---|---:|---:|
| 12 | 33240 | 17460 |
| 13 | 24960 | 8010 |
| 14 | 12630 | 1650 |
| 15 | 1050 | 450 |

Combined71880→27570 (~62% less work), retaining validated routes. These are fixed-
root offline comparisons, not a new native performance measurement.

### Fresh three-recipe macros and explicit condition scenarios

Three recipes × Normal / Good at advancing step7 / Excellent at advancing step10
(forced Poor afterward): nine crafts per policy,45 simulated crafts total. Conditions
come from the same step-based driver, but different actions/routes can consume them
differently; this is not a controlled native A/B or random-success-rate estimate.

| Policy | Aggregate transitions | Aggregate modeled seconds |
|---|---:|---:|
| Old proof/full | 6435332 | 516 |
| Progress proof/full | 6198701 | 516 |
| 8k eligible | 2450656 | 519 |
| 16k eligible | 3466982 | 516 |
| 24k eligible | 4426104 | 516 |

Every craft completes. Eight of nine reach target; Normal Lunar retains7653/7800.
8k misses Good Lunar's54-second/20-action improvement and takes57seconds/21actions.
16k and24k preserve all nine endpoint metrics, but that alone is insufficient evidence.

### Broader seeded fixed corpus and former holdout scenarios

Add24 fixed cases (12 recipe/stat profiles, Normal/seed2026) and24 former holdout
cases (three hard recipes, starting quality0/25%, CP unchanged/reduced20, seeds101/
7379). Each case runs full,16k,24k:144 more simulated crafts. Fresh three-way macros
override missing/older entries; other profiles use their frozen Raphael macros.
Reduced-CP origins are NOT relabelled: mismatching startup provenance starts unseeded;
later aligned own continuations may qualify. Both lanes use the same actual state.

| Dataset | Full transitions | 16k transitions | 24k transitions | Full/16k/24k target counts |
|---|---:|---:|---:|---|
| Fixed24 | 6262946 | 3449652 | 4478530 | 23 / 23 / 23 |
| Former holdout24 | 14776068 | 9485221 | 11363471 | 20 / 20 / 20 |

All144 crafts complete and satisfy mandatory quality. These seeded target counts
are not comparable to the earlier UNSEEDED21/24 and16/24 results as if search alone
improved: input routes and startup admission differ.

Important per-case losses, despite equal aggregate target counts:

| Ipe: starting quality3000, CP620 | Full seconds/actions | 16k | 24k |
|---|---|---|---|
| Condition seed101 | 47 / 18 | 54 / 21 | 49 / 19 |
| Condition seed7379 | 47 / 18 | 50 / 19 | 50 / 19 |

16k also loses47→51 seconds/18→20 actions in a reduced-CP Ipe case. Other cases
improve, and low-level cases show resource-only losses. For example level18 recipe51
Normal ends at equal quality/time/count with CP74/durability10 under full search,
versus CP46/durability5 at16k or CP60/durability5 at24k. Favorable aggregate duration
or target totals can hide these per-case regressions.

**Decision: keep full production allowance.** No blanket Normal/Good budget rule,
new rank coefficients, recipe-specific exceptions, or lowered regression floors
were introduced to make a smaller allowance appear safe. Future targeted improvement
search can use these captured counterexamples before reconsidering a live allowance.

## Verification and reproduction

- Frozen seeded native trace tests preserve exact root/plan/transaction/terminal scope.
- Exhaustive small paths compare pruned/unpruned comparator optimum including
  resources/lexical ties, Final Appraisal, strict/mandatory goals, conditions,
  Delicate Synthesis, zero-durability completion, and numeric boundaries.
- Budget tests cover eligibility, unchanged unseeded/subtarget behavior, hard/soft
  caps, replay order, cancellation/time/node stop reasons, identity, and unchanged
  live default. The existing lifecycle/native-boundary suites remain required.
- `AdaptiveSeededBudgetTests` emits captured-root and nine-scenario comparisons.
  `AdaptiveSeededBudgetCorpusTests` emits paired broader results. Routine tests use
  frozen data; no Raphael solve/download or in-game action is launched.

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test tests/GatherBuddy.Adaptive.Tests/GatherBuddy.Adaptive.Tests.csproj --configuration Release --filter "FullyQualifiedName~AdaptiveSeededBudget" --logger "console;verbosity=detailed"
```

Logs/data under `/tmp/opencode/`:

- `adaptive-seeded-budget-baseline.log`
- `adaptive-seeded-budget-stage2-tests.log`
- `adaptive-seeded-budget-experiments.log`, `adaptive-seeded-budget-results.json`
- `adaptive-seeded-budget-corpus-24k.log`, `adaptive-seeded-budget-corpus-results.json`
- `adaptive-seeded-budget-{debug-tests,release-tests,compat-tests,acquisition-tests,debug-build,release-build}.log`

No new native execution, broader layout acceptance, saved settings, commit or push.

### Final verification

- Full Adaptive suite:555 passed in Debug and555 in Release.
- Compatibility suite:216 passed; acquisition suite:632 passed (1403 distinct tests).
- Forced nonincremental solution builds ran sequentially, Debug then Release:
  both succeeded with zero errors. Each reports only the two existing nullable
  warnings in unchanged ElliCon/ElliLib submodules; no new plugin warnings.
- Fixed the new fixture-test nullable warning with an explicit non-null assertion;
  final Adaptive test logs are warning-free.
- `git diff --check` passed; all95 untracked text files passed trailing-whitespace
  checks. `GatherBuddy/Crafting/CraftingActionExecutor.cs` has no diff.
- No commit/push. Generated-seed startup and broader native acceptance still need
  in-game evidence; offline tests do not close those gaps.
