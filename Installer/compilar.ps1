# Publica AORUS Monitor (Release, autocontenido: no necesita .NET instalado) y genera el instalador.
# Uso: powershell -ExecutionPolicy Bypass -File Installer\compilar.ps1
# Resultado: Installer\salida\AorusMonitor-<versión>-instalador.exe
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

$raiz = Split-Path $PSScriptRoot -Parent
$proyecto = Join-Path $raiz 'AorusMonitor.csproj'
$publicar = Join-Path $raiz 'publicar'

$version = ([xml](Get-Content $proyecto -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No hay <Version> en $proyecto" }
Write-Host "Versión $version"

if (Test-Path $publicar) { Remove-Item $publicar -Recurse -Force }
& dotnet publish $proyecto -c Release -r win-x64 --self-contained true -o $publicar -p:DebugType=none -nologo
if ($LASTEXITCODE -ne 0) { throw "Ha fallado dotnet publish" }

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "No se encuentra Inno Setup 6 (winget install JRSoftware.InnoSetup)" }

& $iscc "/DMiVersion=$version" (Join-Path $PSScriptRoot 'AorusMonitor.iss')
if ($LASTEXITCODE -ne 0) { throw "Ha fallado Inno Setup" }

Get-ChildItem (Join-Path $PSScriptRoot 'salida') -Filter "*$version*.exe" |
    Select-Object Name, @{ n = 'MB'; e = { [Math]::Round($_.Length / 1MB, 1) } }
