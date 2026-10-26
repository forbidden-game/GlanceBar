#ifndef AppVersion
  #error AppVersion must be supplied by scripts/Build-Release.ps1.
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish\win-x64"
#endif
#ifndef InstallerOutputDir
  #define InstallerOutputDir "..\dist"
#endif

[Setup]
AppId={{74539364-8DF4-45DC-B64F-467F6DB10C48}
AppName=GlanceBar
AppVersion={#AppVersion}
AppPublisher=GlanceBar contributors
DefaultDirName={autopf}\GlanceBar
DefaultGroupName=GlanceBar
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.22000
OutputDir={#InstallerOutputDir}
OutputBaseFilename=GlanceBar-{#AppVersion}-win-x64-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\GlanceBar.exe
UninstallDisplayName=GlanceBar
CloseApplications=yes
CloseApplicationsFilter=GlanceBar.exe
RestartApplications=no
SetupLogging=yes
ChangesEnvironment=no

[Tasks]
Name: "startup"; Description: "Start GlanceBar when I sign in"; Flags: checkedonce

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\GlanceBar"; Filename: "{app}\GlanceBar.exe"; WorkingDir: "{app}"
Name: "{group}\Uninstall GlanceBar"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\GlanceBar.exe"; Description: "Launch GlanceBar"; Flags: runasoriginaluser nowait postinstall skipifsilent; Check: CanUseOriginalUser

[Code]
const
  ServiceName = 'GlanceBarNetwork';
  SC_MANAGER_CONNECT = $0001;
  SERVICE_QUERY_STATUS = $0004;
  SERVICE_STOPPED = 1;
  SERVICE_STOP_PENDING = 3;
  SERVICE_RUNNING = 4;
  ERROR_SERVICE_DOES_NOT_EXIST = 1060;

type
  TServiceStatus = record
    ServiceType: LongWord;
    CurrentState: LongWord;
    ControlsAccepted: LongWord;
    Win32ExitCode: LongWord;
    ServiceSpecificExitCode: LongWord;
    CheckPoint: LongWord;
    WaitHint: LongWord;
  end;

var
  OriginalUserChecked, OriginalUserAvailable, InstallationFailed: Boolean;

function OpenSCManager(MachineName, DatabaseName: String; Access: LongWord): THandle;
  external 'OpenSCManagerW@advapi32.dll stdcall';
function OpenService(Manager: THandle; Name: String; Access: LongWord): THandle;
  external 'OpenServiceW@advapi32.dll stdcall';
function QueryServiceStatus(Service: THandle; var Status: TServiceStatus): Boolean;
  external 'QueryServiceStatus@advapi32.dll stdcall';
function CloseServiceHandle(Handle: THandle): Boolean;
  external 'CloseServiceHandle@advapi32.dll stdcall';
function GetFileAttributes(Name: String): LongWord;
  external 'GetFileAttributesW@kernel32.dll stdcall';

function RunSystem(const FileName, Arguments: String): Integer;
var
  ExitCode: Integer;
begin
  Log('Running ' + FileName + ' ' + Arguments);
  if not Exec(ExpandConstant('{sys}\') + FileName, Arguments, '', SW_HIDE,
      ewWaitUntilTerminated, ExitCode) then
    RaiseException('Cannot execute ' + FileName + ': ' + SysErrorMessage(ExitCode));
  Result := ExitCode;
end;

procedure RequireSystem(const FileName, Arguments: String);
var
  ExitCode: Integer;
begin
  ExitCode := RunSystem(FileName, Arguments);
  if ExitCode <> 0 then
    RaiseException(FileName + ' failed with exit code ' + IntToStr(ExitCode) +
      '. See the installer log for the command.');
end;

function ServiceState: LongWord;
var
  Manager, Service: THandle;
  Status: TServiceStatus;
  Error: LongWord;
begin
  Result := 0;
  Manager := OpenSCManager('', 'ServicesActive', SC_MANAGER_CONNECT);
  if Manager = 0 then
    RaiseException('Cannot open the service manager: ' + SysErrorMessage(DLLGetLastError));
  try
    Service := OpenService(Manager, ServiceName, SERVICE_QUERY_STATUS);
    if Service = 0 then begin
      Error := DLLGetLastError;
      if Error <> ERROR_SERVICE_DOES_NOT_EXIST then
        RaiseException('Cannot query ' + ServiceName + ': ' + SysErrorMessage(Error));
      Exit;
    end;
    try
      if not QueryServiceStatus(Service, Status) then
        RaiseException('Cannot read service status: ' + SysErrorMessage(DLLGetLastError));
      Result := Status.CurrentState;
    finally
      CloseServiceHandle(Service);
    end;
  finally
    CloseServiceHandle(Manager);
  end;
end;

procedure WaitForService(const DesiredState: LongWord);
var
  Attempt: Integer;
begin
  for Attempt := 1 to 120 do begin
    if ServiceState = DesiredState then Exit;
    Sleep(250);
  end;
  RaiseException('Timed out waiting for ' + ServiceName + ' (expected state ' +
    IntToStr(DesiredState) + '). Check Windows Event Viewer and the installer log.');
end;

procedure StopNetworkService;
var
  State: LongWord;
  ExitCode: Integer;
begin
  State := ServiceState;
  if (State = 0) or (State = SERVICE_STOPPED) then Exit;
  if State <> SERVICE_STOP_PENDING then begin
    ExitCode := RunSystem('sc.exe', 'stop ' + ServiceName);
    if (ExitCode <> 0) and (ServiceState <> SERVICE_STOPPED) then
      RaiseException('Cannot stop ' + ServiceName + ' (exit code ' + IntToStr(ExitCode) + ').');
  end;
  WaitForService(SERVICE_STOPPED);
end;

procedure RemoveLegacyTask;
var
  Scheduler, Folder, Tasks, LegacyTask: Variant;
  Index: Integer;
begin
  { Enumeration makes absence harmless without hiding scheduler failures. }
  Scheduler := CreateOleObject('Schedule.Service');
  Scheduler.Connect;
  Folder := Scheduler.GetFolder('\');
  Tasks := Folder.GetTasks(1);
  for Index := Tasks.Count downto 1 do begin
    LegacyTask := Tasks.Item(Index);
    if (CompareText(LegacyTask.Name, 'GlanceBar Network Helper') = 0) or
        (CompareText(LegacyTask.Name, 'TaskbarPulse Network Helper') = 0) then begin
      { /End also returns an error when an existing task is already stopped. }
      RunSystem('schtasks.exe', '/End /TN "' + LegacyTask.Name + '"');
      RequireSystem('schtasks.exe', '/Delete /TN "' + LegacyTask.Name + '" /F');
    end;
  end;
end;

procedure StopGlanceBarProcesses;
var
  Locator, Services, Processes, Process: Variant;
  Index, ExitCode: Integer;
begin
  { Exact image-name query, no wildcard and no process-tree kill. The service is stopped first. }
  Locator := CreateOleObject('WbemScripting.SWbemLocator');
  Services := Locator.ConnectServer('', 'root\CIMV2');
  Processes := Services.ExecQuery('SELECT ProcessId, Name FROM Win32_Process WHERE Name = ''GlanceBar.exe'' OR Name = ''TaskbarPulse.exe''');
  for Index := 0 to Processes.Count - 1 do begin
    Process := Processes.ItemIndex(Index);
    ExitCode := RunSystem('taskkill.exe', '/PID ' + IntToStr(Process.ProcessId) +
      ' /FI "IMAGENAME eq ' + Process.Name + '" /F');
    { 128 means the process exited after enumeration. }
    if (ExitCode <> 0) and (ExitCode <> 128) then
      RaiseException('Cannot close GlanceBar (exit code ' + IntToStr(ExitCode) + ').');
  end;
end;

function CanUseOriginalUser: Boolean;
var
  ExitCode: Integer;
begin
  if not OriginalUserChecked then begin
    OriginalUserChecked := True;
    { Probe the original token: explicitly elevated Setup has no ordinary token. }
    OriginalUserAvailable := ExecAsOriginalUser(
      ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      '-NoLogo -NoProfile -NonInteractive -Command "if (([Security.Principal.WindowsPrincipal]' +
      '[Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(' +
      '[Security.Principal.WindowsBuiltInRole]::Administrator)) { exit 1 }; exit 0"',
      '', SW_HIDE, ewWaitUntilTerminated, ExitCode);
    OriginalUserAvailable := OriginalUserAvailable and (ExitCode = 0);
    if not OriginalUserAvailable then
      Log('No unelevated original-user token; skipping per-user setup and UI launch.');
  end;
  Result := OriginalUserAvailable and not InstallationFailed;
end;

procedure RequireOriginalUser(const Argument: String);
var
  ExitCode: Integer;
begin
  if not ExecAsOriginalUser(ExpandConstant('{app}\GlanceBar.exe'), Argument,
      ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ExitCode) then
    RaiseException('Cannot configure the original user: ' + SysErrorMessage(ExitCode));
  if ExitCode <> 0 then
    RaiseException('GlanceBar ' + Argument + ' failed with exit code ' + IntToStr(ExitCode) + '.');
end;

function InitializeSetup: Boolean;
var
  Version: TWindowsVersion;
begin
  GetWindowsVersionEx(Version);
  Result := (Version.ProductType = VER_NT_WORKSTATION) and (Version.Major = 10) and
    (Version.Build >= 22000);
  if not Result then
    SuppressibleMsgBox('GlanceBar requires Windows 11 x64 (desktop edition).', mbError, MB_OK, IDOK);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if CompareText(RemoveBackslashUnlessRoot(ExpandConstant('{app}')),
      RemoveBackslashUnlessRoot(ExpandConstant('{autopf}\GlanceBar'))) <> 0 then begin
    Result := 'GlanceBar must be installed in Program Files because its network service runs as SYSTEM.';
    Exit;
  end;
  try
    RemoveLegacyTask;
    StopNetworkService;
    StopGlanceBarProcesses;
  except
    Result := GetExceptionMessage;
  end;
end;

procedure InstallNetworkService;
var
  DataDirectory, BinPath, Verb, DataFile: String;
  Index: Integer;
begin
  DataDirectory := ExpandConstant('{commonappdata}\GlanceBar');
  if DirExists(DataDirectory) then begin
    if (GetFileAttributes(DataDirectory) and FILE_ATTRIBUTE_REPARSE_POINT) <> 0 then
      RaiseException('The shared GlanceBar data directory must not be a junction or symbolic link.');
  end else if not ForceDirectories(DataDirectory) then
    RaiseException('Cannot create ' + DataDirectory);


  { Secure only the directory; never recursively change ACLs on untrusted hard links. }
  RequireSystem('icacls.exe', '"' + DataDirectory + '" /reset /L');
  RequireSystem('icacls.exe', '"' + DataDirectory + '" /inheritance:r /grant:r ' +
    '"*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" "*S-1-5-32-545:(OI)(CI)RX"');
  { A pre-existing directory must not remain owned by an ordinary user. }
  RequireSystem('icacls.exe', '"' + DataDirectory + '" /setowner "*S-1-5-32-544" /L');
  { A pre-created file link must not redirect SYSTEM snapshot/log writes. }
  for Index := 0 to 2 do begin
    case Index of
      0: DataFile := DataDirectory + '\network-processes.tsv';
      1: DataFile := DataDirectory + '\network-processes.tsv.tmp';
      2: DataFile := DataDirectory + '\service-error.log';
    end;
    if FileExists(DataFile) and
        ((GetFileAttributes(DataFile) and FILE_ATTRIBUTE_REPARSE_POINT) <> 0) then
      RaiseException('A GlanceBar data file must not be a symbolic link: ' + DataFile);
    { Unlink old snapshots/logs before SYSTEM opens them; hard links are not reparse points. }
    if FileExists(DataFile) and not DeleteFile(DataFile) then
      RaiseException('Cannot replace the old GlanceBar data file: ' + DataFile);
  end;

  if ServiceState = 0 then Verb := 'create' else Verb := 'config';
  { sc.exe must receive a quoted executable inside its quoted binPath argument. }
  BinPath := '\"' + ExpandConstant('{app}\GlanceBar.exe') + '\" --network-service';
  RequireSystem('sc.exe', Verb + ' ' + ServiceName + ' binPath= "' + BinPath +
    '" start= auto obj= LocalSystem DisplayName= "GlanceBar Network"');
  RequireSystem('sc.exe', 'description ' + ServiceName +
    ' "Collects per-process network usage for GlanceBar."');
  RequireSystem('sc.exe', 'failure ' + ServiceName + ' reset= 86400 actions= restart/5000/restart/15000/restart/60000');
  RequireSystem('sc.exe', 'start ' + ServiceName);
  WaitForService(SERVICE_RUNNING);
  Sleep(3000);
  if ServiceState <> SERVICE_RUNNING then
    RaiseException('GlanceBar network service stopped after startup. See Windows Event Viewer.');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then begin
    InstallationFailed := True;
    InstallNetworkService;
    InstallationFailed := False;
    if not WizardSilent then begin
      if CanUseOriginalUser then begin
        InstallationFailed := True;
        { Migration is unconditional; startup registration remains optional. }
        RequireOriginalUser('--migrate-user');
        if WizardIsTaskSelected('startup') then
          RequireOriginalUser('--configure-user');
        InstallationFailed := False;
      end;
    end;
  end;
end;

function GetCustomSetupExitCode: Integer;
begin
  if InstallationFailed then Result := 1 else Result := 0;
end;

function InitializeUninstall: Boolean;
begin
  Result := False;
  try
    RemoveLegacyTask;
    StopNetworkService;
    StopGlanceBarProcesses;
    if ServiceState <> 0 then begin
      RequireSystem('sc.exe', 'delete ' + ServiceName);
      WaitForService(0);
    end;
    Result := True;
  except
    SuppressibleMsgBox(GetExceptionMessage, mbError, MB_OK, IDOK);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDirectory: String;
begin
  if CurUninstallStep = usUninstall then begin
    DataDirectory := ExpandConstant('{commonappdata}\GlanceBar');
    if DirExists(DataDirectory) then begin
      if (GetFileAttributes(DataDirectory) and FILE_ATTRIBUTE_REPARSE_POINT) <> 0 then
        RaiseException('Refusing to remove a redirected GlanceBar data directory.');
      if not DelTree(DataDirectory, True, True, True) then
        RaiseException('Cannot remove ' + DataDirectory);
    end;
    { Uninstall runs elevated and has no original-user token. Never modify its HKCU.
      Users can run GlanceBar.exe --clear-user-settings before uninstalling. }
  end;
end;
