#define MyAppName "SolidWorks To URDF"
#define DllLocation SourcePath + "..\SW2URDF\bin\x64\Release\SW2URDF.dll"

[Setup]
AppId={{E43E85A9-071D-430A-91B2-84B7AB923170}
AppName={#MyAppName}
AppVersion=1.7.11
AppVerName=SW2URDF SW2025 - Independent Window and Draft Recovery
AppPublisher=SW2URDF open-source community (custom build)
AppPublisherURL=https://github.com/ros/solidworks_urdf_exporter
VersionInfoVersion=1.7.11.0
VersionInfoDescription=SW2URDF SW2025 independent window with automatic draft recovery
DefaultDirName={autopf}\SOLIDWORKS Corp\SOLIDWORKS\URDFExporter
UsePreviousAppDir=yes
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#SourcePath}..\..\..\outputs
OutputBaseFilename=sw2urdfSetup_SW2025_independent_autosave_v1.7.11
SourceDir=..\SW2URDF\bin\x64\Release
Compression=lzma2
SolidCompression=yes
CloseApplications=no
RestartApplications=no
UninstallDisplayIcon={app}\SW2URDF.dll
WizardStyle=modern
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#SourcePath}..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "*.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "SW2URDF.pdb"; DestDir: "{app}"; Flags: ignoreversion
Source: "SW2URDF.png"; DestDir: "{app}"; Flags: ignoreversion
Source: "images\*"; DestDir: "{app}\images"; Flags: ignoreversion recursesubdirs createallsubdirs

[Registry]
Root: HKLM64; Subkey: "SOFTWARE\SolidWorks\Addins\65c9fc17-6a74-45a3-8f84-55185900275d"; ValueType: none; Flags: dontcreatekey uninsdeletekey
Root: HKCU64; Subkey: "Software\SolidWorks\AddInsStartup\65c9fc17-6a74-45a3-8f84-55185900275d"; ValueType: none; Flags: dontcreatekey uninsdeletekey

[UninstallRun]
Filename: "{win}\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe"; Parameters: """{app}\SW2URDF.dll"" /unregister"; Flags: runhidden; RunOnceId: "UnregisterSW2URDF"

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Locator, Services, Processes: Variant;
begin
  Result := '';
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Services := Locator.ConnectServer('', 'root\CIMV2');
    Processes := Services.ExecQuery('SELECT ProcessId FROM Win32_Process WHERE Name = ''SLDWORKS.exe''');
    if Processes.Count > 0 then
      Result := 'Please save your work and fully exit SolidWorks before installing. No SolidWorks process will be closed automatically.';
  except
    Result := 'Could not verify that SolidWorks is closed. Please close SolidWorks and try again.';
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
begin
  if CurStep = ssPostInstall then begin
    if not Exec(ExpandConstant('{win}\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe'),
      '"' + ExpandConstant('{app}\SW2URDF.dll') + '" /codebase', '', SW_HIDE, ewWaitUntilTerminated, Code) then
      RaiseException('Could not launch the .NET registration tool.');
    if Code <> 0 then
      RaiseException('SW2URDF registration failed (exit code ' + IntToStr(Code) + '). Please run Setup as administrator.');
  end;
end;
