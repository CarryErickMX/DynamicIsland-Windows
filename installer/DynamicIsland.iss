; =====================================================================
;  Instalador de Dynamic Island para Windows (Inno Setup 6)
;  Lo compila GitHub Actions en cada versión. Valores recibidos:
;    /DMyVersion=1.1.0   /DMyArch=x64|arm64   /DMySource=ruta\al\publish
; =====================================================================

#ifndef MyVersion
  #define MyVersion "1.0.0"
#endif
#ifndef MyArch
  #define MyArch "x64"
#endif
#ifndef MySource
  #define MySource "..\publish\" + MyArch
#endif

#define MyAppName "Dynamic Island"
#define MyAppExe "DynamicIsland.exe"
#define MyPublisher "CarryErickMX"
#define MyUrl "https://github.com/CarryErickMX/DynamicIsland-Windows"

[Setup]
; GUID fijo: permite que las versiones nuevas reemplacen a la anterior
AppId={{6F0D6B2E-4C1A-4E7B-9C55-2B8E7A1D3F90}
AppName={#MyAppName}
AppVersion={#MyVersion}
AppVerName={#MyAppName} {#MyVersion}
AppPublisher={#MyPublisher}
AppPublisherURL={#MyUrl}
AppSupportURL={#MyUrl}/issues
AppUpdatesURL={#MyUrl}/releases

; Instalación por usuario, sin pedir permisos de administrador
PrivilegesRequired=lowest
DefaultDirName={autopf}\DynamicIsland
DisableDirPage=yes
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes

UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExe}

OutputDir=..\dist
OutputBaseFilename=DynamicIsland-Setup-{#MyArch}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
MinVersion=10.0.19041

#if MyArch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif

[Languages]
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
es.StartupTask=Iniciar Dynamic Island con Windows
en.StartupTask=Start Dynamic Island with Windows
es.LaunchNow=Abrir Dynamic Island ahora
en.LaunchNow=Launch Dynamic Island now

[Tasks]
Name: "startup"; Description: "{cm:StartupTask}"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[Files]
Source: "{#MySource}\{#MyAppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon

[Registry]
; Mismo nombre que usa la app ("DynamicIsland"), así la opción del menú
; "Iniciar con Windows" y el instalador quedan sincronizados
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: string; ValueName: "DynamicIsland"; ValueData: """{app}\{#MyAppExe}"""; \
    Tasks: startup; Flags: uninsdeletevalue
; Al desinstalar también se borran las preferencias (tema)
Root: HKCU; Subkey: "Software\DynamicIsland"; Flags: uninsdeletekey

[Run]
Filename: "{app}\{#MyAppExe}"; Description: "{cm:LaunchNow}"; \
    Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#MyAppExe}"; \
    Flags: runhidden; RunOnceId: "KillIsland"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
// Cierra la isla si está abierta, para poder reemplazar el .exe al actualizar
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#MyAppExe}', '',
       SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(500);
  Result := '';
end;

// Si se desmarca "Iniciar con Windows" al actualizar, quita la entrada anterior
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and (not WizardIsTaskSelected('startup')) then
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'DynamicIsland');
end;
