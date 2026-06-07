<#
.SYNOPSIS
    Build, (re)register, and launch the Epub.App packaged WinUI 3 app.

.DESCRIPTION
    The app is a single-project MSIX app registered as a "loose layout" pointing
    straight at the build output. A plain `dotnet build` refreshes the exe but NOT
    the registered AppX layout, so the Start-menu icon can run stale code. This
    script rebuilds, repoints the registration at the current build output
    (unregister-then-register, because the version never changes), and launches it.

.EXAMPLE
    .\run.ps1                 # Release build, register, launch
    .\run.ps1 -Configuration Debug
    .\run.ps1 -SkipBuild      # just re-register current output + launch
    .\run.ps1 -NoLaunch       # build + register, don't start the app
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$Platform = 'ARM64',
    [switch]$SkipBuild,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot

# Package identity (from src\Epub.App\Package.appxmanifest)
$IdentityName      = 'FC5361A7-523B-43DD-BEBB-AC970566C7CC'
$PackageFamilyName = 'FC5361A7-523B-43DD-BEBB-AC970566C7CC_1z32rh13vfry6'
$AppId             = 'App'

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

# 1. Build ----------------------------------------------------------------------
if (-not $SkipBuild) {
    Write-Step "Building $Configuration|$Platform ..."
    dotnet build "$repoRoot\epub.slnx" -c $Configuration -p:Platform=$Platform --nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)." }
}

# 2. Locate the freshly generated loose-layout manifest -------------------------
# Use the AppxManifest.xml at the build-output ROOT (current bits) — NOT the one
# inside the older AppX\ subfolder.
$outRoot = Join-Path $repoRoot "src\Epub.App\bin\$Platform\$Configuration"
$manifest = Get-ChildItem -Path $outRoot -Recurse -Filter 'AppxManifest.xml' -ErrorAction SilentlyContinue |
    Where-Object { $_.DirectoryName -notmatch '\\AppX$' } |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $manifest) {
    throw "No AppxManifest.xml found under $outRoot. Build first (don't use -SkipBuild on a clean tree)."
}
Write-Step "Manifest: $($manifest.FullName)"

# 3. Re-register ----------------------------------------------------------------
# Same version every build, so a plain -Register is a no-op if already installed.
# Unregister first, then register the current layout.
$existing = Get-AppxPackage -Name $IdentityName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Step "Unregistering existing package ($($existing.InstallLocation)) ..."
    Remove-AppxPackage -Package $existing.PackageFullName -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
}
Write-Step "Registering current layout ..."
Add-AppxPackage -Register $manifest.FullName -ForceUpdateFromAnyVersion
$reg = Get-AppxPackage -Name $IdentityName
Write-Host "    Registered at: $($reg.InstallLocation)" -ForegroundColor Green

# 4. Launch ---------------------------------------------------------------------
if (-not $NoLaunch) {
    Write-Step "Launching ..."
    Start-Process "shell:AppsFolder\$PackageFamilyName!$AppId"
    Start-Sleep -Seconds 3
    $proc = Get-Process Epub.App -ErrorAction SilentlyContinue
    if ($proc) {
        Write-Host "    Running: PID $($proc.Id)" -ForegroundColor Green
    } else {
        Write-Warning "App did not appear to start — check for a crash dialog."
    }
} else {
    Write-Step "Skipped launch (-NoLaunch). Start it with:"
    Write-Host "    Start-Process 'shell:AppsFolder\$PackageFamilyName!$AppId'"
}
