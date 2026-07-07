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

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut for the LiteRemote viewer"; GroupDescription: "Additional icons:"
Name: "hostautostart"; Description: "Start the LiteRemote Host automatically at Windows logon (on this PC, so it can be remoted)"; GroupDescription: "Host options:"; Flags: unchecked
Name: "hostfirewall"; Description: "Add a Windows Firewall rule to allow incoming host connections (port 7443)"; GroupDescription: "Host options:"; Flags: unchecked

[Files]
Source: "publish\client\*"; DestDir: "{app}\client"; Flags: recursesubdirs ignoreversion
Source: "publish\host\*";   DestDir: "{app}\host";   Flags: recursesubdirs ignoreversion

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
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""LiteRemote Host"""; Flags: runhidden
