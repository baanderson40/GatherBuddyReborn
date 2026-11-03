# Warning remediation

Maintenance work after `62085c3`; not another acquisition phase. No warning codes
were suppressed. Existing MSB3277 demotion was not expanded. Diagnostic-level
ResolveReferences inspection found no MSB3277 or MessagePack/NAudio/Tensors conflict
diagnostics (routine equal-version framework resolution messages remain).

## Changes

- MessagePack 3.1.3 → 3.1.12, with matching annotations/analyzers and lock file.
  V2 decoding uses explicit per-call `UntrustedData` options, rejects null records
  and trailing data, and preserves decode exception details. No global serializer
  settings, version bytes, numeric keys, private fields, or V1 decoder changed.
- CSCore replaced by NAudio.Core/NAudio.Wasapi 3.3.0. Honk retains mono WAV playback,
  per-stream volume, repeat count and 200 ms gaps. Volume is clamped; no Windows
  master/session volume changes. Playback faults are observed, waits bounded,
  unload cancels owned playback. No endpoint skips optional sound with a 30-second
  diagnostic window; later requests rediscover devices rather than caching failure.
- Fixed nullable contracts/guards without inventing inventory or successful work.
  AllaganTools delegates must be bound by EzIPC; optional service availability still
  follows existing readiness checks. Missing craft state no longer reaches initial
  step construction.
- AutoHook intuition swaps get enabled configuration even without a bait override;
  missing bait does not enable ForceBaitSwap. Optional mooch remains nullable and
  omitted by existing export settings, preserving the prior valid payload behavior.
- Standard Solver now excludes **both** repair actions from the CP-driven Byregot
  override. Undefined condition values throw explicitly; deliberate `Unknown`
  behavior and known mappings remain unchanged.
- Migrated obsolete string, movement, tree-list and object-ID APIs; removed unused
  code and corrected the xUnit collection assertion.
- Both CustomTalk lookups share ordered cached raw-sheet existence checks, returning
  the same typed RowRef/untyped fallback and language. No random runtime type hash,
  arbitrary constant, obsolete overload, or CA1857 suppression. Raw sheet checks
  avoid allocating typed wrappers per script; native menu/route verification pending.

## Automated evidence

Fresh baseline full Debug compilation found **64 warnings**, not the 62 from older
incremental logs: 62 in the main project plus two in submodules. After remediation,
full Debug/Release solution compilations have **two warnings**, both unchanged
submodule sites. Main project has zero warnings; no known vulnerable direct or
transitive packages were reported by NuGet audit against current configured sources.
This is not a guarantee against undisclosed vulnerabilities.

Verification: **271 acquisition tests + 28 compatibility tests = 299 passed**.
No tests skipped. `git diff --check` passes. Actual device/host checks below remain
pending, not inferred from managed tests.

Tests are separate projects, run explicitly:

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test tests/GatherBuddy.Acquisition.Tests/GatherBuddy.Acquisition.Tests.csproj --configuration Debug --nologo
"/mnt/c/Program Files/dotnet/dotnet.exe" test tests/GatherBuddy.Compatibility.Tests/GatherBuddy.Compatibility.Tests.csproj --configuration Debug --nologo
"/mnt/c/Program Files/dotnet/dotnet.exe" build GatherBuddy.sln --configuration Debug -p:Platform=x64 --no-incremental --nologo
"/mnt/c/Program Files/dotnet/dotnet.exe" build GatherBuddy.sln --configuration Release -p:Platform=x64 --no-incremental --nologo
"/mnt/c/Program Files/dotnet/dotnet.exe" list GatherBuddy/GatherBuddy.csproj package --vulnerable --include-transitive
```

Compatibility tests reference the actual plugin/model/generated formatter, not a
copied DTO. The immutable Base64 V2 fixture was captured with MessagePack 3.1.3
before changing dependencies. New bytes match it exactly, establishing backward
compatibility for that fixture; all persisted instance fields are compared. Tests
also cover multiple/empty records, malformed/null/truncated/trailing payloads,
solver repair postprocessing, condition mappings, AutoHook configuration and actual
NAudio WAV downmix/volume math without opening a device.

Patched MessagePack safely skips deeply nested **unknown** fields iteratively; it
does not reject that case merely because nesting exceeds object-graph depth. The
test checks preserved record contents after bounded synthetic unknown input, not
an invented depth rejection. Base64's preceding shared gzip decompressor remains
unbounded: this update is not a complete imported-data resource-budget solution.
No arbitrary limits were added to existing large fish databases.

Contained Raphael stage/Debug/Release CLI, provenance, and license hashes match.
Submodule revisions and sources remain untouched.

## Residual warnings

| Owner/file | Warning | Follow-up |
| --- | --- | --- |
| ElliCon, `Core/InputBlocker.cs:74` | CS8602, nullable dereference | Separate submodule patch/revision decision; validate absent input context before dereference. |
| ElliLib, `Filesystem/Selector/FileSystemSelector.cs:160` | CS8618, uninitialized `PathDropped` event | Separate submodule patch/revision decision; nullable event contract and invocation checks. |

## Native checks still pending

- Plugin load/assembly binding under Dalamud with updated dependencies.
- Real fish-file load/save and import/export/merge; V1 records depend on game-data
  validation and were not exercised by the managed compatibility suite. Existing V1
  reader remains unchanged. Back up real records before controlled live testing.
- Audio endpoint playback at 0/mid/100 volume, repeats, unplug/no device/reconnect,
  overlapping requests and unload. Unit sample math is not endpoint validation.
- AutoHook intuition transitions with/without bait, IPC bindings with AllaganTools
  missing/unready/ready, retainer inventory query safety and plugin toggling.
- Collectable headers/route/menu traversal, gil/GC/special vendor discovery,
  yes/no/purification text, flying-offset exclusion and aetheryte/shard recognition.
- Standard Solver native smoke test and missing-state craft/gather/repair recovery.

These are validation items, not claimed runtime passes. Existing acquisition native
acceptance checklist remains separate in `vulcan-live-verification.md`.
