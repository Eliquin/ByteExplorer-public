; This Source Code Form is subject to the terms of the Mozilla Public
; License, v. 2.0. If a copy of the MPL was not distributed with this
; file, You can obtain one at https://mozilla.org/MPL/2.0/.

; ByteExplorer's Windows installer (Inno Setup 6). Build it with build-installer.ps1, which publishes the app and runs
; ISCC with the defines below; by hand:
;   dotnet publish ByteExplorer.csproj -c Release -r win-x64 --self-contained false -o artifacts\publish\win-x64 -p:VlcWindowsX86Enabled=false
;   ISCC.exe /DAppVersion=1.0.0 Tools\Installer\ByteExplorer.iss
;
; It installs the framework-dependent publish output as it is (ByteExplorer.exe, its DLLs, VLC's libvlc folder, LICENSE,
; THIRD-PARTY-NOTICES.md and licenses\), for all users (Program Files) or, chosen in the first dialog, for the current
; user only (no administrator rights). There is no updater and nothing is downloaded: when the .NET 9 runtime is missing,
; Setup says where to get it and the user installs it.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
; Relative to this script (SourcePath ends with a backslash); build-installer.ps1 passes absolute paths.
#ifndef PublishDir
  #define PublishDir SourcePath + "..\..\artifacts\publish\win-x64"
#endif
#ifndef OutputDir
  #define OutputDir SourcePath + "..\..\artifacts\installer"
#endif

#define AppName "ByteExplorer"
#define AppExeName "ByteExplorer.exe"
#define DotNetDownloadPage "https://dotnet.microsoft.com/download/dotnet/9.0"

; Fail at compile time, not at install time, when the publish output is missing or incomplete.
#if !FileExists(AddBackslash(PublishDir) + AppExeName)
  #error "ByteExplorer.exe was not found in PublishDir: run dotnet publish first (see build-installer.ps1)."
#endif
#if !FileExists(AddBackslash(PublishDir) + "LICENSE") || !FileExists(AddBackslash(PublishDir) + "THIRD-PARTY-NOTICES.md")
  #error "LICENSE or THIRD-PARTY-NOTICES.md is missing from PublishDir: the third-party licenses require them next to the program."
#endif

[Setup]
; The AppId identifies the installation for upgrades and the uninstaller: never change it.
AppId={{58A47B49-20E1-4BF4-8F1F-CED542319A93}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Eliquin and ByteExplorer contributors
; The project page, shown in Windows "Installed apps" (a link only: nothing checks for updates).
AppPublisherURL=https://github.com/Eliquin/ByteExplorer-public
AppSupportURL=https://github.com/Eliquin/ByteExplorer-public/issues
AppCopyright=Copyright (C) 2026 Eliquin and ByteExplorer contributors. Mozilla Public License 2.0.
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} Setup
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; All users (Program Files, needs administrator rights) by default; the first dialog offers "only for me", which installs
; to %LOCALAPPDATA%\Programs without administrator rights.
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
; The MPL 2.0, shown as the license page.
LicenseFile=..\..\LICENSE
OutputDir={#OutputDir}
OutputBaseFilename={#AppName}-{#AppVersion}-win-x64-setup
SetupIconFile=..\..\Assets\Icon\ByteExplorer.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
; The publish output is win-x64 (also runs on Windows 11 on Arm through x64 emulation). .NET 9 needs Windows 10 or later.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Upgrading or uninstalling while ByteExplorer runs: close it (Restart Manager) instead of failing on locked files.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Everything dotnet publish produced, folders included (libvlc\win-x64 with VLC's plugins, licenses\). Left out: debug
; symbols, and VLC's 32-bit copy (a plain publish has it; the app runs as x64 and never loads it).
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb,\libvlc\win-x86"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Comment: "Browse your files"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
; Started as the user who ran Setup, never elevated (an elevated file manager would write into the administrator's
; profile and open everything elevated).
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
; ByteExplorer may have been made the default file explorer (Settings > Folder options): give folders, drives and Win+E
; back to File Explorer before the program is deleted. The switch runs without a window and exits; it does nothing when
; ByteExplorer was never registered. It runs as the account that uninstalls (see README "Installer"), and only while the
; .NET 9 runtime is still there (without it the program can't start; the .NET host would show an error instead).
Filename: "{app}\{#AppExeName}"; Parameters: "--unregister-default-file-manager"; Flags: runhidden waituntilterminated; RunOnceId: "UnregisterDefaultFileManager"; Check: IsDotNet9RuntimeInstalled

[Code]
const
  NetCoreKey = 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.NETCore.App';

{ True when Name is a 9.x version ("9.0.11"): a framework-dependent .NET 9 app runs on any 9.x runtime, not on 8 or 10
  (the default roll-forward policy stays within the major version). }
function IsVersion9(const Name: String): Boolean;
begin
  Result := (Length(Name) >= 2) and (Copy(Name, 1, 2) = '9.');
end;

{ The .NET runtime (Microsoft.NETCore.App) 9.x for x64. Avalonia needs only this one, not the Windows Desktop runtime
  (which includes it anyway). The installers register each version as a value name under NetCoreKey; the shared folder
  is checked too, for runtimes copied without an installer. }
function IsDotNet9RuntimeInstalled(): Boolean;
var
  Names: TArrayOfString;
  I: Integer;
  Find: TFindRec;
begin
  Result := False;
  if RegGetValueNames(HKLM64, NetCoreKey, Names) then
  begin
    for I := 0 to GetArrayLength(Names) - 1 do
    begin
      if IsVersion9(Names[I]) then
      begin
        Result := True;
        Exit;
      end;
    end;
  end;

  if FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.NETCore.App\9.*'), Find) then
  begin
    try
      repeat
        if (Find.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
        begin
          Result := True;
          Exit;
        end;
      until not FindNext(Find);
    finally
      FindClose(Find);
    end;
  end;
end;

{ Nothing is downloaded: without the runtime, Setup says where to get it and lets the user decide whether to install
  ByteExplorer now (and the runtime afterwards) or stop. Silent installs continue. }
function InitializeSetup(): Boolean;
begin
  Result := True;
  if not IsDotNet9RuntimeInstalled() then
  begin
    Result := SuppressibleMsgBox(
      'ByteExplorer needs the .NET 9 Runtime (x64), which is not installed on this computer.' + #13#10#13#10 +
      'Download ".NET Runtime 9.0" (or the ".NET Desktop Runtime 9.0", which includes it) for Windows x64 from:' + #13#10 +
      '{#DotNetDownloadPage}' + #13#10#13#10 +
      'ByteExplorer starts once the runtime is installed. Install ByteExplorer now anyway?',
      mbConfirmation, MB_YESNO, IDYES) = IDYES;
  end;
end;
