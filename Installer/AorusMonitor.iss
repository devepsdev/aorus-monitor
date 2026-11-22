; Instalador de AORUS Monitor (Inno Setup 6.7+).
; No se compila a mano: usar Installer\compilar.ps1, que publica el programa y pasa la versión del .csproj.

#ifndef MiVersion
  #define MiVersion "1.1.0"
#endif
#define MiNombre "AORUS Monitor"
#define MiEditor "DevEps"
#define MiExe "AorusMonitor.exe"
#define Origen "..\publicar"

[Setup]
AppId={{6E1A3C52-8F0B-4D7E-9A41-2C5B7E90D4F3}
AppName={#MiNombre}
AppVersion={#MiVersion}
AppVerName={#MiNombre} {#MiVersion}
AppPublisher={#MiEditor}
AppComments=Temperaturas, ventiladores e iluminación para el GIGABYTE AORUS 15G XC
VersionInfoDescription={#MiNombre} · instalador
VersionInfoVersion={#MiVersion}
DefaultDirName={autopf}\{#MiNombre}
DefaultGroupName={#MiNombre}
DisableProgramGroupPage=yes
DisableWelcomePage=no
; Administrador: registra la interfaz WMI, instala PawnIO y crea tareas programadas elevadas.
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=salida
OutputBaseFilename=AorusMonitor-{#MiVersion}-instalador
SetupIconFile=..\Assets\icono.ico
UninstallDisplayIcon={app}\{#MiExe}
UninstallDisplayName={#MiNombre}
Compression=lzma2/ultra64
SolidCompression=yes
; Aspecto: asistente moderno en modo oscuro con los colores de la app
WizardStyle=modern dark
WizardSizePercent=110
WizardBackColor=#121419
WizardImageFile=Imagenes\lateral-100.bmp,Imagenes\lateral-250.bmp
WizardSmallImageFile=Imagenes\pequena-100.bmp,Imagenes\pequena-250.bmp
WizardImageStretch=yes
; La app vive en la bandeja: se cierra a mano en [Code]
CloseApplications=no

[Languages]
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"

[Messages]
es.WelcomeLabel2=Se instalará [name/ver] en el equipo.%n%nMonitor de temperaturas, control de ventiladores e iluminación del teclado para el GIGABYTE AORUS 15G XC, sin necesidad de Gigabyte Control Center.%n%nNo modifica frecuencias, voltajes ni límites de potencia del procesador.

[CustomMessages]
es.GrupoAccesos=Accesos directos:
es.GrupoOpciones=Opciones:
es.TareaEscritorio=Crear un acceso directo en el escritorio
es.TareaArranque=Abrir AORUS Monitor al iniciar Windows (en la bandeja del sistema)
es.TareaPawnIO=Instalar el driver PawnIO (frecuencia, consumo y núcleos de la CPU)
es.AbrirAhora=Abrir AORUS Monitor ahora
es.RegistrandoWmi=Registrando la interfaz WMI de ventiladores...
es.ConfigurandoTareas=Configurando el inicio como administrador...
es.InstalandoPawnIO=Instalando el driver PawnIO...
es.FalloPawnIO=No se ha podido instalar PawnIO automáticamente. Sin él no se ven la frecuencia ni el consumo de la CPU.%n%n¿Quieres abrir su página para descargarlo?

[Tasks]
Name: "escritorio"; Description: "{cm:TareaEscritorio}"; GroupDescription: "{cm:GrupoAccesos}"
Name: "arranque"; Description: "{cm:TareaArranque}"; GroupDescription: "{cm:GrupoOpciones}"
Name: "pawnio"; Description: "{cm:TareaPawnIO}"; GroupDescription: "{cm:GrupoOpciones}"; Check: not PawnIOInstalado

[Files]
Source: "{#Origen}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\README.md"; DestDir: "{app}"; DestName: "LEEME.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MiNombre}"; Filename: "{app}\{#MiExe}"; Comment: "Temperaturas, ventiladores e iluminación"
Name: "{autodesktop}\{#MiNombre}"; Filename: "{app}\{#MiExe}"; Comment: "Temperaturas, ventiladores e iluminación"; Tasks: escritorio

[Run]
Filename: "{sys}\wbem\mofcomp.exe"; Parameters: """{app}\Wmi\gb_wmiacpi.mof"""; StatusMsg: "{cm:RegistrandoWmi}"; Flags: runhidden waituntilterminated
Filename: "{app}\{#MiExe}"; Parameters: "--tarea-abrir si"; StatusMsg: "{cm:ConfigurandoTareas}"; Flags: runhidden waituntilterminated
Filename: "{app}\{#MiExe}"; Parameters: "--arranque si"; StatusMsg: "{cm:ConfigurandoTareas}"; Flags: runhidden waituntilterminated; Tasks: arranque
Filename: "{app}\{#MiExe}"; Description: "{cm:AbrirAhora}"; Flags: nowait postinstall skipifsilent runascurrentuser

[UninstallRun]
; Ventiladores a Normal, quitar la interfaz WMI y las tareas programadas
Filename: "{app}\{#MiExe}"; Parameters: "--restaurar"; Flags: runhidden waituntilterminated; RunOnceId: "RestaurarVentiladores"
Filename: "{sys}\wbem\mofcomp.exe"; Parameters: """{app}\Wmi\gb_wmiacpi_uninstall.mof"""; Flags: runhidden waituntilterminated; RunOnceId: "QuitarWmi"
Filename: "{sys}\schtasks.exe"; Parameters: "/delete /f /tn ""AORUS Monitor"""; Flags: runhidden; RunOnceId: "BorrarTareaInicio"
Filename: "{sys}\schtasks.exe"; Parameters: "/delete /f /tn ""AORUS Monitor (abrir)"""; Flags: runhidden; RunOnceId: "BorrarTareaAbrir"

; Los ajustes (%APPDATA%\AorusMonitor) no se borran: son del perfil del usuario.
; PawnIO tampoco se desinstala: puede usarlo otro programa (se quita desde Aplicaciones de Windows).

[Code]
function PawnIOInstalado: Boolean;
begin
  Result := RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\PawnIO');
end;

procedure CerrarPrograma;
var
  Codigo: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im {#MiExe}', '', SW_HIDE, ewWaitUntilTerminated, Codigo);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  CerrarPrograma;
  Result := '';
end;

function InitializeUninstall: Boolean;
begin
  CerrarPrograma;
  Result := True;
end;

procedure InstalarPawnIO;
var
  Codigo: Integer;
  Abrir: Integer;
begin
  WizardForm.StatusLabel.Caption := CustomMessage('InstalandoPawnIO');
  Exec(ExpandConstant('{cmd}'),
    '/c winget install --id namazso.PawnIO -e --silent --accept-package-agreements --accept-source-agreements --disable-interactivity',
    '', SW_HIDE, ewWaitUntilTerminated, Codigo);
  if not PawnIOInstalado then
  begin
    if SuppressibleMsgBox(CustomMessage('FalloPawnIO'), mbConfirmation, MB_YESNO, IDNO) = IDYES then
      ShellExec('open', 'https://pawnio.eu/', '', '', SW_SHOWNORMAL, ewNoWait, Abrir);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('pawnio') then
    InstalarPawnIO;
end;
