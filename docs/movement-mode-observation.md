# Movement-mode observation

## Trace

The repeated `Legacy mode is now ...` messages originated in
`AutoGather/Helpers/OverrideMovement.cs`, used by AdvancedUnstuck. Its handler
previously reread `MoveMode` on every `UiControlChanged` event and logged at Info
even when the cached value was unchanged. A failed read also assigned false,
making an unknown value look like Standard movement.

At 2026-11-04 13:35:43.352, the full log contains seven disabled and eight enabled
observations for each of GBR, vnavmesh, visland and BossModReborn, interleaved in
the same order during Lifestream login. Comparable bursts occur before and after
GBR reloads; no growing GBR-only subscription multiplier is established.
The disposal chain AutoGather -> AdvancedUnstuck -> OverrideMovement explicitly
removes the original subscription. The evidence supports shared config-event
bursts rather than a proven GBR subscription leak.

Dalamud's config hook forwards recognized config-entry notifications without
movement-specific or value-change filtering. Its UiControl section dynamically
selects keyboard/gamepad configuration using UiConfig.PadMode. The old logs lack
event names/raw read success and cannot establish whether the disabled/enabled
observations were transient loaded values, bank changes, failed reads or actual
MoveMode changes. They do not establish repeated GBR setting writes: this helper
reads mode and does not set it.

## Patch

- Handle only UiControl.MoveMode and UiConfig.PadMode config changes. Fall back to
  the option enum's name when ConfigChangeEvent.Name is absent/empty.
- Refresh on construction, login and before enabling override movement so a
  previous unknown value or another character's config is not assumed current.
- Keep the last successfully observed mode on failed reads. Before the first
  successful observation, the existing default remains false but is not logged
  as a successful Standard-mode reading.
- Log initial successful observation and subsequent actual cached-state changes
  at Debug with `[Movement mode]`, source and initial flag. Unchanged observations
  produce no message; no debounce delays real transitions.
- Dispose removes both config subscriptions and the login subscription; late
  callbacks cannot refresh a disposed observer. Both hook disposals are attempted.
- No writes to movement preference, no changes to temporary mode overrides or
  restoration in other plugins, and no global logging/filter settings changed.
  Other plugins may continue emitting their own repeated messages.

## Verification

546 acquisition/source tests plus 116 actual-plugin compatibility tests pass
(662 total). Tests cover first observation, genuine transitions, failed reads,
irrelevant events avoiding reads, active-bank refresh, disposed callbacks, actual
adapter subscriptions/unsubscriptions, pre-enable refresh and Debug-only logging.
Pure tests do not execute native hooks or simulate the game's configuration load.
Forced Debug/Release builds and `git diff --check` pass; only the two existing
ElliCon/ElliLib nullable warnings remain.

Native logging acceptance (2026-11-04): four build-triggered plugin reloads at
14:01-14:02 each emitted one initial observation. User-confirmed relog then emitted
one disabled observation at 14:06:19.404 and one enabled observation at
14:06:34.756, both initial=False/source=MoveMode. User confirmed no further spam.
This validates logging for that relog, not every movement or lifecycle scenario.

Remaining native acceptance: change MoveMode in-game, change PadMode with differing
keyboard/gamepad settings, exercise AdvancedUnstuck and AR return. Verify unchanged
input preferences, correct movement direction and subscription cleanup.
