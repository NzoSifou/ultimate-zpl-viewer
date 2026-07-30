# ============================================================================
#  build-installer.ps1 - Publie l'application et genere le Setup.exe (Inno Setup).
#
#  Fait tout en une commande :
#    1) dotnet publish (Release, self-contained, non package)
#    2) copie le fichier de ressources .pri de l'app dans le dossier publish
#       (WinUI le genere sous bin\x64\Release mais ne le copie pas dans publish ;
#        sans lui, l'app plante au demarrage : "Cannot locate resource MainWindow.xaml")
#    3) retire tout dossier de donnees WebView2 qui trainerait dans publish
#    4) compile l'installateur avec Inno Setup
#
#  Usage :  powershell -ExecutionPolicy Bypass -File build-installer.ps1
#
#  NB : fichier volontairement en ASCII pur (PowerShell 5.1 lit un .ps1 sans BOM
#       en ANSI ; des accents casseraient l'analyse du script).
# ============================================================================

$ErrorActionPreference = 'Stop'
$proj    = Join-Path $PSScriptRoot '..\Ultimate ZPL Viewer.csproj'
$projDir = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$rel     = 'bin\Release\net8.0-windows10.0.19041.0\win-x64\publish'
$pubDir  = Join-Path $projDir $rel
$priSrc  = Join-Path $projDir 'bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\Ultimate ZPL Viewer.pri'
$iss     = Join-Path $PSScriptRoot 'UltimateZplViewer.iss'

Write-Host '==> 1/4  Publication (Release, self-contained)...' -ForegroundColor Cyan
dotnet publish $proj -c Release -r win-x64 --self-contained -p:Platform=x64 `
    -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true
if ($LASTEXITCODE -ne 0) { throw 'La publication a echoue.' }

Write-Host '==> 2/4  Copie du fichier .pri de l''application...' -ForegroundColor Cyan
if (-not (Test-Path $priSrc)) { throw "Fichier .pri introuvable : $priSrc" }
Copy-Item $priSrc (Join-Path $pubDir 'Ultimate ZPL Viewer.pri') -Force

Write-Host '==> 3/4  Nettoyage d''un eventuel dossier WebView2...' -ForegroundColor Cyan
$wv = Join-Path $pubDir 'Ultimate ZPL Viewer.exe.WebView2'
if (Test-Path $wv) { Remove-Item $wv -Recurse -Force }

Write-Host '==> 4/4  Compilation de l''installateur (Inno Setup)...' -ForegroundColor Cyan
$iscc = @(
  "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
  "${env:ProgramFiles}\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 introuvable (ISCC.exe). Installez-le depuis https://jrsoftware.org/isinfo.php' }
& $iscc $iss
if ($LASTEXITCODE -ne 0) { throw 'La compilation de l''installateur a echoue.' }

Write-Host ''
Write-Host "OK - Setup.exe genere dans : $(Join-Path $PSScriptRoot 'Output')" -ForegroundColor Green
