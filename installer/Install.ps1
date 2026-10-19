#Requires -Version 5.1
<#
.SYNOPSIS
    Installs Controller Toggle: the package-signing certificate, the Xbox Game Bar widget,
    and the helper service that turns the built-in controller on and off.

.DESCRIPTION
    1. Trusts the widget's self-signed certificate (Local Machine > Trusted People, which is all
       Windows needs to sideload an MSIX; nothing goes into the Root store).
    2. Installs the widget package (.msix) and its framework dependencies.
    3. Copies the helper to "C:\Program Files\ControllerToggle" and registers it as the
       "ControllerToggle" Windows service (runs as LocalSystem, starts automatically).
    4. Adds an entry to Settings > Apps so it can be uninstalled normally.

    Safe to run again to update or repair. Must run as administrator (it asks for elevation).
    Smart App Control must be off: it only runs code signed by a publicly trusted certificate
    authority (or known to Microsoft's cloud service), and this build is self-signed.

.PARAMETER Quiet
    Don't wait for a key press at the end.
#>
[CmdletBinding()]
param(
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'

$AppName      = 'Controller Toggle'
$ServiceName  = 'ControllerToggle'
$PackageName  = 'ControllerToggle.Widget'
$CertSubject  = 'CN=ControllerToggle'
$HelperExe    = 'ControllerToggleHelper.exe'
$InstallDir   = Join-Path $env:ProgramFiles 'ControllerToggle'
$DataDir      = Join-Path $env:ProgramData 'ControllerToggle'
$UninstallKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\ControllerToggle'

function Write-Step([int]$n, [int]$total, [string]$text) {
    Write-Host ''
    Write-Host "[$n/$total] $text" -ForegroundColor Cyan
}
function Write-Ok([string]$text)   { Write-Host "  [OK] $text" -ForegroundColor Green }
function Write-Info([string]$text) { Write-Host "       $text" -ForegroundColor Gray }
function Write-Warn([string]$text) { Write-Host "  [!]  $text" -ForegroundColor Yellow }

function Exit-Installer([int]$code) {
    try { Stop-Transcript | Out-Null } catch { }
    if (-not $Quiet) {
        Write-Host ''
        Read-Host 'Press Enter to close' | Out-Null
    }
    exit $code
}

function Test-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    return (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# ---------------------------------------------------------------- Smart App Control
# Smart App Control only runs code that Microsoft's cloud service knows or that is signed by a
# certificate authority in Microsoft's Trusted Root Program. This build is self-signed, so while
# it's on, Windows blocks the helper, and PowerShell may run this script in Constrained Language
# Mode, where it can't install anything. Stop with a clear message instead of failing halfway.
# (Only cmdlets and operators up to here, so this part also works in Constrained Language Mode.)
# VerifiedAndReputablePolicyState: 0 = off, 1 = on, 2 = evaluation.
$sacState = (Get-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy' -Name 'VerifiedAndReputablePolicyState' -ErrorAction SilentlyContinue).VerifiedAndReputablePolicyState
if ($sacState -eq 1 -or $ExecutionContext.SessionState.LanguageMode -ne 'FullLanguage') {
    Write-Host ''
    if ($sacState -eq 1) {
        Write-Host 'Smart App Control is on. It blocks apps that aren''t signed by a publicly trusted' -ForegroundColor Red
        Write-Host 'certificate, and that includes the Controller Toggle helper.' -ForegroundColor Red
        Write-Host ''
        Write-Host 'Turn it off in Windows Security > App & browser control > Smart App Control settings,' -ForegroundColor Yellow
        Write-Host 'then run setup again.' -ForegroundColor Yellow
    }
    else {
        Write-Host 'PowerShell is running this installer in Constrained Language Mode because an application' -ForegroundColor Red
        Write-Host 'control policy is active on this PC, so it can''t install anything.' -ForegroundColor Red
    }
    if (-not $Quiet) {
        Write-Host ''
        Read-Host 'Press Enter to close' | Out-Null
    }
    exit 1
}

# ---------------------------------------------------------------- elevation
if (-not (Test-Admin)) {
    Write-Host 'Controller Toggle needs administrator rights to install its helper service. Asking Windows...' -ForegroundColor Yellow
    $relaunch = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    if ($Quiet) { $relaunch += '-Quiet' }
    try {
        $process = Start-Process -FilePath 'powershell.exe' -ArgumentList $relaunch -Verb RunAs -Wait -PassThru
        exit $process.ExitCode
    }
    catch {
        Write-Host 'Administrator rights were declined. Right-click Install.cmd and choose "Run as administrator".' -ForegroundColor Red
        if (-not $Quiet) { Read-Host 'Press Enter to close' | Out-Null }
        exit 1
    }
}

$here = Split-Path -Parent $PSCommandPath
New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
try { Start-Transcript -Path (Join-Path $DataDir 'install.log') -Force | Out-Null } catch { }

trap {
    Write-Host ''
    Write-Host "INSTALL FAILED: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "A log was saved to $(Join-Path $DataDir 'install.log')" -ForegroundColor Gray
    Exit-Installer 1
}

Write-Host ''
Write-Host '  ==========================================' -ForegroundColor Cyan
Write-Host "    $AppName - setup" -ForegroundColor White
Write-Host '    Xbox Game Bar widget + helper service' -ForegroundColor Gray
Write-Host '  ==========================================' -ForegroundColor Cyan

$total = 6

# ---------------------------------------------------------------- 1. files
Write-Step 1 $total 'Checking the setup files'
$msix = Get-ChildItem -Path $here -File | Where-Object { $_.Name -like 'ControllerToggleWidget_*' -and ($_.Extension -eq '.msix' -or $_.Extension -eq '.appx') } |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
$cerPath    = Join-Path $here 'ControllerToggle.cer'
$helperPath = Join-Path $here $HelperExe
$dependencies = @(Get-ChildItem -Path $here -File -Filter '*.appx' | Where-Object { $_.Name -notlike 'ControllerToggleWidget_*' })
$depFolder = Join-Path $here 'Dependencies'
if (Test-Path $depFolder) { $dependencies += @(Get-ChildItem -Path $depFolder -Recurse -File -Filter '*.appx') }

if (-not $msix)                   { throw "The widget package (ControllerToggleWidget_*.msix) is missing from $here." }
if (-not (Test-Path $cerPath))    { throw "ControllerToggle.cer is missing from $here." }
if (-not (Test-Path $helperPath)) { throw "$HelperExe is missing from $here." }
Write-Ok "Widget package: $($msix.Name)"
Write-Ok "Helper: $HelperExe"
Write-Ok "Framework packages: $($dependencies.Count)"
if ($sacState -eq 2) {
    Write-Warn 'Smart App Control is in evaluation mode. If Windows switches it on, it will block the'
    Write-Info 'helper and the widget will show "Offline". To prevent that, turn it off in Windows Security >'
    Write-Info 'App & browser control > Smart App Control settings.'
}

# ---------------------------------------------------------------- 2. stop old helper
Write-Step 2 $total 'Stopping the previous helper (if installed)'
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') {
        # Stopping the service turns the built-in controller back on; the new one re-applies your settings.
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        try { $service.WaitForStatus('Stopped', (New-TimeSpan -Seconds 20)) } catch { }
    }
    Get-Process -Name 'ControllerToggleHelper' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Write-Ok 'Previous helper stopped'
}
else {
    Write-Info 'Not installed yet'
}

# ---------------------------------------------------------------- 3. certificate
Write-Step 3 $total 'Trusting the widget''s signing certificate'
$cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($cerPath)
if ($cert.Subject -ne $CertSubject) {
    throw "ControllerToggle.cer has subject '$($cert.Subject)', expected '$CertSubject'."
}
$existing = Get-ChildItem -Path 'Cert:\LocalMachine\TrustedPeople' | Where-Object { $_.Thumbprint -eq $cert.Thumbprint }
if ($existing) {
    Write-Ok 'Already trusted'
}
else {
    Import-Certificate -FilePath $cerPath -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
    Write-Ok "Added to Local Machine > Trusted People ($($cert.Thumbprint))"
}

# ---------------------------------------------------------------- 4. widget package
Write-Step 4 $total 'Installing the Game Bar widget'
# Game Bar holds our widget open; close it so the package can be replaced.
Get-Process -Name 'GameBar', 'GameBarFTServer', 'ControllerToggleWidget' -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800

$installed = $false
$lastError = $null
if ($dependencies.Count -gt 0) {
    try {
        Add-AppxPackage -Path $msix.FullName -DependencyPath ($dependencies | ForEach-Object { $_.FullName }) -ForceApplicationShutdown -ForceUpdateFromAnyVersion
        $installed = $true
    }
    catch {
        $lastError = $_.Exception.Message
        Write-Info 'Retrying without the bundled framework packages (newer versions are probably installed already)...'
    }
}
if (-not $installed) {
    try {
        Add-AppxPackage -Path $msix.FullName -ForceApplicationShutdown -ForceUpdateFromAnyVersion
        $installed = $true
    }
    catch {
        $lastError = $_.Exception.Message
    }
}
if (-not $installed) {
    throw "Windows refused the widget package: $lastError"
}
$package = Get-AppxPackage -Name $PackageName
Write-Ok "Installed $PackageName $($package.Version)"

# ---------------------------------------------------------------- 5. helper service
Write-Step 5 $total 'Installing the helper service'
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
$filesToCopy = @($HelperExe, "$HelperExe.config", 'Uninstall.ps1', 'Uninstall.cmd', 'Restore-Controller.cmd', 'ControllerToggle.cer', 'README.txt')
foreach ($name in $filesToCopy) {
    $source = Join-Path $here $name
    if (-not (Test-Path $source)) { continue }
    for ($attempt = 1; $attempt -le 10; $attempt++) {
        try {
            Copy-Item -Path $source -Destination $InstallDir -Force
            break
        }
        catch {
            if ($attempt -eq 10) { throw }
            Start-Sleep -Milliseconds 500   # the old helper may still be exiting
        }
    }
}
$servicePath = Join-Path $InstallDir $HelperExe
Write-Ok "Copied to $InstallDir"

# Quoted, because the path contains spaces.
$binaryPath = '"' + $servicePath + '"'
if (-not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) {
    New-Service -Name $ServiceName -BinaryPathName $binaryPath -DisplayName 'Controller Toggle helper' `
        -Description 'Turns the handheld''s built-in controller on and off for the Controller Toggle Xbox Game Bar widget.' `
        -StartupType Automatic | Out-Null
    Write-Ok 'Service created'
}
else {
    # Set the path directly (sc.exe config would drop the quotes around a path with spaces).
    Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName" -Name 'ImagePath' -Value $binaryPath
    Set-Service -Name $ServiceName -StartupType Automatic
    Write-Ok 'Service updated'
}
# Restart automatically if it ever crashes.
& sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/60000 | Out-Null

Start-Service -Name $ServiceName
try {
    (Get-Service -Name $ServiceName).WaitForStatus('Running', (New-TimeSpan -Seconds 20))
    Write-Ok 'Service running'
}
catch {
    Write-Warn 'The service is taking a while to start; continuing.'
}

# ---------------------------------------------------------------- 6. register + verify
Write-Step 6 $total 'Finishing up'
New-Item -Path $UninstallKey -Force | Out-Null
$uninstallCmd = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "' + (Join-Path $InstallDir 'Uninstall.ps1') + '"'
Set-ItemProperty -Path $UninstallKey -Name 'DisplayName'     -Value $AppName
Set-ItemProperty -Path $UninstallKey -Name 'DisplayVersion'  -Value ([string]$package.Version)
Set-ItemProperty -Path $UninstallKey -Name 'Publisher'       -Value 'Controller Toggle'
Set-ItemProperty -Path $UninstallKey -Name 'InstallLocation' -Value $InstallDir
Set-ItemProperty -Path $UninstallKey -Name 'DisplayIcon'     -Value $servicePath
Set-ItemProperty -Path $UninstallKey -Name 'UninstallString' -Value $uninstallCmd
Set-ItemProperty -Path $UninstallKey -Name 'NoModify' -Value 1 -Type DWord
Set-ItemProperty -Path $UninstallKey -Name 'NoRepair' -Value 1 -Type DWord
Write-Ok 'Added to Settings > Apps'

# Ask the running service what it found (same pipe the widget uses).
Start-Sleep -Seconds 1
$stateJson = & $servicePath --ping 2>$null
if ($LASTEXITCODE -eq 0 -and $stateJson) {
    $state = $stateJson | ConvertFrom-Json
    if ($state.builtInFound) {
        Write-Ok "Found the built-in controller: $($state.builtInName) (currently $($state.builtInState))"
    }
    else {
        Write-Warn 'The helper is running but did not recognize a built-in controller on this device.'
        Write-Info "Run `"$servicePath`" --list to see what it detects."
    }
}
else {
    Write-Warn "The helper didn't answer yet. If the widget says it can't reach the helper, restart the PC."
}

Write-Host ''
Write-Host '  ==========================================' -ForegroundColor Green
Write-Host '    Installed!' -ForegroundColor White
Write-Host '  ==========================================' -ForegroundColor Green
Write-Host ''
Write-Host '  Open Xbox Game Bar (Win+G or your handheld''s Game Bar button), then open' -ForegroundColor Gray
Write-Host '  the Widgets menu and pick "Controller Toggle". Pin it to keep it on screen.' -ForegroundColor Gray
Write-Host ''
Write-Host "  If the controller ever stays off: run Restore-Controller.cmd in $InstallDir" -ForegroundColor DarkGray

Exit-Installer 0
