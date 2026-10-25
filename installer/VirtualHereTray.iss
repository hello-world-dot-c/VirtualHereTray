; Per-user installer for VirtualHereTray (no administrator rights needed).
; Build with installer\build.ps1, or compile this file with Inno Setup 6 after publishing to ..\dist.

#define AppName "VirtualHereTray"
#define AppVersion "1.0.13"
#define AppExe "VirtualHereTray.exe"

[Setup]
AppId={{8B1D6C3E-5A47-4F0B-9C2E-3D7A1E64B9A2}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppName}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=output
OutputBaseFilename={#AppName}-Setup-{#AppVersion}
SetupIconFile=..\app.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=no

[Tasks]
Name: "startup"; Description: "Start {#AppName} when I sign in to Windows"; GroupDescription: "Options:"
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Options:"; Flags: unchecked

[Files]
Source: "..\dist\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; \
    ValueData: """{app}\{#AppExe}"""; Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[Code]
// The tray app has no window to close, so stop it explicitly. The VirtualHere client it
// launched is a separate process and is left running (devices stay connected).
procedure StopTrayApp;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function DesktopRuntimeInstalled: Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\9.*'), FindRec) then
  begin
    Result := True;
    FindClose(FindRec);
  end;
end;

function InitializeSetup: Boolean;
begin
  Result := True;
  if not DesktopRuntimeInstalled then
    Result := MsgBox('{#AppName} needs the .NET 9 Desktop Runtime, which was not found.' + #13#10 +
      'Install it from https://dotnet.microsoft.com/download/dotnet/9.0 (".NET Desktop Runtime", x64) ' +
      'before running the app.' + #13#10#13#10 + 'Continue installing anyway?', mbConfirmation, MB_YESNO) = IDYES;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopTrayApp;
  Result := '';
end;

function InitializeUninstall: Boolean;
begin
  StopTrayApp;
  Result := True;
end;
