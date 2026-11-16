#Requires -Version 5.1
<# Explicit developer-only reference fixture generation; never invoked by plugin builds. #>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Use Windows PowerShell and Windows Rust.' }
$root = Split-Path -Parent $PSScriptRoot
$pin = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'raphael-dependency.json') -Raw | ConvertFrom-Json
$source = Join-Path $root '.dependencies\raphael\source'
$git = Join-Path $env:ProgramFiles 'Git\cmd\git.exe'
$rustup = Join-Path $env:USERPROFILE '.cargo\bin\rustup.exe'
if (!(Test-Path -LiteralPath $source)) { throw 'Run Setup-Raphael.ps1 first; this exporter never checks out or modifies source.' }
$revision = & $git -C $source rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $revision -ne $pin.commit) { throw 'Raphael checkout does not match tools/raphael-dependency.json.' }
$remote = & $git -C $source remote get-url origin
if ($LASTEXITCODE -ne 0 -or $remote -ne $pin.repository) { throw 'Raphael checkout origin does not match pin.' }
$dirty = & $git -C $source status --porcelain --untracked-files=normal
if ($LASTEXITCODE -ne 0 -or $dirty) { throw 'Raphael checkout is dirty; existing work was not modified.' }
$output = Join-Path $root 'tests\GatherBuddy.Adaptive.Tests\Fixtures\raphael-reference.jsonl'
$directory = Split-Path -Parent $output
if (!(Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Path $directory | Out-Null }
$temporary = "$output.pending"
$oldPath = $env:PATH
try {
    $env:PATH = "$(Split-Path -Parent $rustup);$oldPath"
    & $rustup run $pin.toolchain cargo run --release --locked --target $pin.target `
        --manifest-path (Join-Path $PSScriptRoot 'adaptive-reference\Cargo.toml') `
        --target-dir (Join-Path $root '.dependencies\adaptive-reference\target') -- $temporary $pin.commit
    if ($LASTEXITCODE -ne 0) { throw 'Reference export failed; existing fixture not replaced.' }
    Move-Item -LiteralPath $temporary -Destination $output -Force
    Copy-Item -LiteralPath (Join-Path $source 'LICENSE') -Destination (Join-Path $directory 'LICENSE.raphael') -Force
    Write-Host "Exported pinned independent reference to $output"
}
finally {
    $env:PATH = $oldPath
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
}
