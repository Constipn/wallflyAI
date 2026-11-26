; Wallfly for Windows - per-user installer (Inno Setup 6.3 or newer).
; Packs dist\Wallfly\ exactly as `build.ps1 -Publish` leaves it: Wallfly.exe, "Wallfly Detector.exe", wallfly-mcp.exe,
; one shared self-contained .NET runtime, WallflyAudio.dll, whisper\, models\, third-party\ and VERSION.txt.
; No administrator rights: installs to %LOCALAPPDATA%\Programs\Wallfly and writes only HKCU.
; The meeting library (%LOCALAPPDATA%\Wallfly or the location chosen in the app) is never touched, not even on uninstall.
;
; Build (see README.md):  ISCC.exe windows\installer\wallfly.iss            (version from dist\Wallfly\VERSION.txt)
;                         ISCC.exe /DAppVersion=1.2.3 windows\installer\wallfly.iss

#define DistDir AddBackslash(SourcePath) + "..\dist\Wallfly"

#ifndef AppVersion
  #define VersionFile FileOpen(DistDir + "\VERSION.txt")
  #if VersionFile
    #define AppVersion Trim(FileRead(VersionFile))
    #expr FileClose(VersionFile)
  #else
    #define AppVersion "0.1.0"
  #endif
#endif

[Setup]
; Never change AppId: it links upgrades and the uninstaller to earlier installations.
AppId={{6F1D2C3B-8E4A-4B7C-9D21-5A0E7F3C9B12}
AppName=Wallfly
AppVersion={#AppVersion}
AppVerName=Wallfly {#AppVersion}
AppPublisher=Wallfly
DefaultDirName={localappdata}\Programs\Wallfly
DisableDirPage=auto
DefaultGroupName=Wallfly
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#DistDir}\..\installer
OutputBaseFilename=Wallfly-Setup-{#AppVersion}
UninstallDisplayIcon={app}\Wallfly.exe
SetupIconFile=..\Assets\Wallfly.ico
UninstallDisplayName=Wallfly
Compression=lzma2/max
SolidCompression=yes
LZMANumBlockThreads=4
WizardStyle=modern
; Restart Manager asks running Wallfly / Wallfly Detector / wallfly-mcp processes to close before files are replaced.
CloseApplications=yes
CloseApplicationsFilter=*.exe,*.dll
RestartApplications=no
SetupLogging=yes
; Authenticode (README.md): define a sign tool named "wallfly" in ISCC (/Swallfly=...) and uncomment.
;SignTool=wallfly
;SignedUninstaller=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "german"; MessagesFile: "compiler:Languages\German.isl"

[CustomMessages]
english.DetectorAutostart=Start Wallfly Detector at sign-in (reminds you to take notes when a Teams call starts)
german.DetectorAutostart=Wallfly Detector bei der Anmeldung starten (erinnert bei Teams-Anrufen an Notizen)
english.AutostartGroup=Teams:
german.AutostartGroup=Teams:

[Tasks]
Name: "detectorautostart"; Description: "{cm:DetectorAutostart}"; GroupDescription: "{cm:AutostartGroup}"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#DistDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Wallfly"; Filename: "{app}\Wallfly.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Wallfly"; Filename: "{app}\Wallfly.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
; Same value the app writes from its settings toggle (Wallfly.Teams DetectorAutostart): HKCU Run "WallflyDetector".
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "WallflyDetector"; ValueData: """{app}\Wallfly Detector.exe"" --autostart"; Flags: uninsdeletevalue; Tasks: detectorautostart

[Run]
Filename: "{app}\Wallfly Detector.exe"; Parameters: "--autostart"; WorkingDir: "{app}"; Flags: nowait skipifsilent; Tasks: detectorautostart
Filename: "{app}\Wallfly.exe"; WorkingDir: "{app}"; Description: "{cm:LaunchProgram,Wallfly}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; The helper and MCP servers started by an AI client have no unsaved state. Wallfly itself is closed through the
; Restart Manager prompt instead, so a running recording is never killed.
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM ""Wallfly Detector.exe"""; Flags: runhidden; RunOnceId: "StopDetector"
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM wallfly-mcp.exe"; Flags: runhidden; RunOnceId: "StopMcp"

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  ApprovedKey = 'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run';
  DetectorValue = 'WallflyDetector';

{ The app can also register the helper itself (first start, settings toggle). On uninstall remove the Run entry and
  its Task Manager state, but only when it points into this installation. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Command: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    if RegQueryStringValue(HKCU, RunKey, DetectorValue, Command) then
    begin
      if Pos(Lowercase(ExpandConstant('{app}')), Lowercase(Command)) > 0 then
      begin
        RegDeleteValue(HKCU, RunKey, DetectorValue);
        RegDeleteValue(HKCU, ApprovedKey, DetectorValue);
      end;
    end;
  end;
end;
