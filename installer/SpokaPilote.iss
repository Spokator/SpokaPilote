; SpokaPilote installer (Inno Setup 6).
; Build with scripts\build-installer.ps1, which publishes the application first.

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef AppArch
  #define AppArch "x64"
#endif
#if AppArch == "arm64"
  #define SetupSuffix "-arm64"
  ; Pas de « Setup » dans le nom ARM64 : les versions 1.0.x prennent le premier « Setup*.exe » d'une Release.
  #define SetupKind "Installer"
  #define ArchAllowed "arm64"
#else
  #define SetupSuffix ""
  #define SetupKind "Setup"
  #define ArchAllowed "x64compatible"
#endif
#define AppName "SpokaPilote"
#define AppExe "SpokaPilote.App.exe"
#define CliExe "spokapilote.exe"
#define AppPublisher "Spoka"

[Setup]
AppId={{C4E1A7B2-5D3F-4A8E-9B6C-2F7D1E0A3B94}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppComments=Pilotes de confiance pour tout le poste et les imprimantes d'entreprise
VersionInfoVersion={#AppVersion}
VersionInfoDescription={#AppName} - programme d'installation
; Outil d'administration (installation de pilotes) : installation pour tous les utilisateurs, dans Program Files.
PrivilegesRequired=admin
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
DisableWelcomePage=no
; x64 : PC Intel/AMD ; arm64 : PC Snapdragon / Surface Pro X et suivantes (installeur distinct).
ArchitecturesAllowed={#ArchAllowed}
ArchitecturesInstallIn64BitMode={#ArchAllowed}
MinVersion=10.0.17763
OutputDir=..\dist
OutputBaseFilename={#AppName}-{#SetupKind}-{#AppVersion}{#SetupSuffix}
SetupIconFile=..\src\SpokaPilote.App\Assets\SpokaPilote.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
WizardImageFile=wizard-100.bmp,wizard-200.bmp
WizardSmallImageFile=wizard-small-100.bmp,wizard-small-200.bmp
Compression=lzma2/ultra64
SolidCompression=yes
CloseApplications=force
RestartApplications=no
; Le dossier d'installation est ajouté au PATH : Windows doit en être informé.
ChangesEnvironment=yes
#ifdef Sign
; Signature facultative (scripts\build-installer.ps1 la définit seulement si un certificat est configuré,
; voir docs\SIGNATURE.md) : ISCC signe l'installeur ET le désinstalleur avec la commande « /Sspokasign=... ».
SignTool=spokasign
SignedUninstaller=yes
SignToolRetryCount=3
SignToolRetryDelay=2000
#endif

[Languages]
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"

[CustomMessages]
fr.DesktopIcon=Créer un raccourci sur le Bureau
fr.AddToPath=Rendre la commande « spokapilote » disponible dans tous les terminaux (scripts de déploiement)
fr.Options=Options :
fr.LaunchApp=Lancer {#AppName}
fr.RemoveData=Voulez-vous aussi supprimer les réglages, les journaux, les rapports et le cache de SpokaPilote ?%n%n(Les sauvegardes de pilotes sont conservées. Choisissez « Non » pour tout garder en cas de réinstallation.)

[Messages]
fr.WelcomeLabel2=Cet assistant va installer [name/ver] sur votre ordinateur.%n%nSpokaPilote inventorie le poste, trouve les pilotes manquants ou à jour chez Microsoft et chez le constructeur du PC, les vérifie et les installe. Il s'occupe aussi des imprimantes et copieurs réseau.

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; GroupDescription: "{cm:Options}"
Name: "addtopath"; Description: "{cm:AddToPath}"; GroupDescription: "{cm:Options}"

[Files]
Source: "..\dist\app-{#AppArch}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\README.md"; DestDir: "{app}"; DestName: "LISEZ-MOI.md"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "Pilotes de confiance"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon; Comment: "Pilotes de confiance"

[Registry]
; Ajout au PATH système (retiré à la désinstallation, voir [Code]).
Root: HKLM; Subkey: "SYSTEM\CurrentControlSet\Control\Session Manager\Environment"; ValueType: expandsz; ValueName: "Path"; \
  ValueData: "{olddata};{app}"; Tasks: addtopath; Check: NeedsAddPath(ExpandConstant('{app}'))

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent runasoriginaluser
; Mise à jour depuis l'application (/RELAUNCH=1) : relance l'interface même en mode silencieux.
; Un déploiement de parc (/VERYSILENT sans ce paramètre) n'ouvre rien sur les postes.
Filename: "{app}\{#AppExe}"; Flags: nowait runasoriginaluser; Check: RelaunchRequested

[Code]
function RelaunchRequested(): Boolean;
begin
  Result := ExpandConstant('{param:RELAUNCH|0}') = '1';
end;

function NeedsAddPath(Dir: String): Boolean;
var
  Paths: String;
begin
  if not RegQueryStringValue(HKLM, 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment', 'Path', Paths) then
  begin
    Result := True;
    exit;
  end;
  Result := Pos(';' + Uppercase(Dir) + ';', ';' + Uppercase(Paths) + ';') = 0;
end;

procedure RemovePath(Dir: String);
var
  Paths: String;
  P: Integer;
begin
  if not RegQueryStringValue(HKLM, 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment', 'Path', Paths) then
    exit;
  P := Pos(';' + Uppercase(Dir) + ';', ';' + Uppercase(Paths) + ';');
  if P = 0 then
    exit;
  { P pointe sur le « ; » ajouté en tête : il correspond au séparateur qui précède Dir dans Paths. }
  if P = 1 then
    Delete(Paths, 1, Length(Dir) + 1)
  else
    Delete(Paths, P - 1, Length(Dir) + 1);
  RegWriteExpandStringValue(HKLM, 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment', 'Path', Paths);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usUninstall then
    RemovePath(ExpandConstant('{app}'));

  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{commonappdata}\{#AppName}');
    if DirExists(DataDir) and not UninstallSilent then
    begin
      if MsgBox(CustomMessage('RemoveData'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      begin
        DelTree(DataDir + '\Cache', True, True, True);
        DelTree(DataDir + '\Downloads', True, True, True);
        DelTree(DataDir + '\Extracted', True, True, True);
        DelTree(DataDir + '\Logs', True, True, True);
        DelTree(DataDir + '\Reports', True, True, True);
        DeleteFile(DataDir + '\settings.json');
      end;
    end;
  end;
end;
