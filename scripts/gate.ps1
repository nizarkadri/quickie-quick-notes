<#
 Runs every AUTOMATED test for tasks 1..N (regression included) and prints the manual items still required.
 Usage:  powershell -ExecutionPolicy Bypass -File .\scripts\gate.ps1 -Task 3
 Task 10 = final end-to-end gate (all automated tests).
 A task is complete only when this prints "AUTOMATED GATE PASSED" AND the matching boxes in
 docs\MANUAL_ACCEPTANCE.md are ticked.
#>
param([Parameter(Mandatory = $true)][ValidateRange(1, 10)][int]$Task)
$ErrorActionPreference = 'Stop'

function Run([string]$cmd) {
    Write-Host ">> $cmd" -ForegroundColor Cyan
    Invoke-Expression $cmd
    if ($LASTEXITCODE -ne 0) { Write-Host "GATE FAILED at: $cmd" -ForegroundColor Red; exit 1 }
}

if ($Task -eq 1) {
    Run ".\scripts\build.ps1"
    if (-not (Select-String -Path .\win\StickyNotes.App\app.manifest -Pattern 'PerMonitorV2' -Quiet)) {
        Write-Host "GATE FAILED: app.manifest must declare PerMonitorV2" -ForegroundColor Red; exit 1
    }
    if (-not (Test-Path .\win\StickyNotes.App\bin\Release\net8.0-windows\sticky_core.dll)) {
        Write-Host "GATE FAILED: sticky_core.dll was not copied next to the exe" -ForegroundColor Red; exit 1
    }
}

if ($Task -ge 2) { Run "cargo test --manifest-path Cargo.toml" }

if ($Task -ge 3) {
    Run ".\scripts\build.ps1"
    # Tasks with automated C# tests: 3 (FFI), 4 (positioning), 5 (state machine), 7 (note session), 8 (quick note)
    $withTests = @(3, 4, 5, 7, 8) | Where-Object { $_ -le $Task }
    $filter = ($withTests | ForEach-Object { "Task=$_" }) -join "|"
    Run "dotnet test .\win\StickyNotes.sln -c Release --no-build --filter `"$filter`""
}

Write-Host ""
Write-Host "AUTOMATED GATE PASSED for task $Task" -ForegroundColor Green
Write-Host "Now complete the Task $Task section of docs\MANUAL_ACCEPTANCE.md before marking the feature done." -ForegroundColor Yellow
if ($Task -in 6, 9) {
    Write-Host "(Task $Task has no automated tests of its own - the manual checklist IS its gate.)" -ForegroundColor Yellow
}
