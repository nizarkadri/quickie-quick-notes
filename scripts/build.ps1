<#
 Single clean-checkout build (blueprint §9). Fail-fast.
   1. cargo build --release
   2. copy target\release\sticky_core.dll -> win\StickyNotes.App\runtimes\win-x64\native\
   3. dotnet build win\StickyNotes.sln -c Release
 Usage: powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1
#>
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    function Run([string]$cmd) {
        Write-Host ">> $cmd" -ForegroundColor Cyan
        Invoke-Expression $cmd
        if ($LASTEXITCODE -ne 0) { Write-Host "BUILD FAILED at: $cmd" -ForegroundColor Red; exit 1 }
    }

    # 1. Rust core
    Run "cargo build --release --manifest-path Cargo.toml"

    # 2. Stage the native DLL where the .csproj picks it up (flattened next to the exe)
    $nativeDir = Join-Path $root 'win\StickyNotes.App\runtimes\win-x64\native'
    New-Item -ItemType Directory -Force -Path $nativeDir | Out-Null
    Copy-Item -Force (Join-Path $root 'target\release\sticky_core.dll') (Join-Path $nativeDir 'sticky_core.dll')

    # 3. Managed app + tests
    Run "dotnet build .\win\StickyNotes.sln -c Release"
}
finally {
    Pop-Location
}
