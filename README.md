<div align="center">

<img src="docs/logo.png" alt="Ultimate ZPL Viewer" width="140" />

# Ultimate ZPL Viewer

**Visualisez, éditez et imprimez vos étiquettes ZPL — 100 % en local, sans aucune API externe.** 🏷️

[![Windows](https://img.shields.io/badge/Windows-10%20%2F%2011-0078D6?logo=windows&logoColor=white&style=for-the-badge)](#-installation)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white&style=for-the-badge)](#-technologies)
[![WinUI 3](https://img.shields.io/badge/WinUI-3-0067B8?style=for-the-badge)](#-technologies)
[![Licence MIT](https://img.shields.io/badge/Licence-MIT-green.svg?style=for-the-badge)](LICENSE)

<img src="docs/screenshot.png" alt="Aperçu de l'application" width="820" />

</div>

---

## ✨ Fonctionnalités

- 🖥️ **Rendu 100 % local** — vos étiquettes ne quittent jamais votre machine, aucune connexion internet requise.
- ✍️ **Éditeur intégré** (Monaco) avec coloration syntaxique du ZPL, numéros de ligne, minimap et analyse en direct des erreurs.
- 👁️ **Aperçu temps réel** fidèle, à la **taille réelle**, avec zoom (mémorisé par onglet), grille et règles configurables.
- 🗂️ **Onglets multiples** pour travailler sur plusieurs étiquettes à la fois.
- 📄 **Export PDF vectoriel** (net à n'importe quel zoom) et **PNG** avec choix de la **qualité / résolution**.
- 🖨️ **Impression directe** vers une imprimante **Zebra** (ou compatible ZPL) en données brutes.
- 🪄 **Imprimante virtuelle** « Ultimate ZPL Viewer » : imprimez un fichier ZPL depuis n'importe quel logiciel, l'aperçu s'ouvre automatiquement.
- 🌗 **Thèmes** clair / sombre (+ mode « sombre & aperçu clair ») et **couleurs personnalisables**.
- 🌍 **Multilingue** (Français / English), langues **personnalisables et extensibles**.
- 🔗 **Association `.zpl`** : double-cliquez un fichier `.zpl` pour l'ouvrir directement.

## 🧱 Prise en charge du ZPL

Un moteur de rendu **maison**, calibré au pixel sur des étiquettes réelles.

- **Texte & polices** : `^A` / `^A0` (orientations 0/90/180/270°, condensé), polices bitmap A–H, `^CF`, `^FB` (blocs justifiés), `^FH`.
- **Graphiques** : `^GB` (cadres / lignes / barres), `^GC`/`^GE` (cercles/ellipses), `^GD`, `^GF` / `^GFA` — hexadécimal, compression **ACS**, et **`:Z64:` / `:B64:`** (base64 + zlib).
- **Codes-barres 1D** : **Code 128** (`^BC`, sous-ensembles A/B/C), Code 39 (`^B3`), Interleaved 2 of 5 (`^B2`), EAN‑13/8 & UPC‑A.
- **Codes-barres 2D** : **QR** (`^BQ`), **DataMatrix** (`^BX`), **Aztec** (`^BO`), **PDF417** (`^B7`) — tous générés et **scannables**.
- **Mise en page** : `^PW`/`^LL`, `^LH`, `^FO`/`^FT`, `^LT`, `^LS`, `^POI`, `^FR` (inversion), formats stockés `^DF`/`^XF`/`^FN`.

## 📸 Captures d'écran

|  Éditeur & aperçu  |  Paramètres  |
| :---: | :---: |
| <img src="docs/screenshot.png" width="260" /> | <img src="docs/settings.png" width="260" /> |

## 🚀 Installation

1. Rendez‑vous dans la section **[Releases](../../releases)** et téléchargez le dernier `UltimateZplViewer-Setup-x.y.z.exe`.
2. Lancez l'installateur.
   > ⚠️ L'application n'étant pas signée, Windows peut afficher **« Windows a protégé votre ordinateur »** → cliquez **« Informations complémentaires »** puis **« Exécuter quand même »**.
3. L'installation se fait **par utilisateur**, **sans droits administrateur**. Rien d'autre à installer (runtime .NET & WinUI inclus).

## 🪄 Imprimante virtuelle

Depuis les **Paramètres → Imprimante virtuelle**, installez l'imprimante « Ultimate ZPL Viewer » (une autorisation administrateur est demandée une seule fois). Ensuite, **imprimez un fichier `.zpl` depuis n'importe quel logiciel** en choisissant cette imprimante : l'application s'ouvre automatiquement et affiche l'aperçu, prêt à être imprimé sur une vraie Zebra.

## 🛠️ Compiler depuis les sources

**Prérequis** : Windows 10/11, [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) + charge de travail « Développement Windows App SDK » (Visual Studio 2022+).

```bash
# Compiler & lancer
dotnet run --project "Ultimate ZPL Viewer/Ultimate ZPL Viewer.csproj" -p:Platform=x64
```

Créer l'installateur redistribuable (application autonome + `Setup.exe`) en une commande — nécessite [Inno Setup 6](https://jrsoftware.org/isinfo.php) :

```bash
powershell -ExecutionPolicy Bypass -File "Ultimate ZPL Viewer/installer/build-installer.ps1"
```

Le `Setup.exe` est généré dans `Ultimate ZPL Viewer/installer/Output/`.

## 🧩 Technologies

- **WinUI 3** (Windows App SDK) · **.NET 8** · C#
- **Monaco Editor** hébergé dans **WebView2**
- **Win2D** pour le rendu de l'aperçu, moteur PDF vectoriel maison
- **[PDFtoImage](https://github.com/sungaila/PDFtoImage)** pour la rastérisation
- **[Inno Setup](https://jrsoftware.org/isinfo.php)** pour l'installateur

## 📝 Changelog

Voir **[CHANGELOG.md](CHANGELOG.md)** pour l'historique des versions.

## 📄 Licence

Distribué sous licence **MIT** — voir [LICENSE](LICENSE).

## 👤 Auteur

**Enzo Monchanin** *(NzoSifou)* — [@NzoSifou](https://github.com/NzoSifou)

<div align="center">
<sub>Fait avec ❤️ pour simplifier le travail avec les étiquettes ZPL.</sub>
</div>
