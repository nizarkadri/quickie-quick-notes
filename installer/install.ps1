<#
 Installs StickyNotes for the CURRENT user (no admin required).

   powershell -ExecutionPolicy Bypass -File .\install.ps1
   powershell -ExecutionPolicy Bypass -File .\install.ps1 -InstallDir "D:\Apps\StickyNotes" -Launch

 Uninstall:  powershell -ExecutionPolicy Bypass -File .\uninstall.ps1
#>
[CmdletBinding()]
param(
    [string]$InstallDir,
    [switch]$Launch
)
$ErrorActionPreference = 'Stop'

$Version      = '0.1.0'
$AppName      = 'StickyNotes'
$DisplayName  = 'StickyNotes'
$Publisher    = 'StickyNotes'
$ExeName      = 'StickyNotes.App.exe'
$UninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\StickyNotes'

if ([string]::IsNullOrWhiteSpace($InstallDir)) {
    $InstallDir = Join-Path $env:LOCALAPPDATA "Programs\$AppName"
}

$sourceDir = $PSScriptRoot                 # the extracted package folder
$startMenuDir = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
$shortcutPath = Join-Path $startMenuDir "$DisplayName.lnk"

Write-Host "Installing $DisplayName $Version" -ForegroundColor Cyan
Write-Host "  from : $sourceDir"
Write-Host "  to   : $InstallDir"
Write-Host ""

# --- guard: don't nest the install inside the extracted package -----------------
$fullSource = (Resolve-Path $sourceDir).Path.TrimEnd('\')
$fullTarget = [System.IO.Path]::GetFullPath($InstallDir).TrimEnd('\')
if ($fullTarget.StartsWith($fullSource + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "InstallDir must not be inside the extracted package folder ($fullSource)."
}

# --- stop any running instance so files aren't locked ---------------------------
Get-Process -Name 'StickyNotes.App' -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "  stopping running instance (PID $($_.Id))" -ForegroundColor Yellow
    Stop-Process -Id $_.Id -Force
    Start-Sleep -Milliseconds 500
}

# --- copy payload --------------------------------------------------------------
if (-not (Test-Path (Join-Path $sourceDir $ExeName))) {
    throw "$ExeName not found in $sourceDir - extract the whole ZIP first, then run install.ps1 from that folder."
}

Write-Host "  copying files..." -ForegroundColor DarkGray
if (Test-Path $InstallDir) { Remove-Item -Recurse -Force $InstallDir }
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null

Get-ChildItem -Path $sourceDir -Force | Where-Object {
    $_.Name -notin @('install.ps1', 'README.txt')
} | ForEach-Object {
    Copy-Item -Path $_.FullName -Destination $InstallDir -Recurse -Force
}

$exePath = Join-Path $InstallDir $ExeName
if (-not (Test-Path $exePath)) { throw "Copy failed - $exePath missing." }

# --- Start Menu shortcut -------------------------------------------------------
Write-Host "  creating Start Menu shortcut..." -ForegroundColor DarkGray
New-Item -ItemType Directory -Force -Path $startMenuDir | Out-Null
$ws = New-Object -ComObject WScript.Shell
$lnk = $ws.CreateShortcut($shortcutPath)
$lnk.TargetPath       = $exePath
$lnk.WorkingDirectory = $InstallDir
$lnk.Description      = "$DisplayName $Version - auto-hiding sticky notes"
$lnk.Save()

# --- Add/Remove Programs entry -------------------------------------------------
Write-Host "  registering uninstall entry..." -ForegroundColor DarkGray
$uninstallExe = Join-Path $InstallDir 'uninstall.ps1'
$uninstallCmd = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$uninstallExe`""
New-Item -Path $UninstallKey -Force | Out-Null
Set-ItemProperty -Path $UninstallKey -Name 'DisplayName'     -Value $DisplayName
Set-ItemProperty -Path $UninstallKey -Name 'DisplayVersion'  -Value $Version
Set-ItemProperty -Path $UninstallKey -Name 'Publisher'       -Value $Publisher
Set-ItemProperty -Path $UninstallKey -Name 'InstallLocation' -Value $InstallDir
Set-ItemProperty -Path $UninstallKey -Name 'DisplayIcon'     -Value $exePath
Set-ItemProperty -Path $UninstallKey -Name 'UninstallString' -Value $uninstallCmd
Set-ItemProperty -Path $UninstallKey -Name 'NoModify'        -Value 1 -Type DWord
Set-ItemProperty -Path $UninstallKey -Name 'NoRepair'        -Value 1 -Type DWord
Set-ItemProperty -Path $UninstallKey -Name 'EstimatedSize'   -Value ([int]((Get-ChildItem $InstallDir -Recurse -File | Measure-Object Length -Sum).Sum / 1KB)) -Type DWord

Write-Host ""
Write-Host "$DisplayName $Version installed." -ForegroundColor Green
Write-Host "  $InstallDir"
Write-Host ""
Write-Host "It runs in the tray: hover the top-centre edge of a monitor for ~0.2s to reveal the bar." -ForegroundColor DarkGray

if ($Launch) {
    Write-Host "Launching..." -ForegroundColor Cyan
    Start-Process -FilePath $exePath -WorkingDirectory $InstallDir
}