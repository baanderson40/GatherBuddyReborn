# Adaptive reference and real-recipe comparison

## Scope and provenance

Independent Rust reference generation executes Raphael v0.29.2 at commit
`5dc5c4efd756391225617b901b3ec2d7c0fabd9e`. The explicit exporter checks origin,
commit, and clean Windows-Git status, uses Windows Rust/MSVC with a checked-in
lockfile, and does not modify the checkout or plugin build targets. Ordinary
tests consume frozen JSONL; they never compile/download Rust.

The corpus contains eight real recipe IDs, 12 recipe/stat profiles including
four overgear profiles, and 22,504 action trials under Normal, Good, Excellent,
and Poor. Expected action arithmetic comes from Rust, not C#. Recipe constants
come from pinned Raphael data; stats are explicit test loadouts, not gear
captured from the game. Projection and regeneration details:
`tests/GatherBuddy.Adaptive.Tests/Fixtures/README.md`.

Parity covers 29 shared reliable actions. It excludes Final Appraisal,
specialist/mission/expert mechanics, terminal buffs/history, and unknown native
state-reader behavior. Quality is clamped, durability saturated, and Observe's
combo discount normalized. Next conditions are supplied by the harness;
Raphael's simulator takes the current condition as an input.

**Native-backed erratum (2026-11-16):** pinned Rust incorrectly charges10 durability
for Trained Eye. Its32 legal Trained Eye rows remain untouched in the frozen export.
Comparison explicitly corrects that action's expected durability only; all other
fields/actions still compare normally. This field is no longer claimed as independent
Rust parity. See `adaptive-trained-eye.md` for evidence and bounded erratum tests.

**Additional user-verified erratum:** pinned Rust rejects standalone Refined Touch,
although Basic Touch is required only for its extra IQ bonus. Raw360 otherwise-
eligible illegal rows have no After state and remain preserved. Comparisons correct
their legality explicitly; bounded spec tests cover resulting resource/quality/IQ
behavior without claiming an independent Rust projection. Raw72 legal combo rows
still compare normally. See `adaptive-action-audit.md`.

## Defects exposed and addressed

1. **Quality rounding:** floating-point multiplication could produce 944.999...
   where the correct result is 945. Shared C# quality arithmetic now applies
   exact integer percent multipliers and floors once, after multiplication.
2. **Combo eligibility:** standalone Standard Touch does not grant the chained
   Advanced Touch discount. Adaptive tracks valid eligibility rather than just
   last action. Capturing legacy StepState after Standard Touch requires explicit
   confirmed chain history and fails closed if it is unavailable. Legacy
   last-action storage is not silently redefined for other solvers.
3. **Completion breadth:** broad completion probes exhausted their small work
   budgets before reaching a finishing action on high-level recipes. Adaptive
   now limits baseline/suffix probes to at most eight states per layer, leaving
   the main quality-search beam unchanged. Every incumbent still passes complete
   replay validation. No unchecked greedy fallback was added.

The original broad-probe implementation completed only 8 of the initial 16
same-level corpus runs; high-level cases blocked without executing an action.
The fixed-corpus regression now requires completion. This is not proof of
feasibility elsewhere.

## Comparison protocol

Actual-plugin tests run Standard and Adaptive against the same audited ordinary
projection, and replay frozen Rust-generated Raphael macros. Initial quality
is zero; the desired target is full raw quality. This is not a test of HQ or
collectability metadata, which the sparse upstream item table cannot supply.

Each of 12 profiles runs under all-Normal and seeded ordinary transitions.
Good probability is 20% below level 63, 25% otherwise; Excellent is 4%; forced
transitions are preserved. One seed is a regression scenario, not a statistical
estimate of success or HQ chance. Different rotations see conditions on different
actions, so these are not paired identical-condition policies.

Adaptive work limits: 40,000 transitions, 20,000 cumulative stored nodes, main
beam 256, horizon 10, maximum plan length 64, completion beam at most 8. The
wall-clock cutoff is disabled for reproducible coverage. Each plan is replayed
before its first action; its continuation is revalidated next turn.

Standard is stopped/reported when it requests an action outside this reliable
model (e.g. Hasty Touch). This is **not a claim that Standard fails in-game**.
It receives supported combo eligibility through a managed test bridge; no live
native capture is involved.

Raphael reference search is separately bounded to two seconds/profile and can
retain an incumbent. Complete denotes a returned result, not full quality or
real-game optimality. One high-level profile returned no macro within its
deadline; it is unavailable, not scored as a craft failure. Frozen macros are
not adaptively replanned. Solve latency is not included in playback timing.

## Pre-tuning Release baseline (retained for comparison)

Current depth/allocation results: `docs/adaptive-search-tuning.md`. The table
below records the reference-validation patch before that optimization.

| Solver/model | Profiles with results | Completed | Full raw-quality target |
|---|---:|---:|---:|
| Adaptive | 24 | 24 | 18 |
| Standard, reliable-action comparison | 24 | 19 | 18 |
| Frozen Raphael macros | 22 | 22 | 20 |

Standard's five non-completing comparison runs requested an excluded risky
action. Two Raphael runs lack a baseline (one profile, two scenarios). Do not
interpret this small selected corpus as proof that Adaptive outperforms either.

Same-level, all-Normal results (quality / actions; stopped Standard rows are
partial outcomes):

| Recipe ID | Level | Durability | Target | Adaptive | Standard | Raphael |
|---|---:|---:|---:|---:|---:|---:|
| 51 | 18 | 40 | 360 | 360 / 7 | 360 / 7 | 360 / 7 |
| 52 | 18 | 70 | 450 | 450 / 7 | 450 / 9 | 450 / 7 |
| 1207 | 50 | 40 | 1760 | 1760 / 9 | 1760 / 11 | 1760 / 9 |
| 292 | 50 | 80 | 2000 | 2000 / 10 | 2000 / 12 | 2000 / 10 |
| 34890 | 90 | 35 | 7800 | 5600 / 15 | 4563 / 12 (stopped) | 7653 / 21 |
| 5166 | 90 | 80 | 7200 | 7200 / 16 | 7200 / 15 | 7200 / 13 |
| 35907 | 100 | 35 | 12000 | 8018 / 18 | 4626 / 15 (stopped) | 12000 / 24 |
| 5618 | 100 | 80 | 12000 | 7447 / 22 | 6015 / 18 (stopped) | unavailable |

Machine-specific performance observation, not a shipping guarantee:

- 224 Adaptive decisions across 24 crafts.
- Per-craft decision p95 values ranged approximately 11–104 ms. Not aggregate
  p95 and not a test with normal wall-clock cutoff enabled.
- About 2.79 GB allocated over the entire Adaptive comparison, averaging
  12.4 MB/decision including replay/test overhead. Allocation traffic, not peak RAM.
- Runtime/JIT/GC affect timing; tests do not assert elapsed-time limits.

## Remaining gates

Deeper completion fixes a safety/usability gap, not quality-search optimality.
Six runs still miss the desired target even though strong reference macros exist
for several. Allocation traffic is high. Next work should improve depth/value
search and allocations, retaining these regression cases, before broad rollout.

Still required: more recipes/stats, starting-quality planning scenarios, more
seeds, peak-memory profiling, native observed-state parity, confirmed combo
history, stable live-state/goal integration, and shadow mode before selectable
automated execution. Reference parity is not native game verification. No live
automation, new solver mode, or settings are enabled.

## Commands

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test tests/GatherBuddy.Adaptive.Tests/GatherBuddy.Adaptive.Tests.csproj --configuration Release --nologo
"/mnt/c/Program Files/dotnet/dotnet.exe" test tests/GatherBuddy.Compatibility.Tests/GatherBuddy.Compatibility.Tests.csproj --configuration Release --filter FullyQualifiedName~AdaptiveRecipeComparisonTests --logger "console;verbosity=detailed"
```

Full compatibility tests include these benchmarks. Use
`--filter "Category!=Benchmark"` for a faster managed regression pass when not
measuring search; do not omit benchmark gates from release validation.
