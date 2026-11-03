#Requires -Version 5.1
<#
.SYNOPSIS
Build and stage the pinned Windows x64 Raphael CLI. Run explicitly; normal plugin builds never invoke it.
.PARAMETER SourceDirectory
Optional existing clean checkout at the exact pinned commit. Never resets or checks out over existing work.
#>
[CmdletBinding()]
param([string]$SourceDirectory)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Use Windows PowerShell and Windows Rust, including when launching from WSL.' }

$root = Split-Path -Parent $PSScriptRoot
$pin = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'raphael-dependency.json') -Raw | ConvertFrom-Json
if ($pin.commit -notmatch '^[0-9a-f]{40}$' -or $pin.target -ne 'x86_64-pc-windows-msvc') { throw 'Invalid Raphael source pin or target.' }
$cache = Join-Path $root '.dependencies\raphael'
$stage = Join-Path $cache 'stage'
if (!$SourceDirectory) { $SourceDirectory = Join-Path $cache 'source' }
$SourceDirectory = [IO.Path]::GetFullPath($SourceDirectory)

function Find-Tool([string]$name, [string]$fallback) {
    $command = Get-Command $name -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    if (Test-Path -LiteralPath $fallback -PathType Leaf) { return $fallback }
    throw "Missing $name. Install the Windows tool and reopen the terminal."
}

$git = Find-Tool 'git.exe' (Join-Path $env:ProgramFiles 'Git\cmd\git.exe')
$rustup = Find-Tool 'rustup.exe' (Join-Path $env:USERPROFILE '.cargo\bin\rustup.exe')
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (!(Test-Path -LiteralPath $vswhere)) { throw 'Install Visual Studio Build Tools with Desktop development with C++ and a Windows SDK.' }
$visualStudio = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if ($LASTEXITCODE -ne 0 -or !$visualStudio) { throw 'MSVC x64 build tools missing. Install Desktop development with C++.' }
$kits = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots' -ErrorAction SilentlyContinue
if (!$kits -or !$kits.KitsRoot10) { throw 'Windows SDK missing. Install it using the Visual Studio Installer.' }
$sdkReady = @(Get-ChildItem -LiteralPath (Join-Path $kits.KitsRoot10 'Include') -Directory | Where-Object {
    (Test-Path -LiteralPath (Join-Path $_.FullName 'um\Windows.h')) -and
    (Test-Path -LiteralPath (Join-Path $kits.KitsRoot10 "Lib\$($_.Name)\um\x64\kernel32.lib")) -and
    (Test-Path -LiteralPath (Join-Path $kits.KitsRoot10 "Lib\$($_.Name)\ucrt\x64\ucrt.lib"))
})
if ($sdkReady.Count -eq 0) { throw 'Windows SDK x64 headers/libraries missing. Repair the SDK installation.' }

function Invoke-Checked([string]$program, [string[]]$arguments) {
    & $program @arguments
    if ($LASTEXITCODE -ne 0) { throw "$program failed (exit $LASTEXITCODE): $($arguments -join ' ')" }
}

function Test-Cli([string]$program, [string]$arguments) {
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = $program
    $info.Arguments = $arguments
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $info
    try {
        if (!$process.Start()) { throw 'Could not start Raphael CLI.' }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(30000)) {
            $process.Kill()
            $process.WaitForExit()
            throw 'Raphael smoke test timed out after 30 seconds.'
        }
        $output = $stdout.GetAwaiter().GetResult().Trim()
        $errorText = $stderr.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw "Raphael smoke test failed: $errorText" }
        return $output
    }
    finally { $process.Dispose() }
}

$oldPath = $env:PATH
$oldToolchain = $env:RUSTUP_TOOLCHAIN
try {
    $env:PATH = "$(Split-Path -Parent $rustup);$oldPath"
    $env:RUSTUP_TOOLCHAIN = $pin.toolchain
    $rustVersion = & $rustup run $pin.toolchain rustc --version
    if ($LASTEXITCODE -ne 0) { throw "Install the Windows toolchain explicitly: rustup toolchain install $($pin.toolchain)" }
    Write-Host "Using $rustVersion; MSVC at $visualStudio"

    if (!(Test-Path -LiteralPath $SourceDirectory)) {
        New-Item -ItemType Directory -Path (Split-Path -Parent $SourceDirectory) -Force | Out-Null
        Invoke-Checked $git @('clone', '--depth', '1', '--branch', $pin.tag, $pin.repository, $SourceDirectory)
    }
    if (!(Test-Path -LiteralPath (Join-Path $SourceDirectory '.git'))) { throw 'SourceDirectory must be a clean Git checkout of the pinned Raphael source.' }
    $remote = & $git -C $SourceDirectory remote get-url origin
    if ($LASTEXITCODE -ne 0 -or $remote -ne $pin.repository) { throw 'Source checkout origin does not match the pinned upstream repository.' }
    $revision = & $git -C $SourceDirectory rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or $revision -ne $pin.commit) { throw "Source must be at $($pin.commit). Existing checkout was not modified; use a separate checkout." }
    $dirty = & $git -C $SourceDirectory status --porcelain --untracked-files=normal
    if ($LASTEXITCODE -ne 0 -or $dirty) { throw 'Source checkout has local changes. Existing work was not modified.' }

    Push-Location -LiteralPath $SourceDirectory
    try {
        Invoke-Checked $rustup @('run', $pin.toolchain, 'cargo', 'build', '--release', '--locked', '--package', 'raphael-cli', '--target', $pin.target)
    }
    finally { Pop-Location }
    $binary = Join-Path $SourceDirectory "target\$($pin.target)\release\raphael-cli.exe"
    $version = Test-Cli $binary '--version'
    if ($version -ne "raphael-cli $($pin.cliVersion)") { throw "Unexpected CLI version: $version" }
    foreach ($arguments in @(
        'solve --recipe-id 87 --level 100 --stats 5933 5470 649 --manipulation --output-variables action_ids',
        'solve --recipe-id 1526 --level 100 --stats 5933 5470 649 --manipulation --initial 100 --backload-progress --heart-and-soul --quick-innovation --output-variables action_ids'
    )) {
        $solution = Test-Cli $binary $arguments
        if ($solution -notmatch '^"\[(\d+(,\s*\d+)*)\]"$') { throw "Unexpected action_ids output: $solution" }
        foreach ($text in ($solution.Trim('"', '[', ']') -split ',')) {
            [uint32]$id = 0
            if (![uint32]::TryParse($text.Trim(), [ref]$id) -or $id -eq 0) { throw 'Invalid action ID in smoke-test output.' }
        }
        Write-Host "Smoke test passed: $solution"
    }

    # Do not modify an existing working stage until both real solves have succeeded.
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    $suffix = [Guid]::NewGuid().ToString('N')
    $temporaryBinary = Join-Path $stage "raphael-cli.$suffix.tmp"
    $temporaryLicense = Join-Path $stage "raphael-LICENSE.$suffix.tmp"
    $temporaryInfo = Join-Path $stage "raphael-build.$suffix.tmp"
    try {
        Copy-Item -LiteralPath $binary -Destination $temporaryBinary
        Copy-Item -LiteralPath (Join-Path $SourceDirectory 'LICENSE') -Destination $temporaryLicense
        $hash = (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash.ToLowerInvariant()
        if ((Get-FileHash -LiteralPath $temporaryBinary -Algorithm SHA256).Hash.ToLowerInvariant() -ne $hash) { throw 'Staged executable hash mismatch.' }
        [ordered]@{
            repository = $pin.repository; tag = $pin.tag; commit = $pin.commit
            toolchain = $pin.toolchain; rustc = $rustVersion; target = $pin.target
            cliVersion = $pin.cliVersion; sha256 = $hash; builtUtc = [DateTime]::UtcNow.ToString('o')
        } | ConvertTo-Json | Set-Content -LiteralPath $temporaryInfo -Encoding UTF8
        Move-Item -LiteralPath $temporaryLicense -Destination (Join-Path $stage 'raphael-LICENSE.txt') -Force
        Move-Item -LiteralPath $temporaryBinary -Destination (Join-Path $stage 'raphael-cli.exe') -Force
        Move-Item -LiteralPath $temporaryInfo -Destination (Join-Path $stage 'raphael-build.json') -Force
    }
    finally {
        foreach ($temporary in @($temporaryBinary, $temporaryLicense, $temporaryInfo)) {
            if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
        }
    }
    Write-Host "Staged $version at $stage (SHA256 $hash). Rebuild GBR to copy it beside the plugin DLL."
}
finally {
    $env:PATH = $oldPath
    $env:RUSTUP_TOOLCHAIN = $oldToolchain
}
