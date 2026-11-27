; Phoenix for Windows: setup wizard. Built by build.py (which passes Arch, Version and SourceDir).
#ifndef Version
  #define Version "1.0.4"
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish-installer\win-x64"
#endif

[Setup]
; keep this GUID forever: it is how newer versions find and replace older ones
AppId={{6F0D7C52-3B54-4E48-A6D1-8B7D3C1B5A21}
AppName=Phoenix
AppVersion={#Version}
AppVerName=Phoenix {#Version}
AppPublisher=Phoenix
DefaultDirName={autopf}\Phoenix
DefaultGroupName=Phoenix
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\Phoenix.exe
UninstallDisplayName=Phoenix
SetupIconFile=Phoenix.ico
OutputDir=..\dist
OutputBaseFilename=Phoenix-{#Version}-win-{#Arch}-setup
Compression=lzma2/ultra
SolidCompression=yes
WizardStyle=modern
; install for just this user by default (no admin prompt); the wizard offers "all users" as well
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
CloseApplications=yes
RestartApplications=no
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
MinVersion=10.0.19041

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; the app was called Halcyon before: clear its program files and shortcuts out of an upgraded install
Type: files; Name: "{app}\Halcyon.exe"
Type: files; Name: "{app}\Halcyon.dll"
Type: files; Name: "{app}\Halcyon.pdb"
Type: files; Name: "{app}\Halcyon.deps.json"
Type: files; Name: "{app}\Halcyon.runtimeconfig.json"
Type: files; Name: "{autoprograms}\Halcyon.lnk"
Type: files; Name: "{autodesktop}\Halcyon.lnk"

[Icons]
Name: "{autoprograms}\Phoenix"; Filename: "{app}\Phoenix.exe"
Name: "{autodesktop}\Phoenix"; Filename: "{app}\Phoenix.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Phoenix.exe"; Description: "Start Phoenix"; Flags: nowait postinstall skipifsilent
; Phoenix updating itself runs this silently with /RELAUNCH: start the new version again (as the user, not as admin)
Filename: "{app}\Phoenix.exe"; Flags: nowait runasoriginaluser; Check: Relaunch

[UninstallDelete]
; the library, settings and covers in %LocalAppData%\Phoenix are the user's own and are kept on uninstall
Type: filesandordirs; Name: "{app}"

[Code]
function Relaunch: Boolean;
var i: Integer;
begin
  Result := False;
  for i := 1 to ParamCount do
    if CompareText(ParamStr(i), '/RELAUNCH') = 0 then Result := True;
end;

// The ebook reader uses the Microsoft Edge WebView2 Runtime, which Windows 11 includes.
function WebView2Installed: Boolean;
var v: String;
begin
  Result := RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', v) and (v <> '') and (v <> '0.0.0.0')
         or RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', v) and (v <> '') and (v <> '0.0.0.0')
         or RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', v) and (v <> '') and (v <> '0.0.0.0');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and (not WebView2Installed) and (not WizardSilent) then
    MsgBox('Phoenix is installed.' + #13#10#13#10 + 'To read EPUB books Phoenix needs the Microsoft Edge WebView2 Runtime, which was not found on this PC. ' +
           'Everything else works without it. You can get it free from microsoft.com/edge/webview2.', mbInformation, MB_OK);
end;
