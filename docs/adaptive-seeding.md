# Historical Adaptive plan seeding

Implemented for historical shadow analysis only. **Historical results cannot execute actions.**
Separate explicitly armed live trials exist; they do not import Raphael cache seeds
in the first host slice. See [adaptive-live-host.md](adaptive-live-host.md).
No solver selection, saved settings, macro cursor, executor timing, Raphael queue,
or per-turn solve is changed. Export schema 5 introduced seed evidence; current schema
6 additionally carries experimental native acknowledgements, without execution authority.

## Candidate ownership and alignment

- Successful Raphael solve publications and loaded cache rows publish an immutable
  sidecar copy of locally owned actions. Lookup requires the exact existing request,
  key, and current cache-entry reference. It never enumerates the live mutable
  `ActionIds` list, launches a process, enqueues work, saves, or changes cache entries.
  Missing/failed/unsupported/oversized publications remain unavailable.
- A craft captures its candidate once at startup. Strict native recipe/stats/unlock
  context must match. Starting quality is provenance, not a replacement for observed
  quality. Specialist/stochastic/unsupported actions are not silently converted.
- Cursor advances only after a request-correlated, executor-reported-success outcome
  matches all supported native fields and step index on repeated framework frames.
  Attempts, reported success alone, rejected requests, and native step counts do not
  advance it. Non-step Final Appraisal outcomes count as actions.
- Any prefix divergence disables that suffix; there is no guessed realignment.
  Pending roots retain their captured action ordinal even if dispatch occurs later.
- A replay-validated prior Adaptive plan can supply a second suffix only when the
  bounded correlated-action journal proves every intervening action matches. Late
  results that diverge or need unavailable history cannot supply a continuation.
  Session replacement resets candidates; revoked results never become seeds.

At most two candidates, 256 actions each, and 256 journal entries are retained.
Every suffix replays from the actual captured root, using its observed CP, quality,
buffs, condition, and 150%/175% Good setting. Replay checks cancellation/deadline
between actions. Seed replay steps/time are separately reported, included in total
elapsed deadline, and bounded to 512 steps; they are not counted as tree transitions
or stored nodes. Legacy continuation input shares this accounting and two-input cap.
Search still uses at most 40,000 tree transitions, 20,000 stored nodes, and the same
100 ms production deadline. Scheduling/individual-operation overhead can exceed the
deadline; it is not a hard real-time guarantee.

Accepted seeds establish an incumbent. Noncancelled search returns that incumbent
or a better replay-validated plan under the unchanged comparator. Cancellation
returns no plan. If the deadline expires before replay acceptance, no baseline is
invented. One worker plus latest pending root remains unchanged; no simultaneous
seeded/unseeded shadow searches. Results remain historical, noncurrent,
nonexecutable, with `NativeActionIdentityConfirmed = false`.

## Recorded all-NQ comparison

Fixture: recipe 5618, stats 5953/5490/664, full target 12,000, enhanced tool 175% Good.
Seven supplied early roots all have Normal condition. Reference suffixes reconstruct
the user's recorded 17-action existing-solver route: **not a captured Raphael cache
artifact, an independent simulator reference, or native Adaptive outcomes**.

Release sample on this machine, no concurrent verification jobs. Deterministic A/B
disables only time cutoff; all work/node/depth limits remain. Columns show unseeded /
seeded. Timings include JIT/GC noise; these single samples establish no speed claim.

| Step | Quality | Actions | Modeled seconds | Tree transitions (both) | Nodes (both) | Elapsed ms | Allocated bytes |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 2 | 10800 / 12000 | 17 / 16 | 48 / 42 | 39814 | 20000 | 57.694 / 36.982 | 4354920 / 4354688 |
| 3 | 10200 / 12000 | 17 / 15 | 47 / 40 | 39715 | 20000 | 29.497 / 29.328 | 4660344 / 4661512 |
| 4 | 9570 / 12000 | 17 / 14 | 47 / 38 | 40000 | 19823 | 45.052 / 43.061 | 4625624 / 4624144 |
| 5 | 8700 / 12000 | 15 / 13 | 41 / 36 | 40000 | 19895 | 34.251 / 30.837 | 4633032 / 4628416 |
| 6 | 11100 / 12000 | 17 / 12 | 46 / 33 | 40000 | 19426 | 28.157 / 27.753 | 4563152 / 4563752 |
| 7 | 10500 / 12000 | 33 / 11 | 83 / 30 | 40000 | 19439 | 28.923 / 30.424 | 3633568 / 3636488 |
| 8 | 11700 / 12000 | 14 / 10 | 40 / 27 | 40000 | 17344 | 34.437 / 43.701 | 3519848 / 3520728 |

All seven complete in both lanes; full target rises 0/7 → 7/7. Seed replay requires
10–16 actions, 0.007–0.095 ms in this sample. Allocations measure current-thread total
search allocations, not peak memory or native game allocation.

Same run with the production 100 ms limit accepts all seven seeds and preserves
12,000 quality; unseeded qualities and tree/node counts match above. Unseeded/seeded
elapsed ms by step: 41.401/46.942, 42.468/42.245, 44.548/41.470, 35.448/18.867,
15.150/16.172, 12.792/14.983, 12.085/12.611. Timing assertions are deliberately absent.
Raw sample: `/tmp/opencode/adaptive-seed-comparison.log` (local, not committed).
No general heuristic/dominance tuning was included.

## Supplied schema-5 native acceptance evidence

The subsequent user-supplied all-NQ trace demonstrates a real cache hit for
`5618/100/5953/5490/664/1/0/0`, tool 51785, and the same recorded 17-action route.
All 16 captured roots accept the aligned cached suffix and retain independently
replayed full-quality completion plans. Eleven results tie the best accepted seed;
five improve it under the existing comparator. Improvements conserve resources at
the same modeled duration/action count; they are not extra quality above the cap.
The final result ties an Adaptive Careful Synthesis continuation that is already
better than Raphael's Delicate Synthesis suffix.

- 16 correlated outcomes; 16 submitted/dispatched/observed/retained/validated/target
  results. No mismatches, blocking, skipped roots, cancellations, or revoked results.
- Adaptive continuations align at 11 roots. Four later roots reject alignment after
  the existing executor takes actions different from the improved historical plan;
  the first root has no prior continuation. This is expected prefix safety.
- Substantive search elapsed 31.7556–74.8765 ms under unchanged work/node/deadline
  bounds. First seed replay costs 0.8231 ms; subsequent batches 0.0172–0.0663 ms.
  These are reported search times, not measured executor delay or worker start times.
- Native Good Delicate Synthesis gives **0 → 525** (ordinary control: 450).
  Native Good Preparatory Touch gives **2685 → 5047**, gain 2362 (ordinary control:
  final quality 4710). All observed supported fields correlate without mismatch.
  Excellent→Poor→Normal is also present. This run is not all-Normal.
- Last stable native frame reaches quality 12000 but progress remains 6209/6600.
  Final Delicate Synthesis is requested/reported, then stable terminal evidence is
  lost before addon teardown. `TerminalObservation = Unobserved` remains correct.
- Event ring retains 64/200 events (136 dropped); all 16 historical results remain.

Previous unseeded native trace had 9/16 full-quality plans versus 16/16 here.
Conditions/current quality differ, so this is **not controlled native A/B**, nor
16 independently completed Adaptive crafts. Existing solver executed every action.
Cache-missing, divergence, pause/revoke, other recipes, action-ID confirmation,
and terminal capture still require separate native acceptance.

Frozen evidence: `tests/GatherBuddy.Adaptive.Tests/Fixtures/native-schema5-seeded-all-nq.json`
is an explicitly labelled **compact projection**, not the complete original export.
It retains recipe/tool/provenance, all 16 native roots, cached actions, changed
retained routes, alignment/comparison outcomes, summary counts, and reported timing
ranges; session UUID and repeated event/result envelopes are omitted. Tests replay
all cached suffixes and retained routes, verify allocating/scratch Good arithmetic
against ordinary controls, and feed recorded actions/results through the real shadow
session/runner to check alignment, bounded export, and unobserved terminal semantics.
The recorded-result test scheduler replays frozen route actions; it does not reproduce
optimizer work or timing. No runtime production changes or execution enablement.

## Verification and remaining acceptance

334 Adaptive Debug/Release tests, 184 actual-plugin compatibility tests, and 566
acquisition tests pass: 1084 distinct tests. Coverage includes detached cache storage,
missing/failed entries, exact request/current owner, compiled no-execute/no-solve
boundaries, two-candidate accounting, stopped replay, rejected/non-step correlation,
late result/pending-root alignment, divergence, and cross-session revocation.
Forced Debug/Release solution builds pass with only the two existing nullable
warnings in unchanged ElliCon/ElliLib. Whitespace checks pass. No commit/push.

Next native acceptance: repeat with a missing cache candidate, diverging existing
solver route, pause/resume, and cancellation/new craft. Missing cache must stay
explicit and must not trigger Raphael work from shadow lookup. Verify alignment
rejection, fresh observations after resume, revoked-result isolation, and no
`BelowSeed` after noncancelled accepted replay. Then expand recipes/starting quality
and ordinary-tool controls. Preserve ended-session evidence with every trace.

Native action IDs remain unconfirmed; terminal addon disappearance can leave final
outcome unobserved. Pinned Rust parity covers ordinary/unaffected arithmetic, not
enhanced 175% Good. The original all-Normal offline fixtures do not exercise that
bonus, but the subsequent native trace above does. Single-run schema-5 cache-hit/Good
evidence is accepted within that scope; broader native acceptance, terminal/action-ID
confirmation, and any execution enablement remain pending.
Experimental native callback capture is now available for validating that prerequisite:
[`adaptive-native-confirmation.md`](adaptive-native-confirmation.md).
