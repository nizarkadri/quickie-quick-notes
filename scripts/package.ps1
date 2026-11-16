<#
 Builds a distributable StickyNotes package (self-contained win-x64 + ZIP).

 Self-contained => testers do NOT need the .NET 8 Desktop Runtime installed.

 Usage:  powershell -ExecutionPolicy Bypass -File .\scripts\package.ps1
 Output: dist\StickyNotes-<version>-win-x64.zip   (and dist\publish for inspection)
#>
[CmdletBinding()]
param(
    [string]$Version = '0.1.0',
    [string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    function Run([string]$cmd) {
        Write-Host ">> $cmd" -ForegroundColor Cyan
        Invoke-Expression $cmd
        if ($LASTEXITCODE -ne 0) {
            Write-Host "PACKAGE FAILED at: $cmd" -ForegroundColor Red
            exit 1
        }
    }

    $dist    = Join-Path $root 'dist'
    $publish = Join-Path $dist 'publish'
    $stage   = Join-Path $dist ('StickyNotes-' + $Version + '-win-x64')

    if (Test-Path $dist) { Remove-Item -Recurse -Force $dist }
    New-Item -ItemType Directory -Force -Path $publish | Out-Null

    Write-Host "== 1/5  cargo build --release ==" -ForegroundColor Green
    Run "cargo build --release --manifest-path Cargo.toml"

    Write-Host "== 2/5  dotnet publish (self-contained win-x64) ==" -ForegroundColor Green
    Run "dotnet publish .\win\StickyNotes.App\StickyNotes.App.csproj -c $Configuration -r win-x64 --self-contained true -p:PublishTrimmed=false -p:PublishSingleFile=false -p:DebugType=none -o `"$publish`""

    Write-Host "== 3/5  stage native core dll ==" -ForegroundColor Green
    # The app P/Invokes "sticky_core"; it must sit next to the exe.
    Copy-Item -Force (Join-Path $root 'target\release\sticky_core.dll') (Join-Path $publish 'sticky_core.dll')

    Write-Host "== 4/5  assemble staging folder ==" -ForegroundColor Green
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    Get-ChildItem -Path $publish | ForEach-Object {
        Copy-Item -Path $_.FullName -Destination $stage -Recurse -Force
    }
    foreach ($f in @('install.ps1', 'uninstall.ps1')) {
        Copy-Item -Force (Join-Path $root "installer\$f") (Join-Path $stage $f)
    }

    $readme = @"
StickyNotes $Version (win-x64, self-contained)
================================================

QUICKEST START (no install, no admin)
  1. Extract this whole ZIP to a folder.
  2. Double-click StickyNotes.App.exe.
  3. Look for the StickyNotes icon in the system tray (click ^ if it is hidden).

INSTALL (adds Start Menu shortcut + Add/Remove Programs entry)
  powershell -ExecutionPolicy Bypass -File .\install.ps1
  powershell -ExecutionPolicy Bypass -File .\install.ps1 -Launch

UNINSTALL
  powershell -ExecutionPolicy Bypass -File .\uninstall.ps1
  (your notes are kept unless you pass -PurgeData)

HOW TO USE
  * Move the mouse to the TOP-CENTRE edge of a monitor and hold for ~0.2s.
    A bar slides down: [pinned items | Groups v] | [+] | [recents].
  * Hover or click a group to open its vertical note list.
  * "+" opens a quick note: Ctrl+Enter or click outside saves, Esc discards.
    It can save standalone, into a group, or APPEND to an existing note.
  * Click any note to open it beside the list (it will not steal focus).
    " + New Note" in a group creates and focuses a new note.
  * Notes autosave 350ms after you stop typing. Clicking outside a note gives
    you a 5 second grace period before it closes ( handy for copy/paste ).
  * Right-click a chip to pin/unpin. Right-click the tray icon to Exit.

WHERE YOUR DATA LIVES
  %LOCALAPPDATA%\StickyNotes\sticky.db      (SQLite; delete it to reset)
  %LOCALAPPDATA%\Programs\StickyNotes       (installed binaries)

REQUIREMENTS
  Windows 10 21H2+ or Windows 11 (x64). No .NET or Rust install needed.
  To BUILD from source instead: scripts\build.ps1 and scripts\test.ps1.

TROUBLESHOOTING
  * Nothing happens on launch? It is a tray app - check the tray overflow (^).
  * Bar does not appear? Hover the very top edge, horizontally centred, and
    hold still for a moment. Full-screen games and F11 browsers suppress it
    by design; a maximised normal window does not.
  * Antivirus/SmartScreen may warn about an unsigned exe - choose "More info"
    then "Run anyway".
"@

    Set-Content -Path (Join-Path $stage 'README.txt') -Value $readme -Encoding UTF8

    Write-Host "== 5/5  zip ==" -ForegroundColor Green
    $zip = Join-Path $dist ('StickyNotes-' + $Version + '-win-x64.zip')
    Compress-Archive -Path $stage -DestinationPath $zip -CompressionLevel Optimal

    $sizeMb = [Math]::Round((Get-Item $zip).Length / 1MB, 1)
    $files  = (Get-ChildItem $stage -Recurse -File).Count

    Write-Host ""
    Write-Host "PACKAGE BUILT" -ForegroundColor Green
    Write-Host "  zip     : $zip"
    Write-Host "  size    : $sizeMb MB  ($files files)"
    Write-Host "  version : $Version   runtime: self-contained win-x64 (no .NET install needed)"
    Write-Host ""
    Write-Host "Ship the ZIP as-is: testers extract it and run StickyNotes.App.exe," -ForegroundColor DarkGray
    Write-Host "or run install.ps1 for a Start Menu shortcut + Add/Remove Programs entry." -ForegroundColor DarkGray
}
finally {
    Pop-Location
}