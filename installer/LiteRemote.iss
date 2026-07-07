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
; Optional firewall rule for the host listener.
Filename: "netsh"; Parameters: "advfirewall firewall add rule name=""LiteRemote Host"" dir=in action=allow protocol=TCP localport=7443"; \
  Flags: runhidden; Tasks: hostfirewall
; Offer to launch the viewer after install.
Filename: "{app}\client\{#ClientExe}"; Description: "Launch LiteRemote viewer"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""LiteRemote Host"""; Flags: runhidden; RunOnceId: "DelFirewallRule"

[Code]
procedure KillProcess(const ExeName: string);
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM ' + ExeName, '',
       SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  // Close our own running instances so files can be replaced; ignore everything else.
  KillProcess('{#ClientExe}');
  KillProcess('{#HostExe}');
  Sleep(500);
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    KillProcess('{#ClientExe}');
    KillProcess('{#HostExe}');
    Sleep(500);
  end;
end;
