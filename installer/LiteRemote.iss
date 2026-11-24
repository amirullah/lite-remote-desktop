; Inno Setup script for LiteRemote — builds a single Setup.exe that installs both the
; viewer (LiteRemote) and the host tray app (LiteRemoteHost).
;
; Expects the published binaries to already exist:
;   publish\client\  -> LiteRemote.exe   (WPF viewer)
;   publish\host\     -> LiteRemoteHost.exe (tray host)
; The GitHub Actions workflow publishes both before invoking ISCC.

#define AppName "LiteRemote"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define Publisher "LiteRemote"
#define ClientExe "LiteRemote.exe"
#define HostExe "LiteRemoteHost.exe"

[Setup]
AppId={{E9C2B6A1-9F3D-4B7C-8E21-1A2B3C4D5E6F}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#Publisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputBaseFilename=LiteRemote-Setup-{#AppVersion}
OutputDir=output
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
UninstallDisplayIcon={app}\{#ClientExe}
PrivilegesRequired=admin
; Restart Manager keeps flagging unrelated processes (antivirus hooks like "McAfee Framework
; Host") as "using our files", stalling the install. We close our own processes explicitly in
; PrepareToInstall instead, so the detection is unnecessary.
CloseApplications=no
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut for the LiteRemote viewer"; GroupDescription: "Additional icons:"
Name: "hostautostart"; Description: "Start the LiteRemote Host automatically at Windows logon (on this PC, so it can be remoted)"; GroupDescription: "Host options:"; Flags: unchecked
; Checked by default: without this rule, direct (by-address) connections time out and users
; assume the app is broken. ID/relay mode works either way (outbound only).
Name: "hostfirewall"; Description: "Add a Windows Firewall rule to allow incoming host connections (port 7443)"; GroupDescription: "Host options:"

[Files]
; Paths are relative to this .iss file (installer/), while publish/ sits at the repo root.
Source: "..\publish\client\*"; DestDir: "{app}\client"; Flags: recursesubdirs ignoreversion
Source: "..\publish\host\*";   DestDir: "{app}\host";   Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\LiteRemote (Viewer)"; Filename: "{app}\client\{#ClientExe}"
Name: "{group}\LiteRemote Host";     Filename: "{app}\host\{#HostExe}"
Name: "{group}\Uninstall LiteRemote"; Filename: "{uninstallexe}"
Name: "{autodesktop}\LiteRemote";    Filename: "{app}\client\{#ClientExe}"; Tasks: desktopicon

[Registry]
; Optional host autostart at logon.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
  ValueName: "LiteRemoteHost"; ValueData: """{app}\host\{#HostExe}"""; Flags: uninsdeletevalue; Tasks: hostautostart

[Run]
; Optional firewall rule for the host listener. Always delete every existing rule with this name
; first (netsh deletes ALL rules matching the name), so reinstall/update never piles up duplicates;
; then add exactly one if the task is selected.
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""LiteRemote Host"""; \
  Flags: runhidden waituntilterminated; StatusMsg: "Configuring Windows Firewall..."
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""LiteRemote Host"" dir=in action=allow protocol=TCP localport=7443"; \
  Flags: runhidden waituntilterminated; Tasks: hostfirewall
; Offer to launch the viewer after install.
Filename: "{app}\client\{#ClientExe}"; Description: "Launch LiteRemote viewer"; Flags: nowait postinstall skipifsilent
; In-app update (/UPDATE=1, run silently by the viewer): reopen the viewer as the signed-in user, not elevated.
Filename: "{app}\client\{#ClientExe}"; Flags: nowait runasoriginaluser; Check: IsInAppUpdate

[UninstallRun]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""LiteRemote Host"""; Flags: runhidden waituntilterminated; RunOnceId: "DelFirewallRule"
; Remove the SYSTEM login-screen task, otherwise it keeps firing at boot against a deleted exe
; and a later reinstall's tray host would step aside for it.
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN LiteRemoteLogin /F"; Flags: runhidden; RunOnceId: "DelLoginTask"

[Code]
var
  HostWasRunning: Boolean;

procedure KillProcess(const ExeName: string);
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM ' + ExeName, '',
       SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

// Kill only the VPN engine bundled under {app}\client\openvpn — never the user's own OpenVPN / OpenVPN
// Connect processes elsewhere on the system (their tunnels must survive our install/uninstall).
procedure KillBundledVpn();
var
  ResultCode: Integer;
  Dir: String;
begin
  Dir := ExpandConstant('{app}\client\openvpn\');
  Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
       '-NoProfile -NonInteractive -Command "Get-Process openvpn,openvpnserv -ErrorAction SilentlyContinue | ' +
       'Where-Object { $_.Path -and $_.Path.StartsWith(''' + Dir + ''', [StringComparison]::OrdinalIgnoreCase) } | ' +
       'Stop-Process -Force -ErrorAction SilentlyContinue"',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

// Is the TRAY host running for the user who started Setup? The SYSTEM login daemon is the same exe but
// lives in session 0 ("Services") as SYSTEM — it must not count, or every upgrade would start a tray
// host the user never ran (the daemon itself is restarted via its scheduled task).
function IsUserProcessRunning(const ExeName: string): Boolean;
var
  ResultCode: Integer;
begin
  // tasklist | find → exit code 0 when the image is listed.
  Result := Exec(ExpandConstant('{cmd}'),
                 '/C tasklist /FI "IMAGENAME eq ' + ExeName + '" /FI "SESSIONNAME ne Services" /FI "USERNAME eq ' +
                 GetUserNameString + '" /NH | find /I "' + ExeName + '"',
                 '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

// Silent run started by the viewer's in-app updater.
function IsInAppUpdate(): Boolean;
begin
  Result := ExpandConstant('{param:UPDATE|0}') = '1';
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  // Remember whether the tray host was up, so an upgrade doesn't leave the PC unreachable on 7443.
  // In-app updates pass /RESTARTHOST=1 (the viewer checked its own session); manual installs probe.
  if IsInAppUpdate() then
    HostWasRunning := ExpandConstant('{param:RESTARTHOST|0}') = '1'
  else
    HostWasRunning := IsUserProcessRunning('{#HostExe}');
  // Close our own running instances so files can be replaced; ignore everything else.
  KillProcess('{#ClientExe}');
  KillProcess('{#HostExe}');
  // The bundled VPN engine holds openvpn\*.dll (e.g. libcrypto-3-x64.dll) open while a tunnel is up,
  // which otherwise fails the file replace with "Access is denied".
  KillBundledVpn();
  Sleep(700);
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    // PrepareToInstall killed the SYSTEM --daemon too; its task only triggers at boot, so restart it
    // now if login support was installed, or the PC stays unreachable on 7443 until reboot.
    if Exec(ExpandConstant('{sys}\schtasks.exe'), '/Query /TN LiteRemoteLogin', '',
            SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0) then
      Exec(ExpandConstant('{sys}\schtasks.exe'), '/Run /TN LiteRemoteLogin', '',
           SW_HIDE, ewNoWait, ResultCode);
    // Bring the tray host back (as the signed-in user, not elevated) if it was running before.
    if HostWasRunning then
      ExecAsOriginalUser(ExpandConstant('{app}\host\{#HostExe}'), '', ExpandConstant('{app}\host'),
                         SW_SHOWNORMAL, ewNoWait, ResultCode);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    KillProcess('{#ClientExe}');
    KillProcess('{#HostExe}');
    KillBundledVpn();
    Sleep(700);
  end;
end;
