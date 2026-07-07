#requires -Version 5
<#
    LiteRemote — satu perintah: build + uji loopback + (opsional) uji remote ke VM.

    Pakai:
        pwsh -File go.ps1                      # build + loopback test (127.0.0.1)
        pwsh -File go.ps1 -Remote 192.168.1.10 # sekaligus tes konektivitas ke VM
        pwsh -File go.ps1 -Password "rahasia"  # set password host otomatis (loopback)

    Skrip ini men-set password host, menjalankan Host (tray) lalu Viewer, dan mengecek jaringan.
    Jalankan di PC Windows dengan .NET 8 SDK terpasang.
#>
param(
    [string]$Password = "test1234",
    [string]$Remote = "",
    [int]$Port = 7443
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

function Step($m) { Write-Host "`n=== $m ===" -ForegroundColor Cyan }

# 1) Prasyarat
Step "Cek .NET 8 SDK"
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    Write-Host "dotnet tidak ditemukan. Install: winget install Microsoft.DotNet.SDK.8" -ForegroundColor Red
    exit 1
}
dotnet --version

# 2) Build
Step "Build (Release)"
dotnet build -c Release
if ($LASTEXITCODE -ne 0) { Write-Host "Build gagal." -ForegroundColor Red; exit 1 }

# 3) Set password host (Argon2id) lewat CLI headless
Step "Set access password host = '$Password'"
dotnet run --project src/RemoteDesktop.Host -c Release -- --set-password $Password
if ($LASTEXITCODE -ne 0) { Write-Host "Gagal set password." -ForegroundColor Red; exit 1 }

# 4) Jalankan Host (tray) di proses terpisah
Step "Jalankan Host (tray)"
$host_proc = Start-Process -PassThru -FilePath "dotnet" `
    -ArgumentList "run --project src/RemoteDesktop.Host -c Release"
Write-Host "Host PID $($host_proc.Id). Tunggu 8 detik untuk startup..."
Start-Sleep -Seconds 8

# 5) Tes konektivitas
$target = if ($Remote) { $Remote } else { "127.0.0.1" }
Step "Tes konektivitas ke ${target}:$Port"
$conn = Test-NetConnection $target -Port $Port -WarningAction SilentlyContinue
if ($conn.TcpTestSucceeded) {
    Write-Host "OK — ${target}:$Port dapat dijangkau." -ForegroundColor Green
} else {
    Write-Host "GAGAL menjangkau ${target}:$Port." -ForegroundColor Red
    if ($Remote) {
        Write-Host "Cek: Host jalan di VM? Firewall port $Port terbuka? VMware Bridged? IP benar?" -ForegroundColor Yellow
    } else {
        Write-Host "Host lokal mungkin belum siap / password belum di-set. Set via tray lalu ulangi." -ForegroundColor Yellow
    }
}

# 6) Jalankan Viewer
Step "Jalankan Viewer"
Write-Host "Di Viewer: tab 'By address' -> $target : $Port -> password -> Connect." -ForegroundColor Yellow
dotnet run --project src/RemoteDesktop.Client -c Release

# 7) Bersih-bersih
Step "Selesai — menutup Host"
if ($host_proc -and -not $host_proc.HasExited) { Stop-Process -Id $host_proc.Id -Force -ErrorAction SilentlyContinue }
