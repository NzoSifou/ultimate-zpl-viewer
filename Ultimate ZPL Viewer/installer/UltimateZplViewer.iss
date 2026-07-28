; ============================================================================
;  Ultimate ZPL Viewer - script d'installation Inno Setup
;  Genere un Setup.exe classique (installation dans Program Files, raccourcis,
;  desinstalleur). L'application est "non packagee" (pas de MSIX) et self-contained
;  (elle embarque .NET et le Windows App SDK : rien d'autre a installer).
;
;  Pour compiler : ouvrir ce fichier dans Inno Setup et cliquer "Compile",
;  ou en ligne de commande :
;    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" "UltimateZplViewer.iss"
;  Le Setup.exe est produit dans le sous-dossier "Output".
;
;  IMPORTANT : publier l'app AVANT de compiler (voir la commande dotnet publish
;  dans la documentation). Le dossier source ci-dessous doit exister.
; ============================================================================

#define MyAppName "Ultimate ZPL Viewer"
#define MyAppVersion "1.1.0"
#define MyAppPublisher "Enzo Monchanin (NzoSifou)"
#define MyAppExeName "Ultimate ZPL Viewer.exe"
; Dossier de publication (relatif a ce .iss). Genere par :
;   dotnet publish -c Release -r win-x64 --self-contained -p:Platform=x64
#define MyPublishDir "..\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish"

[Setup]
; AppId identifie l'application pour les mises a jour/desinstallation : NE PAS changer.
AppId={{7F3A9C21-5B84-4E6A-9C2D-1A2B3C4D5E6F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
; Icone de l'assistant d'installation (Setup.exe).
SetupIconFile=..\Assets\AppIcon.ico
OutputBaseFilename=UltimateZplViewer-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
; Par defaut : installation PAR UTILISATEUR (dans %LOCALAPPDATA%\Programs), SANS
; droits admin ni UAC — comme beaucoup d'applications distribuees sur GitHub.
; Un administrateur peut choisir "tous les utilisateurs" (Program Files) via le
; dialogue propose au demarrage.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
WizardStyle=modern
DisableProgramGroupPage=yes

[Languages]
Name: "french";  MessagesFile: "compiler:Languages\French.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Tout le contenu de la publication self-contained. On exclut tout dossier de
; donnees WebView2 (<exe>.WebView2\) qui aurait pu etre cree en lancant l'app
; depuis le dossier de publication : ces donnees vivent cote utilisateur, pas ici.
Source: "{#MyPublishDir}\*"; DestDir: "{app}"; Excludes: "*.WebView2\*,*.WebView2"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Association .zpl : ProgID + entree "Ouvrir avec" (par-utilisateur, HKCU, retire
; a la desinstallation). L'app rafraichit aussi ces cles au demarrage
; (FileAssociationService) pour garder le chemin de l'exe a jour.
Root: HKCU; Subkey: "Software\Classes\UltimateZplViewer.zpl"; ValueType: string; ValueData: "Fichier ZPL"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\UltimateZplViewer.zpl"; ValueType: string; ValueName: "FriendlyTypeName"; ValueData: "Fichier ZPL"
Root: HKCU; Subkey: "Software\Classes\UltimateZplViewer.zpl\DefaultIcon"; ValueType: string; ValueData: "{app}\{#MyAppExeName},0"
Root: HKCU; Subkey: "Software\Classes\UltimateZplViewer.zpl\shell\open\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\.zpl\OpenWithProgids"; ValueType: string; ValueName: "UltimateZplViewer.zpl"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyAppExeName}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#MyAppName}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyAppExeName}\shell\open\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes"; ValueType: string; ValueName: ".zpl"; ValueData: ""

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Nettoyage a la desinstallation : retire l'imprimante virtuelle, son port et la
; tache de capture si elles avaient ete installees depuis l'app (best-effort).
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command ""Remove-Printer -Name 'Ultimate ZPL Viewer' -EA SilentlyContinue; Remove-PrinterPort -Name (Join-Path $env:ProgramData 'UltimateZplViewer\spool.prn') -EA SilentlyContinue; Unregister-ScheduledTask -TaskName 'UltimateZplViewer_PrintCapture' -Confirm:$false -EA SilentlyContinue"""; RunOnceId: "RemoveVirtualPrinter"; Flags: runhidden
