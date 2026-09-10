; StealthPDF installer (Inno Setup 6)
;
; Per-user install, no administrator rights. Compile with the publish tree already built:
;   iscc build\StealthPDF.iss /DAppVersion=2.0.0 /DSourceDir=..\bin\Release\net48\publish
;
; The compiled setup EXE must be signed AFTER this runs - signing anything earlier and then
; repackaging would invalidate it. See .github/workflows/release.yml for the order.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\bin\Release\net48\publish"
#endif

#define AppName     "StealthPDF"
#define AppPublisher "StealthPDF"
#define AppURL      "https://stealthpdf.com"
#define AppExe      "StealthPDF.exe"

[Setup]
; Stable across versions - changing it would orphan existing installs in Add/Remove Programs.
AppId={{8F3D2A14-6B7E-4C59-9D21-5A7E0C4B18F6}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
AppUpdatesURL={#AppURL}
VersionInfoVersion={#AppVersion}

; Per-user: installs under %LOCALAPPDATA% and never prompts for elevation. Matches the path the
; app's own self-installer used, so an existing install is upgraded in place rather than doubled.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={localappdata}\Programs\StealthPDF
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto

; GPLv3 is shown before install, as the licence requires it to travel with the binary.
LicenseFile=..\LICENSE
SetupIconFile=..\Resources\kp-icon.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}

OutputDir=..\dist
OutputBaseFilename=StealthPDF-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\{#AppExe}";        DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\*.config";         DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "{#SourceDir}\LICENSE";          DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
; The .pdf document icon. The app only extracts this during its own self-install, which does not
; run under this installer, so ship it directly or DefaultIcon below points at a missing file.
Source: "..\Resources\pdf-file.ico";     DestDir: "{app}"; Flags: ignoreversion
; The two helper folders. Without these, TWAIN scanning and PDF compression silently do nothing:
; both are resolved at runtime relative to the EXE.
Source: "{#SourceDir}\TwainHelper\*";    DestDir: "{app}\TwainHelper"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SourceDir}\PdfHelper\*";      DestDir: "{app}\PdfHelper";   Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}";        Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#AppName}";  Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Marker the application itself checks, so an Inno-installed copy does not offer to self-install again.
Root: HKCU; Subkey: "Software\StealthPDF"; ValueType: dword;  ValueName: "Installed";   ValueData: 1;                      Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\StealthPDF"; ValueType: string; ValueName: "InstallPath"; ValueData: "{app}\{#AppExe}";      Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\StealthPDF"; ValueType: string; ValueName: "Version";     ValueData: "{#AppVersion}";        Flags: uninsdeletevalue

; ProgID. This makes StealthPDF *available* for PDFs - it deliberately does NOT take over the
; user's default PDF application, which is theirs to choose.
Root: HKCU; Subkey: "Software\Classes\StealthPDF.pdf"; ValueType: string; ValueName: ""; ValueData: "PDF Document"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\StealthPDF.pdf\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\pdf-file.ico,0"
Root: HKCU; Subkey: "Software\Classes\StealthPDF.pdf\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\.pdf\OpenWithProgids"; ValueType: none; ValueName: "StealthPDF.pdf"; Flags: uninsdeletevalue

; Default Programs capability
Root: HKCU; Subkey: "Software\StealthPDF\Capabilities"; ValueType: string; ValueName: "ApplicationName";        ValueData: "{#AppName}"
Root: HKCU; Subkey: "Software\StealthPDF\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "Lightweight PDF viewer and editor"
Root: HKCU; Subkey: "Software\StealthPDF\Capabilities\FileAssociations"; ValueType: string; ValueName: ".pdf";  ValueData: "StealthPDF.pdf"
Root: HKCU; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "{#AppName}"; ValueData: "Software\StealthPDF\Capabilities"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Caches the app extracts at runtime (OCR natives, tessdata). User settings live in
; %LOCALAPPDATA%\StealthPDF\*.json and are deliberately left in place so preferences,
; saved signatures and stamp presets survive an uninstall or a reinstall.
Type: filesandordirs; Name: "{localappdata}\StealthPDF\ocr"

[Code]
// Close a running instance before upgrading, otherwise the EXE is locked and the copy fails.
function InitializeSetup(): Boolean;
var
  ResultCode: Integer;
begin
  if CheckForMutexes('Local\StealthPDF.SingleInstance') then
  begin
    if MsgBox('StealthPDF is running and must be closed before it can be updated.'#13#10#13#10 +
              'Close it now?', mbConfirmation, MB_YESNO) = IDYES then
      Exec('taskkill.exe', '/IM StealthPDF.exe /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
    else
    begin
      Result := False;
      Exit;
    end;
  end;
  Result := True;
end;
