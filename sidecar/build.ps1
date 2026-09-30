# Build the VETT sidecar binaries on Windows without `make`.
#
# Why: GNU Make isn't bundled with stock Windows, and asking users to
# install MSYS2 / Git Bash / Chocolatey just to rebuild a sidecar is
# friction. This script reproduces the targets in Makefile using
# native PowerShell so anyone with Go installed can run it.
#
# Usage (from sidecar/):
#   .\build.ps1                 # builds linux + windows + both macOS
#   .\build.ps1 -Target windows # one target
#   .\build.ps1 -All            # explicit all (same as default)

[CmdletBinding()]
param(
    [ValidateSet('all', 'linux', 'windows', 'darwin-amd64', 'darwin-arm64')]
    [string]$Target = 'all',
    [switch]$All
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not (Get-Command go -ErrorAction SilentlyContinue)) {
    Write-Error "Go isn't on PATH. Install from https://go.dev/dl/ first."
    exit 1
}

# Make sure bin/ exists (Go will refuse to write into a non-existent dir).
$binDir = Join-Path $PSScriptRoot 'bin'
if (-not (Test-Path $binDir)) { New-Item -ItemType Directory -Path $binDir | Out-Null }

function Build-Sidecar {
    param([string]$GoOS, [string]$GoArch, [string]$Output)
    Write-Host "[$GoOS/$GoArch] building $Output..." -ForegroundColor Cyan
    $env:GOOS = $GoOS
    $env:GOARCH = $GoArch
    & go build -o $Output ./cmd/vett-sidecar
    if ($LASTEXITCODE -ne 0) { Write-Error "Build failed: $GoOS/$GoArch"; exit 1 }
    Write-Host "  ok: $Output ($((Get-Item $Output).Length) bytes)" -ForegroundColor Green
}

$buildAll = $All -or $Target -eq 'all'

try {
    if ($buildAll -or $Target -eq 'linux')        { Build-Sidecar 'linux'   'amd64' 'bin/vett-sidecar-linux-amd64' }
    if ($buildAll -or $Target -eq 'windows')      { Build-Sidecar 'windows' 'amd64' 'bin/vett-sidecar-windows-amd64.exe' }
    if ($buildAll -or $Target -eq 'darwin-amd64') { Build-Sidecar 'darwin'  'amd64' 'bin/vett-sidecar-darwin-amd64' }
    if ($buildAll -or $Target -eq 'darwin-arm64') { Build-Sidecar 'darwin'  'arm64' 'bin/vett-sidecar-darwin-arm64' }
}
finally {
    Remove-Item Env:\GOOS  -ErrorAction SilentlyContinue
    Remove-Item Env:\GOARCH -ErrorAction SilentlyContinue
}

# The .NET CLI looks for them in bin/ at the repo root (and packs them from there).
$distDir = Join-Path $PSScriptRoot '..\bin'
if (-not (Test-Path $distDir)) { New-Item -ItemType Directory -Path $distDir | Out-Null }
Copy-Item (Join-Path $binDir 'vett-sidecar-*') $distDir -Force

Write-Host "`nDone. Binaries in $binDir, copied to $distDir" -ForegroundColor Green
