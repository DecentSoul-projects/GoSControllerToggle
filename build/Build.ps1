#Requires -Version 5.1
<#
.SYNOPSIS
    Builds Controller Toggle: the helper service, the Xbox Game Bar widget package, and the
    installer (dist\ControllerToggle-Setup.exe and dist\ControllerToggle-Setup.zip).

.DESCRIPTION
    Needs Visual Studio 2022 (Community is fine) with the "WinUI application development"
    workload and its optional "Universal Windows Platform tools" component (older versions:
    the "Universal Windows Platform development" workload).

    The first build creates a self-signed code-signing certificate in .\signing
    (subject CN=ControllerToggle). Later builds reuse it, so new versions install over old
    ones. Keep that folder private: anything signed with it is trusted on the PCs where you
    installed Controller Toggle.

.PARAMETER NoExe
    Skip the single-file Setup.exe (the zip is always created).
#>
[CmdletBinding()]
param(
    [switch]$NoExe
)

$ErrorActionPreference = 'Stop'

$Root      = Split-Path -Parent $PSScriptRoot
$Src       = Join-Path $Root 'src'
$Installer = Join-Path $Root 'installer'
$Dist      = Join-Path $Root 'dist'
$Work      = Join-Path $Root 'obj'
$Signing   = Join-Path $Root 'signing'
$Publisher = 'CN=ControllerToggle'
$MinSdk    = [version]'10.0.19041.0'

# Package version: 1.0.<yyDDD>.<HHmm> so every build installs over the previous one.
$now = Get-Date
$Version = '1.0.{0}{1}.{2}' -f $now.ToString('yy'), $now.DayOfYear.ToString('000'), ($now.Hour * 100 + $now.Minute)

function Step([string]$text) {
    Write-Host ''
    Write-Host "==> $text" -ForegroundColor Cyan
}

function Fail([string]$text) {
    Write-Host ''
    Write-Host "BUILD FAILED: $text" -ForegroundColor Red
    exit 1
}

New-Item -ItemType Directory -Force -Path $Dist, $Work | Out-Null

# ------------------------------------------------------------------ Visual Studio
Step 'Finding Visual Studio with the Universal Windows Platform tools'
$installHelp = ("Open 'Visual Studio Installer', click Modify next to Visual Studio 2022, tick the 'WinUI application development' " +
                "workload (named 'Windows application development' in 17.10-17.12, 'Universal Windows Platform development' before that), " +
                "then in 'Installation details' on the right tick 'Universal Windows Platform tools', click Modify, and run Build.cmd again.")
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) {
    Fail ("Visual Studio isn't installed. Install Visual Studio 2022 Community (free) from https://visualstudio.microsoft.com/, then: " + $installHelp)
}
# Since Visual Studio 17.10 the UWP tools are an optional part of the WinUI workload, so check for
# the C# UWP build targets themselves rather than for a workload.
$instances = @(& $vswhere -prerelease -products * -sort -property installationPath)
$vsPath = $null
foreach ($candidate in $instances) {
    $xamlDir = Join-Path $candidate 'MSBuild\Microsoft\WindowsXaml'
    if (-not (Test-Path (Join-Path $candidate 'MSBuild\Current\Bin\MSBuild.exe')) -or -not (Test-Path $xamlDir)) { continue }
    $xamlTargets = Get-ChildItem -Path $xamlDir -Recurse -Filter 'Microsoft.Windows.UI.Xaml.CSharp.targets' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($xamlTargets) {
        $vsPath = $candidate
        break
    }
}
if (-not $vsPath) {
    if ($instances.Count -gt 0) {
        Fail ("Visual Studio is installed ($($instances[0])) but without the UWP build tools. " + $installHelp)
    }
    Fail ("Visual Studio 2022 is required. " + $installHelp)
}
$msbuild = Join-Path $vsPath 'MSBuild\Current\Bin\MSBuild.exe'
$csc     = Join-Path $vsPath 'MSBuild\Current\Bin\Roslyn\csc.exe'
foreach ($tool in @($msbuild, $csc)) {
    if (-not (Test-Path $tool)) { Fail "Expected tool not found: $tool" }
}
Write-Host "    $vsPath"

# ------------------------------------------------------------------ Windows SDK
Step 'Finding the Windows SDK'
$uapRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\Platforms\UAP'
$sdk = $null
if (Test-Path $uapRoot) {
    $sdk = Get-ChildItem -Path $uapRoot -Directory |
        Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' -and (Test-Path (Join-Path $_.FullName 'Platform.xml')) } |
        ForEach-Object { [version]$_.Name } |
        Where-Object { $_ -ge $MinSdk } |
        Sort-Object -Descending |
        Select-Object -First 1
}
if (-not $sdk) {
    Fail ("No Windows SDK 10.0.19041 or newer was found. In Visual Studio Installer > Modify > Individual components, " +
          "add a 'Windows 11 SDK', then run Build.cmd again.")
}
Write-Host "    Windows SDK $sdk"

# ------------------------------------------------------------------ Signing certificate
Step 'Preparing the package-signing certificate'
New-Item -ItemType Directory -Force -Path $Signing | Out-Null
$pfx     = Join-Path $Signing 'ControllerToggle.pfx'
$cer     = Join-Path $Signing 'ControllerToggle.cer'
$pwdFile = Join-Path $Signing 'pfx-password.txt'
if (-not ((Test-Path $pfx) -and (Test-Path $cer) -and (Test-Path $pwdFile))) {
    # Same recipe as Microsoft's "Create a certificate for package signing" docs.
    $plain = [Guid]::NewGuid().ToString('N')
    $cert = New-SelfSignedCertificate -Type Custom -Subject $Publisher -KeyUsage DigitalSignature `
        -FriendlyName 'Controller Toggle package signing (self-signed)' `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}') `
        -NotAfter (Get-Date).AddYears(10)
    $secure = ConvertTo-SecureString -String $plain -Force -AsPlainText
    Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $secure | Out-Null
    Export-Certificate -Cert $cert -FilePath $cer | Out-Null
    [IO.File]::WriteAllText($pwdFile, $plain)
    Remove-Item -Path ('Cert:\CurrentUser\My\' + $cert.Thumbprint) -ErrorAction SilentlyContinue
    Write-Host "    Created a new certificate ($($cert.Thumbprint))"
}
else {
    Write-Host '    Reusing signing\ControllerToggle.pfx'
}
$pfxPassword = ([IO.File]::ReadAllText($pwdFile)).Trim()

# ------------------------------------------------------------------ Helper
Step 'Building the helper (ControllerToggleHelper.exe)'
$fx = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$helperOut = Join-Path $Work 'helper'
if (Test-Path $helperOut) { Remove-Item -Path $helperOut -Recurse -Force }
New-Item -ItemType Directory -Force -Path $helperOut | Out-Null
$helperExe = Join-Path $helperOut 'ControllerToggleHelper.exe'
$sources = @(Get-ChildItem -Path (Join-Path $Src 'Helper') -Recurse -Filter '*.cs' |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
    ForEach-Object { $_.FullName })
$cscArgs = @(
    '/nologo', '/noconfig', '/target:exe', '/platform:x64', '/optimize+', '/langversion:7.3', '/warn:4',
    "/out:$helperExe",
    "/win32icon:$(Join-Path $Installer 'SetupLauncher\ControllerToggle.ico')",
    "/reference:$fx\System.dll",
    "/reference:$fx\System.Core.dll",
    "/reference:$fx\System.ServiceProcess.dll"
) + $sources
& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { Fail "The helper didn't compile (see the errors above)." }
Copy-Item -Path (Join-Path $Src 'Helper\App.config') -Destination "$helperExe.config" -Force
# Quick sanity check of the native structure layouts on this machine's .NET Framework.
& $helperExe --selftest | Select-Object -First 5 | ForEach-Object { Write-Host "    $_" }

# ------------------------------------------------------------------ Widget
Step 'Building the Game Bar widget (the first build downloads NuGet packages and can take a few minutes)'
$widgetDir    = Join-Path $Src 'Widget'
$manifestPath = Join-Path $widgetDir 'Package.appxmanifest'
$pkgRoot      = Join-Path $widgetDir 'AppPackages'
if (Test-Path $pkgRoot) { Remove-Item -Path $pkgRoot -Recurse -Force }

$originalManifest = [IO.File]::ReadAllText($manifestPath)
try {
    # Stamp this build's version (and make sure the publisher matches the certificate).
    $stamped = [regex]::Replace($originalManifest, '(<Identity\b[^>]*?\bVersion=")[^"]*(")', ('${1}' + $Version + '${2}'))
    $stamped = [regex]::Replace($stamped, '(<Identity\b[^>]*?\bPublisher=")[^"]*(")', ('${1}' + $Publisher + '${2}'))
    [IO.File]::WriteAllText($manifestPath, $stamped, (New-Object Text.UTF8Encoding($false)))

    $msbuildArgs = @(
        (Join-Path $widgetDir 'ControllerToggleWidget.csproj'),
        '/restore', '/nologo', '/verbosity:minimal', '/maxcpucount',
        '/p:Configuration=Release',
        '/p:Platform=x64',
        '/p:AppxBundle=Never',
        '/p:AppxBundlePlatforms=x64',
        '/p:UapAppxPackageBuildMode=SideloadOnly',
        '/p:GenerateAppxPackageOnBuild=true',
        '/p:GenerateAppInstallerFile=false',
        '/p:AppxPackageSigningEnabled=true',
        "/p:PackageCertificateKeyFile=$pfx",
        "/p:PackageCertificatePassword=$pfxPassword",
        '/p:PackageCertificateThumbprint=',
        "/p:TargetPlatformVersion=$sdk"
    )
    & $msbuild @msbuildArgs
    if ($LASTEXITCODE -ne 0) { Fail "The widget didn't build (see the errors above)." }
}
finally {
    [IO.File]::WriteAllText($manifestPath, $originalManifest, (New-Object Text.UTF8Encoding($false)))
}

$pkgFolder = Get-ChildItem -Path $pkgRoot -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -like "ControllerToggleWidget_$($Version)_*" } | Select-Object -First 1
if (-not $pkgFolder) {
    $pkgFolder = Get-ChildItem -Path $pkgRoot -Directory -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
}
if (-not $pkgFolder) { Fail "MSBuild didn't create an app package under $pkgRoot." }
$msix = Get-ChildItem -Path $pkgFolder.FullName -File | Where-Object { $_.Extension -eq '.msix' -or $_.Extension -eq '.appx' } | Select-Object -First 1
if (-not $msix) { Fail "No .msix found in $($pkgFolder.FullName)." }
$deps = @()
$depDir = Join-Path $pkgFolder.FullName 'Dependencies\x64'
if (Test-Path $depDir) {
    $deps = @(Get-ChildItem -Path $depDir -File | Where-Object { $_.Extension -eq '.appx' -or $_.Extension -eq '.msix' })
}
Write-Host "    $($msix.Name) (+ $($deps.Count) framework packages)"

# ------------------------------------------------------------------ Installer
Step 'Assembling the installer'
$stage = Join-Path $Dist 'ControllerToggle-Setup'
if (Test-Path $stage) { Remove-Item -Path $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

# Flat layout: everything Install.ps1 needs sits next to it.
Copy-Item -Path $msix.FullName -Destination $stage
foreach ($dep in $deps) { Copy-Item -Path $dep.FullName -Destination $stage }
Copy-Item -Path $cer -Destination (Join-Path $stage 'ControllerToggle.cer')
Copy-Item -Path $helperExe, "$helperExe.config" -Destination $stage
foreach ($name in @('Install.ps1', 'Install.cmd', 'Uninstall.ps1', 'Uninstall.cmd', 'Restore-Controller.cmd', 'README.txt')) {
    Copy-Item -Path (Join-Path $Installer $name) -Destination $stage
}

$zip = Join-Path $Dist 'ControllerToggle-Setup.zip'
if (Test-Path $zip) { Remove-Item -Path $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
Write-Host "    $zip"

$exe = Join-Path $Dist 'ControllerToggle-Setup.exe'
if (-not $NoExe) {
    # Single-file installer: a small launcher (installer\SetupLauncher\Setup.cs) with the zip embedded.
    # Its manifest asks for admin once, then it unpacks to %TEMP% and runs Install.ps1.
    $launcherDir = Join-Path $Installer 'SetupLauncher'
    if (Test-Path $exe) { Remove-Item -Path $exe -Force }
    $setupArgs = @(
        '/nologo', '/noconfig', '/target:exe', '/platform:x64', '/optimize+', '/langversion:7.3', '/warn:4',
        "/out:$exe",
        "/win32manifest:$(Join-Path $launcherDir 'Setup.manifest')",
        "/win32icon:$(Join-Path $launcherDir 'ControllerToggle.ico')",
        "/resource:$zip,payload.zip",
        "/reference:$fx\System.dll",
        "/reference:$fx\System.IO.Compression.dll",
        (Join-Path $launcherDir 'Setup.cs')
    )
    & $csc @setupArgs
    if ($LASTEXITCODE -eq 0 -and (Test-Path $exe)) {
        Write-Host "    $exe"
    }
    else {
        Write-Warning "Couldn't build ControllerToggle-Setup.exe (see above); use the zip (run Install.cmd inside it)."
    }
}

Write-Host ''
Write-Host 'BUILD SUCCEEDED' -ForegroundColor Green
Write-Host "  Version:   $Version"
if (Test-Path $exe) { Write-Host "  Installer: $exe" }
Write-Host "  Zip:       $zip"
Write-Host ''
Write-Host 'Copy ControllerToggle-Setup.exe (or the zip) to the handheld and run it.' -ForegroundColor Gray
exit 0
