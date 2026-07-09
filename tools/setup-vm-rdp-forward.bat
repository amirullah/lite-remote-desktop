@echo off
setlocal
REM ============================================================
REM  LiteRemote - teruskan RDP VM (NAT/host-only) lewat PC host
REM  Jalankan skrip INI di PC HOST tempat VM berada (mis. 10.28.76.92).
REM  Klik-kanan -> "Run as administrator" (atau skrip minta izin sendiri).
REM  Setelah selesai: dari LiteRemote konek ke  <IP-PC-INI>:13389  (via VPN).
REM  Tidak butuh VMware config, tidak butuh router.
REM ============================================================

REM --- minta hak administrator bila belum ---
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Meminta hak administrator...
    powershell -NoProfile -Command "Start-Process '%~f0' -Verb RunAs"
    exit /b
)

set LPORT=13389
echo.
echo   PC ini akan meneruskan koneksi masuk di port %LPORT% ke RDP (3389) milik VM.
echo.
set /p VMIP=Masukkan IP internal VM (contoh 192.168.163.128):
if "%VMIP%"=="" (echo IP kosong - dibatalkan. & pause & exit /b)

echo.
echo Memasang forward:  0.0.0.0:%LPORT%  --^>  %VMIP%:3389 ...
netsh interface portproxy delete v4tov4 listenport=%LPORT% listenaddress=0.0.0.0 >nul 2>&1
netsh interface portproxy add    v4tov4 listenport=%LPORT% listenaddress=0.0.0.0 connectport=3389 connectaddress=%VMIP%
netsh advfirewall firewall delete rule name="LiteRemote VM RDP %LPORT%" >nul 2>&1
netsh advfirewall firewall add    rule name="LiteRemote VM RDP %LPORT%" dir=in action=allow protocol=TCP localport=%LPORT% >nul

echo.
echo === SELESAI. Forward aktif: ===
netsh interface portproxy show v4tov4
echo.
echo Dari LiteRemote (PC Anda, VPN menyala) isi Host:
echo     ^<IP-PC-INI^>:%LPORT%        (mis. 10.28.76.92:%LPORT%)
echo     User / Password = akun Windows di dalam VM
echo.
echo Catatan: agar tak rusak saat VM restart, beri VM IP STATIS (lihat panduan).
echo Untuk MENGHAPUS forward nanti, jalankan:
echo     netsh interface portproxy delete v4tov4 listenport=%LPORT% listenaddress=0.0.0.0
echo.
pause
