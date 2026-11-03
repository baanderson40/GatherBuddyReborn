# Local Raphael CLI dependency

Pure Raphael needs `raphael-cli.exe` beside the loaded `GatherBuddyReborn.dll`.
The upstream Raphael release executable is the GUI, not this CLI. Rust is needed
to build the CLI, not to run the resulting executable.

## Explicit setup

Install Windows x64 Rust (`stable-x86_64-pc-windows-msvc`), Git for Windows, and
Visual Studio/Build Tools with **Desktop development with C++** and a Windows SDK.
Use Windows PowerShell 5.1 or newer. From this repository's root:

```powershell
powershell -NoProfile -File tools/Setup-Raphael.ps1
```

From WSL, invoke Windows PowerShell, not Linux Cargo:

```bash
"/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe" -NoProfile -File tools/Setup-Raphael.ps1
```

Setup explicitly downloads/builds source; normal plugin builds and plugin startup
never do so. The source is pinned by full commit in
[`tools/raphael-dependency.json`](../tools/raphael-dependency.json), and Cargo runs
with `--locked`. The configured stable Rust toolchain can change over time; its
actual version and the binary SHA256 are recorded in `raphael-build.json`.
This pins source/dependencies, not a guarantee of bit-identical builds across
different compilers/Windows SDKs. Updating the pin requires compatibility tests.

Setup uses ignored `.dependencies/raphael/source` and stages the verified CLI,
Apache license, and provenance under `.dependencies/raphael/stage`. It checks
the exact source commit and clean working tree, then tests two real recipes
using plugin-compatible arguments and nonempty `action_ids` output. A failed
build/smoke test does not replace an existing stage. No broad cache cleanup,
checkout reset, release creation, or publishing is performed.

To reuse a clean existing checkout at the pinned commit:

```powershell
powershell -NoProfile -File tools/Setup-Raphael.ps1 -SourceDirectory '.dependencies\raphael\source'
```

## Plugin builds

```powershell
dotnet build GatherBuddy.sln --configuration Debug -p:Platform=x64
dotnet build GatherBuddy.sln --configuration Release -p:Platform=x64
```

The project copies all three staged files beside the plugin DLL, including after
a Clean/Rebuild and with a custom output directory. Clean removes output copies,
not the stage; deleting `.dependencies` requires running setup again. The stage
must exist before starting MSBuild. Advanced builds can override
`RaphaelDependencyDirectory` with another complete, trusted stage.

Missing/incomplete staging emits warning `GBR001` with setup instructions;
plugin-only compilation remains possible, but Pure Raphael will not work in a
fresh output directory. Never infer runtime availability from successful C#
compilation alone. Existing unrelated dependency warnings remain visible.

After rebuilding, reload the plugin and start a small Pure Raphael craft test.
Reload clears prior failed in-memory solves; valid saved solutions are retained
subject to existing cache settings. Successful setup smoke tests do not prove
in-game crafting, stat accuracy, or solver behavior for every recipe.

Runtime dependency preflight/recovery and repeated diagnostic suppression are
separate pending work. This setup does not change crafting or purchase logic.
