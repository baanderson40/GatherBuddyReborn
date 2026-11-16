# Independent Raphael reference corpus

`raphael-reference.jsonl` was exported by executing the pinned Rust simulator,
data crate, and solver—not by running GatherBuddy's C# simulator. Upstream:
https://github.com/KonaeAkira/raphael-rs.git, commit
`5dc5c4efd756391225617b901b3ec2d7c0fabd9e` (v0.29.2). Upstream Apache-2.0 license
is retained as `LICENSE.raphael`.

## Contents

- Header: source pin, schema, state columns, projection limits, macro solve limit.
- 12 recipe/stat profiles: eight distinct ordinary recipes at recipe levels
  18/50/90/100, low/high durability, plus level-100 overgear profiles for four
  lower-level recipes. Stats are explicit test loadouts, not captured player gear.
- 22,504 action trials from initial, partial-starting-quality, replayed combo/
  buff/repair states, and deliberate resource boundaries under four conditions.
- 29 reliable ordinary actions shared with Raphael; Final Appraisal is absent
  from Raphael and is tested separately in C#.
- 11 frozen macros; one level-100 high-durability solve had no incumbent within
  its two-second reference-search deadline and is explicitly marked unavailable.

These are real pinned recipe constants, not an exhaustive/current live-game
recipe database. Recipe IDs, item IDs, level-table rows, stats, base increases,
and raw-quality targets are preserved. Raphael's sparse item table does not
provide full HQ/collectability metadata; tests do not invent it. Targets are raw
quality, not HQ chance or live collectability requirements.

## Comparison projection

State arrays follow the header's 17 columns. Quality is clamped to recipe max;
durability is saturated at zero for comparison. Observe maps to Standard Touch's
Advanced Touch discount state. Other last-action history has no counterpart in
Raphael. Terminal trials compare progress, quality, CP, durability, and terminal
classification only: Rust returns before updating terminal buffs/history.

Current-condition action arithmetic comes from Rust. Ordinary forced next
conditions are supplied by the exporter because `raphael-sim` accepts condition
as an action input rather than generating a next condition. No expert, mission,
specialist, adversarial, probabilistic-action, or Final Appraisal parity is claimed.

### Trained Eye erratum

Standalone Refined Touch has a separate user-verified legality erratum:360 raw
otherwise-eligible rows are incorrectly illegal in pinned Rust (no After state).
`ReferenceCorpus.IsStandaloneRefinedTouchErratum` makes corrected legality explicit;
bounded spec tests check all360 results without inventing independent Rust output.
The72 legal combo rows still compare normally. See `docs/adaptive-action-audit.md`.

Pinned Rust also contains the incorrect10-durability Trained Eye cost exposed by
supplied native recipe5282 evidence. Raw JSONL remains unchanged (32 legal affected
rows). `AdaptiveReferenceTests` explicitly corrects only expected Trained Eye
durability and separately asserts the raw discrepancy; this field is not claimed
as independent Rust parity. See `docs/adaptive-trained-eye.md`.

`live-schema4-trained-eye-durability-mismatch.json` retains the supplied initial
frame, first native packet, prepared route and original erroneous expectation.
Native Trained Eye reports durability40, not30. No later CP/status/full frame or
terminal was supplied; modeled CP414 and plan completion must not become observed
evidence. The original failure is preserved, not relabelled as post-fix live success.

## Regeneration

Requires the existing clean pinned Windows Raphael checkout from
`tools/Setup-Raphael.ps1`, Windows Rust/MSVC, and this tool's checked-in Cargo.lock.
Run explicitly from repository root in Windows PowerShell:

```powershell
powershell -NoProfile -File tools/Export-AdaptiveReference.ps1
```

From WSL:

```bash
"/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe" -NoProfile -File tools/Export-AdaptiveReference.ps1
```

The script verifies the dependency pin, origin, and Windows-Git-clean source,
runs Cargo with `--locked` and the pinned Windows toolchain/target, and publishes
only a successful export. It never edits/checks out the dependency or changes
plugin build targets. Binaries/build caches stay under ignored `.dependencies`.
No Rust execution/download occurs in normal tests or plugin builds.

Mathematical transition rows are deterministic for a fixed source/generator;
time-bounded macro search can change incumbents/status with timing or worker
scheduling. Review regenerated macros and benchmark results before accepting
changes. Do not regenerate expected data from the C# implementation to hide a
parity failure. `AdaptiveReferenceTests` detects pin/schema drift.

The standalone exporter lockfile can be deliberately refreshed with Windows
Cargo's `generate-lockfile --manifest-path tools/adaptive-reference/Cargo.toml`;
review that diff separately. Normal exports use the existing lock.

## Scoped live schema-4 search regression

`live-schema4-root-history-success.json` is a compact projection of the supplied
live export, not a Rust oracle or a complete native callback log. It retains all
16 input frames, explicit history-derived combo/remaining-use fields, published
action lists, exact-root continuation assessments, transaction linkage, engine
work counters, and host timing columns. Parallel arrays use the same root order;
transaction and timing column names are embedded in the fixture.

The source reports 16 matched acknowledgements, 15 full-frame correlations, full
quality/progress, and managed end with zero root-history loss. Its separate
confirmation ring evicted 99/163 events. Earlier callback packets are omitted,
not reconstructed. Tests validate all proposals and 15 observed non-condition
transitions, and check captured transaction metadata; they do not replay omitted
native acknowledgements or manufacture a final full frame. Last frame remains
step16/progress5118/CP80; terminal packet separately reports 6600/12000/0 durability.

Source plans remain nonexecutable diagnostic proposals. Fresh optimizer replay
uses the exact captured roots and independently replayed prior-plan suffixes,
with the wall-clock cutoff disabled but original work/node/depth limits retained.
These offline results are not new native execution, a controlled comparison with
other live runs, a native-layout acceptance claim, or proof of global optimality.

## Fresh three-way macro comparison

`raphael-three-way-macros.json` freezes fresh single-thread Windows CLI solves
for the three same-level recipes still short of target under unseeded Adaptive.
All three finished normally within a300-second external timeout; Tacos now has
a full-quality macro after an18.2-second solve. The original two-second reference
corpus is deliberately not regenerated or replaced.

Exact CLI arguments, upstream pin, binary hash, output action IDs, modeled time,
and solve wall time are preserved. `FinalQuality` retains raw uncapped CLI output;
tests clamp to the actual recipe maximum. Tests compare unseeded per-action
Adaptive, unchanged fresh macro, and initial-macro-seeded per-action Adaptive,
all from the same Normal/zero-quality root. No specialist/food/potion/backload
effects, live cache access, native capture, or game execution are involved.
The configured production work limits remain; wall-clock cutoff is disabled.

See `docs/adaptive-three-way-comparison.md` for results, timing caveats,
reproduction, and explicit separation from live startup wiring. Routine tests
consume frozen data only and never invoke Raphael.

## First seeded live cache-hit trial

`live-schema4-seeded-cache-success.json` preserves all17 supplied input roots and
published plans, explicit history-derived combo/perfection availability, seed
assessments, transaction linkage, and host timings. Initial RaphaelCache macro has
17 actions/45 modeled seconds and independently replays to12000 quality. Later
plans are exact suffixes, all source comparisons Tied. `GenerationRequested=false`
means cache hit, not generated-seed acceptance.

Source reports17 acknowledgements/16 full-frame correlations and managed terminal
success. Raw earlier callback packets are omitted (confirmation ring evicted109/173),
not reconstructed. Full-root records have no drops. Tests replay captured plans and
non-condition transitions; they do not regenerate omitted acknowledgement evidence.
Good Delicate Synthesis yields observed525 quality. Last stable frame remains
progress6209/quality11775/CP86; terminal packet separately reports6600/12000/5
durability. No final CP/status/full frame is supplied or invented.

`docs/adaptive-seeded-search-budget.md` records baseline/bounds/allowance comparisons
and why smaller seeded allowances remain disabled in the live default.
## Collectable reward metadata

`collectable-tacos.json` freezes external recipe35829/item44232 reward data,
including source URLs/schema/version: ratings660/900/1140 map to
quality6600/9000/11400 with maximum12000. Runtime reads local sheets instead.
Fixture is game-data evidence, not a native action/terminal trace. See
`docs/adaptive-collectables.md` for tier goals and pending native acceptance.
