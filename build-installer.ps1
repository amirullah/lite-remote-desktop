# Builds the full LiteRemote package set locally, mirroring what CI does, into dist\:
#   LiteRemote-Setup-<ver>.exe        installer (viewer + host)
#   LiteRemote-Viewer-<ver>.zip       viewer portabel
#   LiteRemote-Host-<ver>.zip         host portabel
#   LiteRemote-Relay-win-<ver>.zip    relay (Windows)
#   LiteRemote-Relay-linux-<ver>.zip  relay (Linux, untuk VPS)
#
#   pwsh -File build-installer.ps1
#   pwsh -File build-installer.ps1 -Version 1.2.0
#   pwsh -File build-installer.ps1 -SkipRelay      # lebih cepat: setup + viewer/host saja
#
# Prasyarat: .NET 8 SDK dan Inno Setup 6 (winget install JRSoftware.InnoSetup).
# Output publish\ dan dist\ sengaja di-gitignore — artefak biner tidak ikut di-push.

param(
    [string]$Version = "1.2.0",
    [switch]$SkipRelay
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

function Find-Tool([string[]]$candidates, [string]$hint) {
    $found = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if (-not $found) { throw "Tidak ditemukan: $hint" }
    return $found
}

# x64 SDK dulu — beberapa PC punya host x86 di PATH yang tidak berisi SDK.
$dotnet = Find-Tool @(
    "$env:ProgramFiles\dotnet\dotnet.exe",
    (Get-Command dotnet -ErrorAction SilentlyContinue)?.Source
) ".NET SDK (winget install Microsoft.DotNet.SDK.8)"

$iscc = Find-Tool @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) "Inno Setup 6 (winget install JRSoftware.InnoSetup)"

# The viewer's in-app split-tunnel VPN ships a hidden OpenVPN engine. We fetch the OFFICIAL signed
# binaries (OpenVPN Inc. + WireGuard LLC), verify their Authenticode signatures, and cache them under
# third_party\ (gitignored — binaries never enter the source repo). Returns the folder with the engine.
function Ensure-OpenVpnEngine([string]$cache) {
    $need = @("openvpn.exe","libssl-3-x64.dll","libcrypto-3-x64.dll","libpkcs11-helper-1.dll",
              "vcruntime140.dll","wintun.dll","openvpnserv.exe")
    if (-not ($need | Where-Object { -not (Test-Path (Join-Path $cache $_)) })) {
        Write-Host "  engine cached."
        return $cache
    }
    New-Item -ItemType Directory -Force $cache | Out-Null
    $tmp = Join-Path $env:TEMP ("ovpn-" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force $tmp | Out-Null
    try {
        $msi = Join-Path $tmp "openvpn.msi"
        Write-Host "  downloading OpenVPN 2.7.5 (official)…"
        Invoke-WebRequest "https://build.openvpn.net/downloads/releases/OpenVPN-2.7.5-I001-amd64.msi" -OutFile $msi -UseBasicParsing -TimeoutSec 180
        $s = Get-AuthenticodeSignature $msi
        if ($s.Status -ne 'Valid' -or $s.SignerCertificate.Subject -notmatch 'OpenVPN') { throw "OpenVPN MSI signature not trusted ($($s.Status))." }
        $ext = Join-Path $tmp "ext"
        Start-Process msiexec.exe -ArgumentList "/a","`"$msi`"","TARGETDIR=`"$ext`"","/qn" -Wait
        $bin = Join-Path $ext "OpenVPN\bin"
        foreach ($n in "openvpn.exe","libssl-3-x64.dll","libcrypto-3-x64.dll","libpkcs11-helper-1.dll","vcruntime140.dll","openvpnserv.exe") {
            Copy-Item (Join-Path $bin $n) $cache -Force
        }
        $wz = Join-Path $tmp "wintun.zip"
        Write-Host "  downloading wintun (official)…"
        Invoke-WebRequest "https://www.wintun.net/builds/wintun-0.14.1.zip" -OutFile $wz -UseBasicParsing -TimeoutSec 120
        Expand-Archive $wz -DestinationPath (Join-Path $tmp "wt") -Force
        $wt = Get-ChildItem (Join-Path $tmp "wt") -Recurse -Filter wintun.dll | Where-Object { $_.DirectoryName -match "amd64" } | Select-Object -First 1
        $ws = Get-AuthenticodeSignature $wt.FullName
        if ($ws.Status -ne 'Valid' -or $ws.SignerCertificate.Subject -notmatch 'WireGuard') { throw "wintun.dll signature not trusted ($($ws.Status))." }
        Copy-Item $wt.FullName $cache -Force
        Write-Host "  engine assembled -> $cache"
    }
    finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }
    return $cache
}

Write-Host "== Publish viewer (client) =="
& $dotnet publish "$root\src\RemoteDesktop.Client\RemoteDesktop.Client.csproj" `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:Version=$Version `
    -o "$root\publish\client"
if ($LASTEXITCODE -ne 0) { throw "Publish client gagal" }

Write-Host "== Bundle OpenVPN engine (split-tunnel VPN) =="
$engine = Ensure-OpenVpnEngine "$root\third_party\openvpn"
New-Item -ItemType Directory -Force "$root\publish\client\openvpn" | Out-Null
Copy-Item "$engine\*" "$root\publish\client\openvpn\" -Force

Write-Host "== Publish host (tray) =="
& $dotnet publish "$root\src\RemoteDesktop.Host\RemoteDesktop.Host.csproj" `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=none `
    -o "$root\publish\host"
if ($LASTEXITCODE -ne 0) { throw "Publish host gagal" }

if (-not $SkipRelay) {
    Write-Host "== Publish relay (Windows & Linux) =="
    & $dotnet publish "$root\src\RemoteDesktop.Relay\RemoteDesktop.Relay.csproj" `
        -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=none `
        -o "$root\publish\relay-win"
    if ($LASTEXITCODE -ne 0) { throw "Publish relay-win gagal" }
    & $dotnet publish "$root\src\RemoteDesktop.Relay\RemoteDesktop.Relay.csproj" `
        -c Release -r linux-x64 --self-contained true `
        -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=none `
        -o "$root\publish\relay-linux"
    if ($LASTEXITCODE -ne 0) { throw "Publish relay-linux gagal" }
}

Write-Host "== Build installer (Inno Setup) =="
& $iscc "/DAppVersion=$Version" "$root\installer\LiteRemote.iss"
if ($LASTEXITCODE -ne 0) { throw "ISCC gagal" }

Write-Host "== Kemas ke dist\ =="
New-Item -ItemType Directory -Force "$root\dist" | Out-Null
Move-Item "$root\installer\output\LiteRemote-Setup-$Version.exe" "$root\dist\" -Force
Compress-Archive -Path "$root\publish\client\*" -DestinationPath "$root\dist\LiteRemote-Viewer-$Version.zip" -Force
Compress-Archive -Path "$root\publish\host\*"   -DestinationPath "$root\dist\LiteRemote-Host-$Version.zip" -Force
if (-not $SkipRelay) {
    Compress-Archive -Path "$root\publish\relay-win\*"   -DestinationPath "$root\dist\LiteRemote-Relay-win-$Version.zip" -Force
    Compress-Archive -Path "$root\publish\relay-linux\*" -DestinationPath "$root\dist\LiteRemote-Relay-linux-$Version.zip" -Force
}

Write-Host ""
Write-Host "Selesai — isi dist\:" -ForegroundColor Green
Get-ChildItem "$root\dist" | ForEach-Object { "  {0}  {1:N1} MB" -f $_.Name, ($_.Length / 1MB) }
