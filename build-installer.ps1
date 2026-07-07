# Builds the LiteRemote installer locally, mirroring what CI does, and drops the result in dist\.
#
#   pwsh -File build-installer.ps1                # -> dist\LiteRemote-Setup-<versi>.exe
#   pwsh -File build-installer.ps1 -Version 1.2.0
#
# Prasyarat: .NET 8 SDK dan Inno Setup 6 (winget install JRSoftware.InnoSetup).
# Output publish\ dan dist\ sengaja di-gitignore — artefak biner tidak ikut di-push.

param(
    [string]$Version = "1.2.0"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

function Find-Tool([string[]]$candidates, [string]$hint) {
    $found = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if (-not $found) { throw "Tidak ditemukan: $hint" }
    return $found
}

$dotnet = Find-Tool @(
    (Get-Command dotnet -ErrorAction SilentlyContinue)?.Source,
    "$env:ProgramFiles\dotnet\dotnet.exe"
) ".NET SDK (winget install Microsoft.DotNet.SDK.8)"

$iscc = Find-Tool @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) "Inno Setup 6 (winget install JRSoftware.InnoSetup)"

Write-Host "== Publish viewer (client) =="
& $dotnet publish "$root\src\RemoteDesktop.Client\RemoteDesktop.Client.csproj" `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=none `
    -o "$root\publish\client"
if ($LASTEXITCODE -ne 0) { throw "Publish client gagal" }

Write-Host "== Publish host (tray) =="
& $dotnet publish "$root\src\RemoteDesktop.Host\RemoteDesktop.Host.csproj" `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=none `
    -o "$root\publish\host"
if ($LASTEXITCODE -ne 0) { throw "Publish host gagal" }

Write-Host "== Build installer (Inno Setup) =="
& $iscc "/DAppVersion=$Version" "$root\installer\LiteRemote.iss"
if ($LASTEXITCODE -ne 0) { throw "ISCC gagal" }

New-Item -ItemType Directory -Force "$root\dist" | Out-Null
$setup = Get-ChildItem "$root\installer\output\LiteRemote-Setup-$Version.exe"
Move-Item $setup.FullName "$root\dist\" -Force

Write-Host ""
Write-Host "Selesai: dist\$($setup.Name)" -ForegroundColor Green
