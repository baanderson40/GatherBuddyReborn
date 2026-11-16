# Unseeded Adaptive vs fresh Raphael vs Raphael-seeded Adaptive

Measured 2026-11-15. **Seeded Adaptive matches fresh Raphael on all three tested
crafts**, including full-quality Ipe and Tacos. No production search, settings,
executor, or live seed wiring changed for this report.

## Controlled inputs

- Same pinned recipe/stat profiles as the existing independent reference corpus.
- Zero starting quality, all-Normal conditions, ordinary reliable actions,
  Manipulation enabled, no specialist actions, food, potion, or backload constraint.
- Lunar Adamantite Ingot: recipe34890, level90, craftsmanship3400/control3300/CP540,
  durability35, progress2925, quality7800.
- Ipe Lumber: recipe35907, level100, craftsmanship4900/control4800/CP620,
  durability35, progress4125, quality12000.
- Tacos de Carne Asada: recipe5618, level100, same4900/4800/620,
  durability80, progress6600, quality12000. **Not the higher-stat live-trial loadout.**
- Fresh staged Windows Raphael CLI0.2.2, upstream v0.29.2 commit
  `5dc5c4efd756391225617b901b3ec2d7c0fabd9e`, one solver thread, external
  300-second timeout per solve. All three exited0 without interruption. Default
  full-quality objective permits non-max-quality solutions; a subtarget valid
  completion remains usable. CLI hash matches staged build provenance:
  `e475ec83f743720b07ac519de7b05584a1bc7258f6f1f415fb722665d7530edb`.
- Adaptive production sources compiled directly by the game-independent test
  project. Defaults retained: beam256/horizon10, maximum64 actions,40000
  transitions/20000 node admissions per decision, two configured probes/layer.
  Wall-clock cutoff disabled for deterministic work comparisons; this is not a
  test of the production100ms deadline. No per-case search hints or coefficients.

## Method

1. **Unseeded Adaptive:** search from scratch at the initial root. Independently
   replay every published plan; simulate only its first action, then replan from
   the resulting state with the exactly aligned own continuation.
2. **Fresh Raphael macro:** execute the freshly solved macro unchanged through
   the same audited C# simulation. Check its completion, quality, action count,
   and modeled duration against CLI output.
3. **Raphael-seeded Adaptive:** independently replay the complete fresh macro as
   an initial-root candidate, then perform the same one-action/replan loop as
   unseeded Adaptive. Later roots receive their own exact prior-plan continuation,
   not guessed original-macro suffixes. Every accepted seed/continuation must be
   retained or comparator-improved. No live cache or native observation is used.

All outcomes use identical deterministic Normal conditions. Quality is capped at
recipe maximum: CLI raw values12036(Ipe)/12022(Tacos) become12000, not an apparent
advantage over the C# model. Routes terminate on completed progress, including
valid zero-durability completion. Modeled action seconds are not in-game elapsed
time or measured animation/dispatch duration.

## Final quality and route length

Cells show **quality / executed actions / modeled seconds**.

| Recipe | Target | Unseeded Adaptive | Fresh Raphael | Raphael-seeded Adaptive |
|---|---:|---|---|---|
| Lunar Adamantite Ingot | 7800 | 6838 / 20 / 55 | 7653 / 21 / 57 | 7653 / 21 / 57 |
| Ipe Lumber | 12000 | 9521 / 21 / 57 | 12000 / 24 / 64 | 12000 / 24 / 64 |
| Tacos de Carne Asada | 12000 | 10053 / 19 / 53 | 12000 / 21 / 57 | 12000 / 21 / 57 |

All nine simulated crafts complete. In this run, each seeded Adaptive route is
**identical action-for-action** to its Raphael macro; it neither loses the seed
nor improves it. The tests enforce replay/quality/comparator floors, not permanent
route identity, so future valid improvements remain possible.

Fresh Lunar and Ipe action lists match their old frozen macros. Fresh Tacos now
has a complete full-quality macro: the old corpus's two-second interruption
represented a missing incumbent, not infeasibility or a prohibition on partial
quality. That original reference corpus remains unchanged.

## Solve and replan work (separate measurements)

| Recipe | Fresh CLI solve wall seconds | Unseeded Adaptive aggregate transitions | Seeded Adaptive aggregate transitions |
|---|---:|---:|---:|
| Lunar Adamantite Ingot | 2.819 | 724540 | 763310 |
| Ipe Lumber | 6.664 | 824960 | 810457 |
| Tacos de Carne Asada | 18.222 | 751040 | 703110 |

CLI wall measurements include process startup, one local run per recipe. Adaptive
transition totals aggregate all20/21/24/etc decisions; the40000 limit applies to
each decision, not the entire craft. They exclude CLI internal search work and
independent plan-replay steps. Node admissions are also cumulative work counts,
not peak resident memory. Running a fixed macro requires no Adaptive search.

Selected Release-only test-run aggregate search wall observations were127/494/124ms
unseeded and153/288/187ms seeded (Lunar/Ipe/Tacos respectively). These include
runtime/JIT/GC/scheduling effects and are neither CPU measurements nor statistical
latency results. Do not compare them to native dispatch/action latency or treat
this as an equal-compute-budget solver contest: Raphael gets a multi-second
initial solve; Adaptive gets bounded searches at every turn. A real seeded setup
must account for the initial Raphael solve too, unless already cached.

## Interpretation and remaining scope

- The quality gap in the prior table was primarily **unseeded search versus a
  solved macro**, not a failure to preserve supplied Raphael seeds.
- An identical valid seed closes that gap in the tested Normal-only lane. Ipe
  and Tacos both demonstrate feasible full-quality routes missed by unseeded
  Adaptive. Lunar's remaining147-quality shortfall is not proved impossible here.
- All-Normal runs show no condition-adaptation benefit; Good/Excellent/Poor
  outcomes, random-condition macro robustness, and enhanced-Good tool behavior
  are outside this comparison. Action-dependent condition sampling requires a
  separately defined experiment; do not infer it from this table.
- Seeded Adaptive still spends work searching for improvements even when it ends
  up taking the same route. No automatic search-free suffix execution is proposed.
- Historical shadow can already accept an available cached Raphael candidate.
  At measurement time the runtime live trial still started unseeded. This test/report
  itself did not import Raphael seeds into live startup or change safeguards. A later
  seed-first preparation patch is documented in `adaptive-live-host.md`: cache first,
  then shared generation before synthesis, with unseeded fallback and actual-root replay.
- No real game craft was run, native acceptance expanded, release mode enabled,
  or global optimality/statistical success rate established.

## Reproduction and artifacts

`tests/GatherBuddy.Adaptive.Tests/Fixtures/raphael-three-way-macros.json` freezes
fresh action lists, uncapped CLI outputs, exact arguments, timing, and provenance.
Normal tests replay that fixture and do not launch/download/build Raphael.

Fresh CLI argument pattern (substitute the explicit recipe/profile above):

```text
raphael-cli.exe solve --recipe-id 5618 --level 100 --stats 4900 4800 620 --manipulation --initial-quality 0 --threads 1 --output-variables action_ids final_quality duration steps
```

Targeted deterministic comparison from repository root in WSL:

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test tests/GatherBuddy.Adaptive.Tests/GatherBuddy.Adaptive.Tests.csproj --configuration Release --filter FullyQualifiedName~AdaptiveThreeWayComparisonTests --logger "console;verbosity=detailed"
```

Raw fresh CLI capture: `/tmp/opencode/raphael-three-way-fresh.json`.
Selected Release output: `/tmp/opencode/adaptive-three-way-release.log`.
Parsed report data/routes: `/tmp/opencode/adaptive-three-way-results.json`.
Full-suite logs: `/tmp/opencode/adaptive-three-way-{debug,release}-tests.log`.

Verification: all466 Adaptive tests pass in both Debug and Release, including
three new comparison cases and the independent transition corpus. Frozen fields
match raw fresh CLI captures exactly. Tracked diff and changed report/test files
pass whitespace checks. No production files, saved settings, live seed wiring,
or original Rust-reference data changed; no commit/push.
