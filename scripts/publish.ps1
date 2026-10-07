# Сборка одного GnirehtetSquad.exe (self-contained, single-file) и архива для релиза.
# Использование: powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 [-Version 2.0.0]
param([string]$Version = "")

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\GnirehtetSquad\GnirehtetSquad.csproj"
$out = Join-Path $root "dist\GnirehtetSquad"

if (-not $Version) {
    $Version = ([xml](Get-Content $project -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}

if (Test-Path $out) { Remove-Item $out -Recurse -Force }

dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish завершился с кодом $LASTEXITCODE" }

$exe = Join-Path $out "GnirehtetSquad.exe"
if (-not (Test-Path $exe)) { throw "После сборки не найден $exe" }
Copy-Item (Join-Path $root "docs\README.txt") (Join-Path $out "README.txt")

# Архив для OTA: папка GnirehtetSquad\ с файлами верхнего уровня (формат как у 1.x)
$zip = Join-Path $root "dist\GnirehtetSquad-$Version-win64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $out -DestinationPath $zip

# Готовый exe — в корень проекта, как в остальных проектах
Copy-Item $exe (Join-Path $root "GnirehtetSquad.exe") -Force

Write-Host "Готово: $exe"
Write-Host "Архив:  $zip"
