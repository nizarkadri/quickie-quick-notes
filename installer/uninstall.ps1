<#
 Uninstalls StickyNotes for the CURRENT user.

   powershell -ExecutionPolicy Bypass -File .\uninstall.ps1
   powershell -ExecutionPolicy Bypass -File .\uninstall.ps1 -PurgeData   # also delete notes

 By default your notes (%LOCALAPPDATA%\StickyNotes\sticky.db) are KEPT.
#>
[CmdletBinding()]
param(
    [switch]$PurgeData
)
$ErrorActionPreference = 'SilentlyContinue'

$InstallDir   = Join-Path $env:LOCALAPPDATA 'Programs\StickyNotes'
$DataDir      = Join-Path $env:LOCALAPPDATA 'StickyNotes'
$UninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\StickyNotes'
$shortcutPath = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\StickyNotes.lnk'

Write-Host "Uninstalling StickyNotes" -ForegroundColor Cyan

Write-Host "  stopping any running instance..." -ForegroundColor DarkGray
Get-Process -Name 'StickyNotes.App' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

Write-Host "  removing Start Menu shortcut..." -ForegroundColor DarkGray
Remove-Item -Path $shortcutPath -Force -ErrorAction SilentlyContinue

Write-Host "  removing registry entry..." -ForegroundColor DarkGray
Remove-Item -Path $UninstallKey -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "  deleting $InstallDir ..." -ForegroundColor DarkGray
Remove-Item -Path $InstallDir -Recurse -Force -ErrorAction SilentlyContinue

if ($PurgeData) {
    Write-Host "  deleting your notes ($DataDir)..." -ForegroundColor Yellow
    Remove-Item -Path $DataDir -Recurse -Force -ErrorAction SilentlyContinue
} elseif (Test-Path $DataDir) {
    Write-Host ""
    Write-Host "Your notes were kept in $DataDir" -ForegroundColor DarkGray
    Write-Host "(re-install to pick them up again, or re-run with -PurgeData to delete them)" -ForegroundColor DarkGray
}

Write-Host ""
Write-Host "StickyNotes uninstalled." -ForegroundColor Green