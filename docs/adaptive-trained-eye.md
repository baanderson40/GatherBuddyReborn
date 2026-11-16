# Trained Eye durability correction

2026-11-16. Supplied Adaptive failure on CUL recipe5282 (Dark Rye Flour) exposes
a shared simulator defect, not evidence that AutoRetainer caused the mismatch.

## Native evidence

- Decision session `ecb7dbab-e2b8-4749-8731-3960367c9e1f`; confirmation session
  `6e43e3dc-384c-4ca9-a623-5336d83245fa`.
- Actual initial frame: step1/progress0/quality0/durability40/CP664/Normal.
- Validated prepared route: Trained Eye, Groundwork. Generation was requested;
  candidate retrieved from shared Raphael cache and replayed against actual root.
- First requested canonical action100283 binds to CUL native action100290.
- Native stream1/sequence4/op9 reports step2/progress0/quality5200/durability40,
  Normal,flags19. Model predicted durability30; this was the only reported scalar
  mismatch. Host correctly stopped with ProtocolFailure and did not send Groundwork.
- No post-action CP/status/full frame or terminal outcome was supplied. The fixture
  does not invent those observations or claim a successful post-fix native craft.
- The later AR handoff log occurs about40 seconds after craft startup; the protocol
  failure occurs about2.3 seconds into Adaptive's session. The supplied evidence
  does not establish an AR cause. Handoff behavior of a stalled craft is separate
  from this arithmetic correction and was not changed here.

## Correction

Trained Eye fills remaining quality, costs250 CP, and costs **zero durability**.
Both `Simulator.GetDurabilityCost` and its `HasDurabilityCost` classification now
exclude Trained Eye from the10-durability group. Zero-cost actions must not consume
Trained Perfection. Shared simulator callers (including existing solvers/estimates)
receive the same corrected arithmetic; Adaptive does not add its own formula.

Native acknowledgement comparisons remain exact. The captured40-durability packet
now matches; a synthetic packet reporting the former incorrect30 still fails.
Even a matching acknowledgement cannot advance the root without independent stable
CP/status samples. Tests keep last actual frame separate from modeled CP414 and
modeled two-action completion durability20. No retry, packet whitelist, guessed
observation, settings change or AR workaround was introduced.

## Pinned Raphael erratum

This section records the Trained Eye correction's original scope. A subsequent
separate standalone Refined Touch legality erratum is documented in
`adaptive-action-audit.md`; raw evidence remains preserved for both.

Pinned Raphael v0.29.2 commit `5dc5c4efd756391225617b901b3ec2d7c0fabd9e` also
assigns Trained Eye10 durability (`raphael-sim/src/actions.rs`, TrainedEye's
`base_durability_cost`). Its frozen22504-row export includes32 legal Trained Eye
rows with that incorrect cost. Existing audit tests copied that expectation.

The raw JSONL and pinned Rust checkout remain unchanged. Reference comparison
explicitly substitutes **only Trained Eye's expected durability** with before-state
durability; every other field/action remains compared as before. A separate test
locks the known32-row scope, asserts the raw10-cost discrepancy and zero-cost C#
result, and checks absence of repair/cost modifiers in those rows. This is an
audited native-backed exception, **not independent Rust parity for that field**.
Future regeneration against a corrected pin must revisit/remove the erratum rather
than silently accepting changed raw data. States exported after upstream Trained
Eye remain valid synthetic input states, not claims about native action history.

## Regressions and verification

- Corrected existing audit to assert full quality/250 CP/unchanged durability.
- Added conditions, low durability, Waste Not and synthetic Trained Perfection
  coverage, ensuring both cost and consumption classification are corrected.
- Frozen supplied packet/root and replay regressions cover seed floor, accepted
  acknowledgement without fabricated full frame, and strict rejection of wrong
  durability.
- Actual-plugin compatibility test exercises the corrected shared simulator.
- Existing independent corpus, fixed/holdout completion floors, native lifecycle,
  leases and legacy solvers remain required; no regression floor is lowered.

Logs: `/tmp/opencode/adaptive-trained-eye-{stage1-tests,stage1-build,debug-tests,release-tests,compat-tests,acquisition-tests,debug-build,release-build}.log`.
No in-game action, commit or push performed by the coding agent. Reload plugin and
start a fresh craft to validate the fix; do not replay/retry the stopped transaction.

Final verification:583 Adaptive tests passed in each of Debug and Release,
278 compatibility and642 acquisition tests passed (1503 distinct). Final test logs
have no warnings. Forced sequential Debug/Release solution builds succeeded with
zero errors and only the two existing ElliCon/ElliLib nullable warnings. Tracked
whitespace and all103 untracked text-file checks passed. Legacy action executor
is unchanged. Initial failures exposed two more tests inheriting the same incorrect
cost assumption; those expectations now use the documented zero-cost rule/erratum,
not relaxed mismatch checks or lower corpus completion floors.
