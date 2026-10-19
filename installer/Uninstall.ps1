#Requires -Version 5.1
<#
.SYNOPSIS
    Removes Controller Toggle and makes sure the built-in controller is turned back on.

.PARAMETER Quiet
    Don't wait for a key press at the end.

.PARAMETER KeepCertificate
    Leave the signing certificate in Trusted People (handy if you'll reinstall a build signed with it).
#>
[CmdletBinding()]
param(
    [switch]$Quiet,
    [switch]$KeepCertificate
)

$ErrorActionPreference = 'Stop'

$ServiceName  = 'ControllerToggle'
$PackageName  = 'ControllerToggle.Widget'
$CertSubject  = 'CN=ControllerToggle'
$HelperExe    = 'ControllerToggleHelper.exe'
$InstallDir   = Join-Path $env:ProgramFiles 'ControllerToggle'
$DataDir      = Join-Path $env:ProgramData 'ControllerToggle'
$UninstallKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\ControllerToggle'

function Write-Ok([string]$text)   { Write-Host "  [OK] $text" -ForegroundColor Green }
function Write-Info([string]$text) { Write-Host "       $text" -ForegroundColor Gray }
function Write-Warn([string]$text) { Write-Host "  [!]  $text" -ForegroundColor Yellow }

function Test-Admin {
    # whoami instead of .NET calls, so uninstalling also works if PowerShell runs this script in
    # Constrained Language Mode (for example after Smart App Control was turned back on).
    # High (12288) or System (16384) mandatory level = elevated.
    $groups = & (Join-Path $env:SystemRoot 'System32\whoami.exe') /groups
    return [bool]($groups -match 'S-1-16-12288|S-1-16-16384')
}

# Turns the built-in controller back on with Windows' own pnputil, for when the helper can't run
# (for example, Smart App Control blocks it): the device the helper last managed, then the
# Legion Go S gamepad interface (MI_01, driven by xusb22).
function Restore-WithPnpUtil {
    $pnputil = Join-Path $env:SystemRoot 'System32\pnputil.exe'
    $commands = @()
    $settingsFile = Join-Path $DataDir 'settings.ini'
    if (Test-Path $settingsFile) {
        foreach ($line in @(Get-Content -Path $settingsFile -ErrorAction SilentlyContinue)) {
            if ($line -match '^\s*TargetInstanceId\s*=\s*(\S.*?)\s*$') { $commands += , @('/enable-device', $Matches[1]) }
        }
    }
    foreach ($hardwareId in @('USB\VID_1A86&PID_E310&MI_01')) {
        $commands += , @('/enable-device', '/deviceid', $hardwareId)
    }

    $enabled = $false
    foreach ($arguments in $commands) {
        try {
            & $pnputil @arguments | Out-Null
            if ($LASTEXITCODE -in 0, 3010) { $enabled = $true }   # 3010 = done, restart needed
        }
        catch { }
    }
    return $enabled
}

function Get-ForwardedArgs {
    $list = @()
    if ($Quiet) { $list += '-Quiet' }
    if ($KeepCertificate) { $list += '-KeepCertificate' }
    return $list
}

# ---------------------------------------------------------------- elevation
if (-not (Test-Admin)) {
    $relaunch = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"") + (Get-ForwardedArgs)
    try {
        $process = Start-Process -FilePath 'powershell.exe' -ArgumentList $relaunch -Verb RunAs -Wait -PassThru
        exit $process.ExitCode
    }
    catch {
        Write-Host 'Administrator rights are needed to uninstall.' -ForegroundColor Red
        if (-not $Quiet) { Read-Host 'Press Enter to close' | Out-Null }
        exit 1
    }
}

# Run from a temp copy so the install folder (where this script may live) can be deleted.
if ($PSCommandPath -like "$InstallDir\*") {
    $tempCopy = Join-Path $env:TEMP 'ControllerToggle-Uninstall.ps1'
    Copy-Item -Path $PSCommandPath -Destination $tempCopy -Force
    $relaunch = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$tempCopy`"") + (Get-ForwardedArgs)
    $process = Start-Process -FilePath 'powershell.exe' -ArgumentList $relaunch -Wait -PassThru -NoNewWindow
    exit $process.ExitCode
}

Write-Host ''
Write-Host '  Uninstalling Controller Toggle' -ForegroundColor Cyan
Write-Host ''

# 1. Stop the service. Its stop handler turns the built-in controller back on.
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        try { $service.WaitForStatus('Stopped', (New-TimeSpan -Seconds 20)) } catch { }
    }
    Write-Ok 'Helper service stopped'
}

# 2. Belt and braces: turn the controller on directly, even if the service was broken.
$restored = $false
$helper = Join-Path $InstallDir $HelperExe
if (Test-Path $helper) {
    try {
        & $helper --restore | ForEach-Object { Write-Info $_ }
        $restored = ($LASTEXITCODE -eq 0)
    }
    catch {
        # Windows refused to start it (Smart App Control, for example).
        Write-Info "The helper couldn't run: $($_.Exception.Message)"
    }
}
if ($restored) {
    Write-Ok 'Built-in controller is on'
}
elseif (Restore-WithPnpUtil) {
    Write-Ok 'Built-in controller turned back on (with pnputil)'
}
else {
    Write-Warn 'Could not confirm the built-in controller is on (check Device Manager).'
}

# 3. Remove the service.
if ($service) {
    & sc.exe delete $ServiceName | Out-Null
    for ($i = 0; $i -lt 20 -and (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 250 }
    Write-Ok 'Helper service removed'
}
Get-Process -Name 'ControllerToggleHelper' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

# 4. Remove the widget package.
Get-Process -Name 'GameBar', 'GameBarFTServer', 'ControllerToggleWidget' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
$packages = @(Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue)
try { $packages += @(Get-AppxPackage -AllUsers -Name $PackageName -ErrorAction SilentlyContinue) } catch { }
$removed = @{}
foreach ($pkg in $packages) {
    if ($removed.ContainsKey($pkg.PackageFullName)) { continue }
    $removed[$pkg.PackageFullName] = $true
    try {
        Remove-AppxPackage -Package $pkg.PackageFullName -AllUsers -ErrorAction Stop
    }
    catch {
        Remove-AppxPackage -Package $pkg.PackageFullName -ErrorAction SilentlyContinue
    }
}
if ($removed.Count -gt 0) { Write-Ok 'Game Bar widget removed' } else { Write-Info 'Widget package was not installed' }

# 5. Remove the certificate.
if (-not $KeepCertificate) {
    $certs = @(Get-ChildItem -Path 'Cert:\LocalMachine\TrustedPeople' | Where-Object { $_.Subject -eq $CertSubject })
    foreach ($c in $certs) { Remove-Item -Path ('Cert:\LocalMachine\TrustedPeople\' + $c.Thumbprint) -Force }
    if ($certs.Count -gt 0) { Write-Ok 'Signing certificate removed' }
}

# 6. Remove files and the Settings > Apps entry.
Remove-Item -Path $UninstallKey -Recurse -Force -ErrorAction SilentlyContinue
foreach ($dir in @($InstallDir, $DataDir)) {
    if (Test-Path $dir) {
        try {
            Remove-Item -Path $dir -Recurse -Force -ErrorAction Stop
        }
        catch {
            Write-Warn "Couldn't delete $dir ($($_.Exception.Message)). You can delete it manually."
        }
    }
}
Write-Ok 'Files removed'

Write-Host ''
Write-Host '  Controller Toggle has been uninstalled.' -ForegroundColor Green
if (-not $Quiet) {
    Write-Host ''
    Read-Host 'Press Enter to close' | Out-Null
}
exit 0
