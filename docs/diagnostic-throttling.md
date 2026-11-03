# Diagnostic throttling

This patch changes logging only. Stat calculations, navigation decisions, purchases,
solve failure results, and queue scheduling retain their existing behavior.

## Windows and keys

- Gearset cap diagnostics: first occurrence, then once per 30 seconds per
  item/stat/slot/failure category. Maximum 512 keys; reset at plugin initialization.
- Vendor navigation: independent 5-second windows per live object and diagnostic
  category, including staging, final approach, close approach, and projection source.
  Selection/fallback changes emit immediately; coordinate jitter does not reset the
  window. Maximum 64 keys; cleared when navigation stops before a new run.
- Missing Raphael CLI: one error per unavailable path/state, case-insensitive and
  thread-safe. Maximum eight keys. Observing a resolved directory or existing file
  rearms its diagnostic. Existing per-request failures are still cached. Process
  failures and timeouts remain unsuppressed; queue preflight is not implemented.

The shared helper uses monotonic timestamps, bounded least-recently-used storage,
and saturating suppression counts. Window expiry or state changes include the
number of suppressed earlier repeats. Forget/reset/eviction discard pending counts.
Navigation projection details are stored as value records and formatted only after
admission, rather than building coordinate strings for every rejected candidate.

## Stat investigation

XIVAPI Item rows 10339 and 10343 identify Soul of the Armorer and Soul of the
Alchemist: slot category 17 (SoulCrystal), no materia slots, fixed base bonuses
20 craftsmanship / 20 control / 15 CP. The existing missing-cap fallback returns
base plus meld values, preserving these bonuses. It is unchanged. This does not
establish that all missing-cap data for other equipment is harmless; diagnostics
remain visible.

## Verification

`tests/GatherBuddy.Acquisition.Tests/DiagnosticThrottleTests.cs` links the production
helper and covers first emission, counted repeats, exact expiry, independent and
alternating keys, state changes, LRU bounds, reset/recovery, backward timestamps,
invalid bounds, and concurrent case-insensitive missing-path reporting.

Native integration is not exercised by these tests. After reloading the plugin:

1. Refresh a large crafting list; check cap messages are bounded and stats match.
2. Revisit the kobold vendor; check staging/final diagnostics no longer alternate
   every frame, useful changes remain visible, and navigation/purchases match.
3. In an isolated test installation, check concurrent missing-CLI requests produce
   one dependency error while retaining every request's failure. Restore the CLI,
   observe availability through a fresh solve, then verify a later absence logs
   again. Do not remove the working installation to perform this test.

Full acquisition-to-crafting completion and housing navigation remain separate
live-verification gaps.
