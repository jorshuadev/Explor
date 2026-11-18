; Instalador de Explor (Inno Setup 6). Se instala por usuario: no pide permisos de administrador.
#define AppName "Explor"
#define AppVersion "1.0.0"
#ifndef PublishDir
  #define PublishDir "..\publish"
#endif
#ifndef OutDir
  #define OutDir "..\dist"
#endif

[Setup]
AppId={{6F3B1E2A-8C4D-4E7A-9B1F-0000000E5910}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Jorshua Jiménez
DefaultDirName={localappdata}\Programs\Explor
DefaultGroupName=Explor
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutDir}
OutputBaseFilename=Explor-Setup-{#AppVersion}
SetupIconFile=..\src\Faro\Assets\explor.ico
UninstallDisplayIcon={app}\Explor.exe
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "desktopicon"; Description: "Crear un acceso directo en el escritorio"; GroupDescription: "Accesos directos:"
Name: "contextmenu"; Description: "Añadir «Abrir en Explor» al menú contextual de carpetas y discos"; GroupDescription: "Integración con Windows:"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Explor"; Filename: "{app}\Explor.exe"; Comment: "Explorador de archivos"
Name: "{autodesktop}\Explor"; Filename: "{app}\Explor.exe"; Tasks: desktopicon

[Registry]
; «Abrir en Explor» en carpetas, fondo de carpeta y discos (sólo para este usuario; se quita al desinstalar)
Root: HKCU; Subkey: "Software\Classes\Directory\shell\Explor"; ValueType: string; ValueName: ""; ValueData: "Abrir en Explor"; Tasks: contextmenu; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Directory\shell\Explor"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\Explor.exe,0"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\Directory\shell\Explor\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Explor.exe"" ""%1"""; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\Explor"; ValueType: string; ValueName: ""; ValueData: "Abrir en Explor"; Tasks: contextmenu; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\Explor"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\Explor.exe,0"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\Explor\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Explor.exe"" ""%V"""; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\Drive\shell\Explor"; ValueType: string; ValueName: ""; ValueData: "Abrir en Explor"; Tasks: contextmenu; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Drive\shell\Explor"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\Explor.exe,0"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\Drive\shell\Explor\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Explor.exe"" ""%1"""; Tasks: contextmenu
; Para que aparezca en "Abrir con" y en el registro de aplicaciones
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\Explor.exe"; ValueType: string; ValueName: ""; ValueData: "{app}\Explor.exe"; Flags: uninsdeletekey

[Run]
Filename: "{app}\Explor.exe"; Description: "Abrir Explor"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
// La app se llamó «Explor» desde 2026-10-26; antes «Archivos» y antes «Nido»: si siguen instaladas, se
// desinstalan en silencio (sus datos ya los migra la app al arrancar).
procedure UninstallOld(const Key: String);
var
  Uninst: String;
  Code: Integer;
begin
  if RegQueryStringValue(HKCU, Key, 'UninstallString', Uninst) then
    Exec(RemoveQuotes(Uninst), '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
  begin
    UninstallOld('Software\Microsoft\Windows\CurrentVersion\Uninstall\{6F3B1E2A-8C4D-4E7A-9B1F-0000000A4C40}_is1');
    UninstallOld('Software\Microsoft\Windows\CurrentVersion\Uninstall\{6F3B1E2A-8C4D-4E7A-9B1F-0000000D1D00}_is1');
  end;
end;
