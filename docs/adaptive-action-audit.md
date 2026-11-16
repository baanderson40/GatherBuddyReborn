# Confirmed crafting action corrections

2026-11-16. Follow-up to [Trained Eye correction](adaptive-trained-eye.md).
User verified the following in-game after comparison with current action descriptions:

- Refined Touch can be used standalone: one normal Inner Quiet stack. After
  Basic Touch it grants two stacks: Basic1 + Refined2 =3 total from zero, capped10.
- Quick Innovation does not increase step count.
- Hasty Touch's Expedience effect requires level96 or higher.

## Implementation

All corrections live in shared `Vulcan.Simulator.cs`, not a duplicate Adaptive model.

1. Remove Refined Touch's combo-only execution restriction. Its extra IQ stack
   now requires preceding Basic Touch; base quality/24CP/10durability/level92
   requirements remain unchanged. Adaptive public/scratch projections and full
   seed replay can accept standalone Refined Touch.
   Generated search omits standalone Refined when Basic Touch dominates it: same
   potency/durability/IQ/duration,6 less CP, lower lexical ID, and strictly richer
   combo eligibility. Combo Refined remains searchable. This filter applies only
   to generated branches, never simulator legality, observations or seed replay.
   Attempts still count against the shared transition budget. Property tests cover
   conditions, IQ caps, combos and durability-cost buffs; inefficient legal seeds
   remain replayable without rewriting. Initial full-suite tests exposed a real
   quality-search regression from admitting redundant branches; this proof filter
   preserves existing floors instead of reducing regression requirements.
2. Classify Quick Innovation as non-step. Preserve current condition, existing
   buff timers and Expedience; do not trigger a Manipulation repair tick. Grant
   Innovation1 and consume its single charge. Existing unlock/charge/Innovation-
   active restrictions remain. First-step actions stay eligible afterward.
   Other non-step action behavior is not redefined by this patch.
3. Gate successful Hasty Touch's Expedience grant at level96. Success/failure
   behavior remains otherwise unchanged: this patch does not claim to resolve
   whether a failed Hasty Touch should grant Expedience. That needs separate
   evidence. Daring Touch's existing unlock/Expedience requirements remain.

Quick Innovation and Hasty/Daring Touch remain excluded from Adaptive's supported
action allowlist. Their corrections affect shared/legacy simulation, not new
specialist/probabilistic Adaptive execution. No executor, native packet matching,
goal comparator, settings/defaults, action delay, or retry behavior is changed.

## References and frozen-Rust limitation

- [Current CUL actions](https://ffxiv.consolegameswiki.com/wiki/List_of_culinarian_actions)
- [Official actions/traits](https://na.finalfantasyxiv.com/crafting_gathering_guide/culinarian/)
- [Refined Touch](https://ffxiv.consolegameswiki.com/wiki/Refined_Touch_(culinarian))
- [Quick Innovation](https://ffxiv.consolegameswiki.com/wiki/Quick_Innovation)
- [Hasty Touch](https://ffxiv.consolegameswiki.com/wiki/Hasty_Touch)

The wiki category includes removed actions; current lists and trait thresholds
must be checked separately. Shared crafter mechanics do not imply identical native
IDs: `SkillActionMap` resolves job-specific IDs from game sheets. This audit does
not independently certify every native binding, specialist/mission mechanic or
every buff edge case.

Pinned Raphael commit `5dc5c4efd756391225617b901b3ec2d7c0fabd9e` has a
combo-only `RefinedTouch::precondition`. Frozen export contains360 otherwise-eligible
standalone rows marked illegal, with no After state. Raw JSONL/Rust checkout remain
unchanged. `ReferenceCorpus.IsStandaloneRefinedTouchErratum` explicitly identifies
only those rows using action, level, resources, nonterminal state and missing combo.
Reference comparisons expect corrected legality; no invented Rust After state.
Dedicated bounded spec tests check all360 for CP, durability/repair, IQ, progress
and quality (independent decimal arithmetic). The72 legal combo rows still compare
against raw Rust normally. Other raw legality remains required. Existing Trained
Eye32-row durability erratum remains separate and explicit.

This is user/native-backed specification coverage, not independent Rust parity
for standalone Refined Touch. A future corrected reference pin must revisit both
errata rather than silently replacing raw evidence. No corpus completion/target
floor is lowered to accommodate these changes.

## Verification

`ConfirmedCraftingActionRulesTests` covers standalone/combo variants and IQ cap,
unlock/resources, Adaptive public/scratch parity, four conditions for Quick
Innovation, no timer/repair tick, one-use charge and first-step preservation,
Hasty level9/95/96/100, and all360 reference legality errata. Actual-plugin
compatibility tests cover the three corrected rules without native game calls.

Logs use `/tmp/opencode/adaptive-action-rules-` with suffixes `stage1-tests.log`,
`stage1-build.log`, `debug-tests.log`, `release-tests.log`, `compat-tests.log`,
`acquisition-tests.log`, `debug-build.log`, and `release-build.log`.
No coding-agent in-game actions, commit or push.

Final verification:603 Adaptive tests passed in each of Debug and Release,
282 compatibility and642 acquisition tests passed (1527 distinct). Test logs
have no warnings. Forced sequential Debug/Release solution builds passed with
zero errors and only the two existing ElliCon/ElliLib nullable warnings. Tracked
whitespace and all105 untracked text-file checks passed; legacy action executor
unchanged. Existing quality/completion floors and captured weak-bound transition
counter reproduction remain intact after the search-only dominance filter.
