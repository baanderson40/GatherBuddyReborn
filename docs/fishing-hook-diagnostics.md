# Fishing-hook allocation diagnostics

## Reported failure

On 2026-11-04, dev reload at 12:43:36 and manual enable at 12:57:18 failed
constructing the AgentCatch.UpdateCatch hook in FishingParser. The exception came
from Reloaded's `CreateJump -> CreateMemoryBuffer`: 32 bytes requested in a 64KB
allocation inside a constrained address interval. A retry succeeded earlier and
the user confirmed successful loading after a game restart.

This establishes a native hook-backend allocation failure, not ordinary RAM
exhaustion, fish-record corruption, a bell-navigation failure, or a proven leaking
plugin. Root cause remains unresolved. The available post-restart log reports
Dalamud 15.0.3.9. Its matching source disables Reloaded hooks during disposal but
does not reclaim trampoline buffers in that disposal path. Reload pressure is a
hypothesis; shared-target hooks alone are not evidence of conflict.

## Patch behavior

- `[Fishing hooks][attempt-id]` logs creation stage, raw game target address,
  containing native module/offset, PID/thread, and versions/informational builds
  of currently loaded Dalamud/Reloaded assemblies. Successful creation logs the
  actual backend name. These are creation/failure logs, not per-frame traces.
- Module enumeration is best-effort. Diagnostics cannot prevent hook creation or
  turn a successfully returned hook into an unowned allocation if logging fails.
- Failures preserve the full original exception and point to Dalamud's preceding
  `Could not set up a hook at` address-space report. Raw supplied target is not
  claimed to be the final jump-resolved address or managed detour address.
- Two-hook construction rolls back the first returned hook if the second factory
  fails. There is no caller-owned hook to dispose if the backend throws before
  returning one. This patch cannot reclaim unknown partial backend allocations.
- Enable failure attempts to remove subscriptions and disable both hooks, then
  rethrows the original error. Repeated successful Enable calls do not duplicate
  chat/framework subscriptions. Disposal is idempotent and independent cleanup
  attempts continue when another cleanup action throws a managed exception.
- Recorder construction releases a completed parser if later setup fails, and
  disposes its cancellation source if parser/setup fails. Rollback does not add
  fish-record saves. Normal disposal still attempts existing record saves even if
  earlier cleanup fails. No saved records are deleted or migrated.
- No retry loop, backend selection change, process-memory patch, global hook
  override, swallowed primary initialization failure, or disabled fishing fallback.
  AccessViolationException/SEHException are not treated as recoverable failures.

Correction to earlier investigation: `FishRecorder.LoadRemoteFile()` reads
embedded JSON synchronously. It does **not** start a remote request before parser
construction. Uploads are initiated from the later enabled framework update path.

## Verification / next capture

535 acquisition/source tests and 111 actual-plugin compatibility tests pass
(646 total). Pure tests cover single-attempt factories, constructor rollback,
cleanup/logging failures preserving the primary exception, and independent cleanup.
Actual-plugin IL checks cover rollback wiring, enable/disposal guards and loaded
assembly diagnostics. Native allocation, hook activation and actual event
subscriptions are not executed by these tests.
Forced Debug/Release builds and `git diff --check` pass; full builds retain only
the two existing ElliCon/ElliLib nullable warnings.

Controlled acceptance remains required:

1. Fresh game load, normal fishing capture, unload and re-enable.
2. Confirm one creation pair per attempt and no duplicate recording/subscriptions.
3. If allocation fails again, retain both `[Fishing hooks]` entries and the complete
   preceding Dalamud address-space report, not only PluginInstaller's wrapper stack.
4. Compare first load versus reload behavior and exact loaded backend versions
   before considering a Dalamud/backend fix. Do not repeatedly reload to force a
   failure or blindly switch to MinHook (unsupported by the inspected version).

The patch improves diagnosis and failure cleanup; it is not a verified fix for
the underlying allocator failure.
