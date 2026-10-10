<#
.SYNOPSIS
    Builds and packs the Thrum Haptics release package.

.DESCRIPTION
    ONE package, both platforms. The manifest carries pluginFolderWin: bin and
    pluginFolderMac: bin/mac, and the Plugin Service loads whichever matches the
    running OS. A Windows user downloads the macOS payload too - about 10MB
    rather than 0.4MB - and in exchange there is one Marketplace listing, one
    review per release, and no way for the two platforms to drift out of version
    sync.

    Each platform is built from a COMPLETELY CLEAN TREE, and that is not
    tidiness - it is the fix for a real defect. An earlier version of this script
    built Windows and then macOS in sequence. Because
    AppendRuntimeIdentifierToOutputPath is false (needed so pluginFolderWin can
    stay 'bin'), the RID is stripped from the INTERMEDIATE paths as well, so both
    builds shared src/obj/Release and the second inherited state from the first.

    The macOS package that came out was subtly poisoned: identical file list,
    identical deps.json, an assembly of exactly the same size exporting the same
    APIs - and Logi Options+ refused to install it, every time, with a generic
    "Install plugin method failed". Eight consecutive packages failed that way
    while five built standalone installed first time. Nothing in the package
    content revealed it; only build provenance correlated.

    The macOS output is then COPIED into bin/mac of the Windows staging tree, so
    the two RIDs never meet in one intermediate directory.

    NOTE the dual package itself was never the problem. Every failing dual came
    from the poisoned sequential build, which welded two claims together - "dual
    layout is broken" and "shared obj is broken" - until a dual package built
    with clean provenance installed first time and separated them.

    Cleaning also protects the older trap this script was written for. A build
    only ADDS to its output directory, so a renamed or removed project leaves its
    old assemblies behind for pack to ship - invisible in the log, and verify
    passes because the package is structurally fine.

.EXAMPLE
    ./tools/pack-release.ps1 -Version 1.3.0
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Configuration = 'Release',

    # A build for testing on your own machines, never for release. Stamps
    # -Version into the STAGED manifest instead of requiring the source manifest
    # to match it, and marks the file name, so trying something out does not mean
    # editing LoupedeckPackage.yaml and remembering to put it back.
    [switch]$TestBuild
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'ThrumHapticsPlugin\src\ThrumHapticsPlugin.csproj'
$dist = Join-Path $root 'dist'
$staging = Join-Path $root 'ThrumHapticsPlugin\bin-package'

# NOTE bin-package is deliberately ABSENT. It is the staging tree, and the Windows
# output is copied into it before the macOS build runs - so listing it here would
# have Reset-Tree delete the half-assembled package on the way past. It is cleared
# once, explicitly, at the start of the script instead.
$scratch = @(
    'ThrumHapticsPlugin\bin'
    'ThrumHapticsPlugin\bin-mac'
    'ThrumHapticsPlugin\src\obj'
    'ThrumHapticsSettings\bin'
    'ThrumHapticsSettings\obj'
    'ThrumHapticsSettingsMac\bin'
    'ThrumHapticsSettingsMac\obj'
)

function Reset-Tree {
    foreach ($d in $scratch) {
        $p = Join-Path $root $d
        if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Recurse -Force }
    }
}

# Out-Host, not bare invocation. A PowerShell function returns everything it
# writes to the pipeline, so build and packer chatter would otherwise come back
# as part of the return value.
function Invoke-Build {
    param([string]$Platform, [string[]]$ExtraArgs)

    Write-Host "Building $Platform from a clean tree..." -ForegroundColor Cyan
    Reset-Tree

    dotnet build $project -c $Configuration --nologo -v minimal @ExtraArgs | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "$Platform build failed" }
}

if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
if (-not (Test-Path -LiteralPath $dist)) { New-Item -ItemType Directory -Path $dist | Out-Null }

# Windows first: its output tree carries the metadata and events folders, so it
# becomes the staging tree that macOS is then nested into.
Invoke-Build -Platform 'Windows' -ExtraArgs @('-p:IsDevLoopBuild=false')
Copy-Item -LiteralPath (Join-Path $root "ThrumHapticsPlugin\bin\$Configuration") -Destination $staging -Recurse -Force

Invoke-Build -Platform 'macOS' -ExtraArgs @('-r', 'osx-arm64')
Copy-Item -LiteralPath (Join-Path $root "ThrumHapticsPlugin\bin-mac\$Configuration\bin") `
    -Destination (Join-Path $staging 'bin\mac') -Recurse -Force

$manifest = Join-Path $staging 'metadata\LoupedeckPackage.yaml'
$text = [IO.File]::ReadAllText($manifest, [Text.Encoding]::UTF8)

# pluginFolderMac is 'bin' in the source manifest so a macOS-only build can be
# packed straight from bin-mac. In the dual package it has to point at the nested
# copy instead.
$text = $text.Replace('pluginFolderMac: bin', 'pluginFolderMac: bin/mac')

if ($TestBuild) {
    $text = [regex]::Replace($text, '(?m)^version:\s*\S+', "version: $Version")
}

[IO.File]::WriteAllText($manifest, $text, (New-Object System.Text.UTF8Encoding($false)))

$declared = (($text -split "`n") | Where-Object { $_ -match '^version:' }) -replace 'version:\s*', ''
if ($declared.Trim() -ne $Version) {
    throw "LoupedeckPackage.yaml declares '$($declared.Trim())' but -Version said '$Version'."
}

foreach ($key in @('pluginFolderWin: bin', 'pluginFolderMac: bin/mac')) {
    if ($text -notmatch [regex]::Escape($key)) { throw "manifest is missing '$key'" }
}

# Both platforms must actually be present. A missing folder still packs and still
# verifies - the package is structurally valid, it just cannot run on one OS.
foreach ($probe in @('bin\ThrumHapticsPlugin.dll', 'bin\ThrumHapticsSettings.exe',
                     'bin\mac\ThrumHapticsPlugin.dll', 'bin\mac\ThrumHapticsSettingsMac',
                     'bin\mac\AppIcon.png')) {
    if (-not (Test-Path -LiteralPath (Join-Path $staging $probe))) {
        throw "staging is missing $probe"
    }
}

$stale = Get-ChildItem $staging -Recurse -File | Where-Object { $_.Name -match '(?i)mxhaptic' }
if ($stale) { throw "Files from the old plugin name survived: $($stale.Name -join ', ')" }

$suffix = if ($TestBuild) { '_TEST' } else { '' }
$out = Join-Path $dist ("ThrumHaptics_" + ($Version -replace '\.', '_') + "$suffix.lplug4")
if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Force }

logiplugintool pack $staging $out | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'pack failed' }

logiplugintool verify $out | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'verify failed' }

Write-Host ''
Get-Item $out | Select-Object Name, @{n = 'MB'; e = { [math]::Round($_.Length / 1MB, 2) } }
