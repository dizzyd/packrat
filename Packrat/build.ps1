# ─── Build & Package ───
# Builds the mod in Release mode and packages it into a distributable .zip.
# Usage: right-click → "Run with PowerShell"  or  .\build.ps1

$ErrorActionPreference = "Stop"

$projectDir  = $PSScriptRoot
$modId       = "Packrat"
$version     = (Get-Content "$projectDir\modinfo.json" | ConvertFrom-Json).version
$zipName     = "$modId" + "_" + "$version.zip"
$outDir      = "$projectDir\bin\Release\Mods\mod"
$zipPath     = "$projectDir\$zipName"

Write-Host "==> Building $modId $version ..." -ForegroundColor Cyan

dotnet build "$projectDir\Packrat.csproj" -c Release --nologo -v minimal
if ($LASTEXITCODE -ne 0) { Write-Error "Build failed."; exit 1 }

Write-Host "==> Packaging $zipName ..." -ForegroundColor Cyan

# Remove previous zip if it exists
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

# Files to include in the zip
$include = @(
    "$projectDir\modinfo.json",
    "$outDir\Packrat.dll",
    "$projectDir\modicon.png",
    "$projectDir\assets"
)

# Only add files that actually exist
$existing = $include | Where-Object { Test-Path $_ }

Compress-Archive -Path $existing -DestinationPath $zipPath

Write-Host "==> Done! Output: $zipPath" -ForegroundColor Green
