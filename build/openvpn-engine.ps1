# Shared by build-installer.ps1 (local) and .github/workflows/build.yml (CI) so both bundle the
# SAME split-tunnel VPN engine. Dot-source it, then call Ensure-OpenVpnEngine <cacheFolder>.
# Fetches the OFFICIAL signed binaries (OpenVPN Inc. + WireGuard LLC), verifies their Authenticode
# signatures, and assembles them in $cache (binaries never enter the source repo). Returns $cache.
function Ensure-OpenVpnEngine([string]$cache) {
    $need = @("openvpn.exe","libssl-3-x64.dll","libcrypto-3-x64.dll","libpkcs11-helper-1.dll",
              "vcruntime140.dll","wintun.dll","openvpnserv.exe","tapctl.exe")
    if (-not ($need | Where-Object { -not (Test-Path (Join-Path $cache $_)) })) {
        Write-Host "  engine cached."
        return $cache
    }
    New-Item -ItemType Directory -Force $cache | Out-Null
    $tmpRoot = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { $env:TEMP }
    $tmp = Join-Path $tmpRoot ("ovpn-" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force $tmp | Out-Null
    try {
        $msi = Join-Path $tmp "openvpn.msi"
        Write-Host "  downloading OpenVPN 2.7.5 (official)…"
        Invoke-WebRequest "https://build.openvpn.net/downloads/releases/OpenVPN-2.7.5-I001-amd64.msi" -OutFile $msi -UseBasicParsing -TimeoutSec 300
        $s = Get-AuthenticodeSignature $msi
        if ($s.Status -ne 'Valid' -or $s.SignerCertificate.Subject -notmatch 'OpenVPN') { throw "OpenVPN MSI signature not trusted ($($s.Status))." }
        $ext = Join-Path $tmp "ext"
        $p = Start-Process msiexec.exe -ArgumentList "/a","`"$msi`"","TARGETDIR=`"$ext`"","/qn" -Wait -PassThru
        if ($p.ExitCode -ne 0) { throw "msiexec administrative extract failed (exit $($p.ExitCode))." }
        $bin = Join-Path $ext "OpenVPN\bin"
        foreach ($n in "openvpn.exe","libssl-3-x64.dll","libcrypto-3-x64.dll","libpkcs11-helper-1.dll","vcruntime140.dll","openvpnserv.exe","tapctl.exe") {
            Copy-Item (Join-Path $bin $n) $cache -Force -ErrorAction Stop
        }
        $wz = Join-Path $tmp "wintun.zip"
        Write-Host "  downloading wintun 0.14.1 (official)…"
        Invoke-WebRequest "https://www.wintun.net/builds/wintun-0.14.1.zip" -OutFile $wz -UseBasicParsing -TimeoutSec 180
        Expand-Archive $wz -DestinationPath (Join-Path $tmp "wt") -Force
        $wt = Get-ChildItem (Join-Path $tmp "wt") -Recurse -Filter wintun.dll | Where-Object { $_.DirectoryName -match "amd64" } | Select-Object -First 1
        if (-not $wt) { throw "wintun.dll (amd64) not found in archive." }
        $ws = Get-AuthenticodeSignature $wt.FullName
        if ($ws.Status -ne 'Valid' -or $ws.SignerCertificate.Subject -notmatch 'WireGuard') { throw "wintun.dll signature not trusted ($($ws.Status))." }
        Copy-Item $wt.FullName $cache -Force
        Write-Host "  engine assembled -> $cache"
    }
    finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }
    return $cache
}
