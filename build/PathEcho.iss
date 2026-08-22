#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef Channel
  #error Channel is required
#endif
#ifndef SourceDir
  #error SourceDir is required
#endif
#ifndef OutputDir
  #error OutputDir is required
#endif
#ifndef OutputBaseFilename
  #error OutputBaseFilename is required
#endif

[Setup]
AppId={{B1A88833-313C-4D6E-9EF5-1F56E77F64C9}
AppName=PathEcho
AppVersion={#AppVersion}
AppPublisher=Kratosmax
AppPublisherURL=https://github.com/Kratosmax/PathEcho
DefaultDirName={localappdata}\Programs\PathEcho
DefaultGroupName=PathEcho
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBaseFilename}
Compression=lzma2
SolidCompression=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\PathEcho.exe
SetupIconFile=..\src\PathEcho\Assets\PathEcho.ico
WizardStyle=modern

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\PathEcho"; Filename: "{app}\PathEcho.exe"

[Run]
Filename: "{app}\PathEcho.exe"; Description: "启动 PathEcho"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'PathEcho');
end;

#if Channel == "Lite"
function HasDesktopRuntime8(RootKey: Integer): Boolean;
var
  Versions: TArrayOfString;
  Index: Integer;
begin
  Result := False;
  if RegGetValueNames(RootKey, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App', Versions) then
    for Index := 0 to GetArrayLength(Versions) - 1 do
      if Pos('8.', Versions[Index]) = 1 then
      begin
        Result := True;
        Exit;
      end;
end;

function InitializeSetup(): Boolean;
var
  ErrorCode: Integer;
begin
  Result := HasDesktopRuntime8(HKLM64) or HasDesktopRuntime8(HKLM32) or
    HasDesktopRuntime8(HKCU64) or HasDesktopRuntime8(HKCU32);
  if not Result then
  begin
    MsgBox('PathEcho Lite 需要 .NET 8 Desktop Runtime x64。安装运行时后请重新运行本安装包。', mbError, MB_OK);
    ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/8.0', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
  end;
end;
#endif
