; Elite Soft Erwin Add-In Installer
; Inno Setup 6 Script
; Creates standalone installer with self-contained .NET 10 runtime

#define MyAppName "Elite Soft Erwin Add-In"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Elite Soft"
#define MyAppURL "https://www.elitesoft.com.tr"

[Setup]
AppId={{C4D5E6F7-A8B9-0123-CDEF-456789ABCDEF}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
DefaultDirName={autopf}\EliteSoft\ErwinAddIn
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=ErwinAddIn-Setup-{#MyAppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
WizardStyle=modern
UninstallDisplayName={#MyAppName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[InstallDelete]
; Clean old files before install (prevents stale self-contained runtime conflicts)
Type: filesandordirs; Name: "{app}"

[Files]
; Framework-dependent publish output (requires .NET 10 runtime on target machine)
Source: "C:\EliteSoft\ErwinAddIn-Publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Run]
; Register COM host after install (no /s flag so user sees errors)
Filename: "regsvr32.exe"; Parameters: """{app}\EliteSoft.Erwin.AddIn.comhost.dll"""; StatusMsg: "Registering COM component..."

[UninstallRun]
; Unregister COM host before uninstall
Filename: "regsvr32.exe"; Parameters: "/u /s ""{app}\EliteSoft.Erwin.AddIn.comhost.dll"""; RunOnceId: "UnregComHost"; Flags: runhidden

[Code]
// Register add-in in erwin Add-In Manager
procedure RegisterErwinAddIn();
var
  ErwinRegBase: String;
  Versions: TArrayOfString;
  LatestVersion: String;
  AddInsPath: String;
  I: Integer;
begin
  ErwinRegBase := 'SOFTWARE\erwin\Data Modeler';

  // Register in all erwin versions found in both HKLM and HKCU
  if RegGetSubkeyNames(HKEY_LOCAL_MACHINE, ErwinRegBase, Versions) then
  begin
    for I := 0 to GetArrayLength(Versions) - 1 do
    begin
      AddInsPath := ErwinRegBase + '\' + Versions[I] + '\Add-Ins\Elite Soft Erwin Addin';
      RegWriteDWordValue(HKEY_LOCAL_MACHINE, AddInsPath, 'Menu Identifier', 1);
      RegWriteStringValue(HKEY_LOCAL_MACHINE, AddInsPath, 'ProgID', 'EliteSoft.Erwin.AddIn');
      RegWriteStringValue(HKEY_LOCAL_MACHINE, AddInsPath, 'Invoke Method', 'Execute');
      RegWriteDWordValue(HKEY_LOCAL_MACHINE, AddInsPath, 'Invoke EXE', 0);
      Log('Registered in erwin ' + Versions[I] + ' (HKLM)');
    end;
  end;

  if RegGetSubkeyNames(HKEY_CURRENT_USER, ErwinRegBase, Versions) then
  begin
    for I := 0 to GetArrayLength(Versions) - 1 do
    begin
      AddInsPath := ErwinRegBase + '\' + Versions[I] + '\Add-Ins\Elite Soft Erwin Addin';
      RegWriteDWordValue(HKEY_CURRENT_USER, AddInsPath, 'Menu Identifier', 1);
      RegWriteStringValue(HKEY_CURRENT_USER, AddInsPath, 'ProgID', 'EliteSoft.Erwin.AddIn');
      RegWriteStringValue(HKEY_CURRENT_USER, AddInsPath, 'Invoke Method', 'Execute');
      RegWriteDWordValue(HKEY_CURRENT_USER, AddInsPath, 'Invoke EXE', 0);
      Log('Registered in erwin ' + Versions[I] + ' (HKCU)');
    end;
  end;
end;

// Unregister add-in from erwin Add-In Manager (both HKLM and HKCU)
procedure UnregisterErwinAddIn();
var
  ErwinRegBase: String;
  Versions: TArrayOfString;
  I: Integer;
  AddInsPath: String;
begin
  ErwinRegBase := 'SOFTWARE\erwin\Data Modeler';

  // Clean HKLM
  if RegGetSubkeyNames(HKEY_LOCAL_MACHINE, ErwinRegBase, Versions) then
  begin
    for I := 0 to GetArrayLength(Versions) - 1 do
    begin
      AddInsPath := ErwinRegBase + '\' + Versions[I] + '\Add-Ins\Elite Soft Erwin Addin';
      if RegKeyExists(HKEY_LOCAL_MACHINE, AddInsPath) then
        RegDeleteKeyIncludingSubkeys(HKEY_LOCAL_MACHINE, AddInsPath);
    end;
  end;

  // Clean HKCU (legacy installs)
  if RegGetSubkeyNames(HKEY_CURRENT_USER, ErwinRegBase, Versions) then
  begin
    for I := 0 to GetArrayLength(Versions) - 1 do
    begin
      AddInsPath := ErwinRegBase + '\' + Versions[I] + '\Add-Ins\Elite Soft Erwin Addin';
      if RegKeyExists(HKEY_CURRENT_USER, AddInsPath) then
        RegDeleteKeyIncludingSubkeys(HKEY_CURRENT_USER, AddInsPath);
    end;
  end;
end;

// Set read/execute permissions for all users
procedure SetPermissions();
var
  ResultCode: Integer;
begin
  Exec('icacls.exe', '"' + ExpandConstant('{app}') + '" /grant Users:(OI)(CI)RX /T /Q', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

// Check for previous installation
function InitializeSetup(): Boolean;
var
  UninstallKey: String;
  UninstallString: String;
  ResultCode: Integer;
begin
  Result := True;

  UninstallKey := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#SetupSetting("AppId")}_is1';

  if RegQueryStringValue(HKLM, UninstallKey, 'UninstallString', UninstallString) then
  begin
    if MsgBox('A previous version of ' + '{#MyAppName}' + ' is installed.' + #13#10 +
              'Would you like to uninstall it first?', mbConfirmation, MB_YESNO) = IDYES then
    begin
      Exec(RemoveQuotes(UninstallString), '/SILENT', '', SW_SHOW, ewWaitUntilTerminated, ResultCode);
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    RegisterErwinAddIn();
    SetPermissions();
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    UnregisterErwinAddIn();
  end;
end;
