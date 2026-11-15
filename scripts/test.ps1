<#
 Single command to run all tests (blueprint §9). Fail-fast.
   1. cargo test              (schema migrations, pin ordering, recents, cascade delete, append logic)
   2. cargo build --release + copy sticky_core.dll (the C# FFI tests P/Invoke the real DLL)
   3. dotnet test win\StickyNotes.sln -c Release
 Usage: powershell -ExecutionPolicy Bypass -File .\scripts\test.ps1
#>
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    function Run([string]$cmd) {
        Write-Host ">> $cmd" -ForegroundColor Cyan
        Invoke-Expression $cmd
        if ($LASTEXITCODE -ne 0) { Write-Host "TEST RUN FAILED at: $cmd" -ForegroundColor Red; exit 1 }
    }

    # 1. Rust
    Run "cargo test --manifest-path Cargo.toml"

    # 2. Rebuild + stage the native DLL for the managed tests
    Run "cargo build --release --manifest-path Cargo.toml"
    $nativeDir = Join-Path $root 'win\StickyNotes.App\runtimes\win-x64\native'
    New-Item -ItemType Directory -Force -Path $nativeDir | Out-Null
    Copy-Item -Force (Join-Path $root 'target\release\sticky_core.dll') (Join-Path $nativeDir 'sticky_core.dll')

    # 3. .NET
    Run "dotnet test .\win\StickyNotes.sln -c Release"
}
finally {
    Pop-Location
}
