# 📋 Changelog

Toutes les modifications notables de **Ultimate ZPL Viewer** sont consignées dans ce fichier.

Le format s'appuie sur [Keep a Changelog](https://keepachangelog.com/fr/1.1.0/) et le projet suit le [versionnage sémantique](https://semver.org/lang/fr/).

---

## [1.1.0] — 2026-07-28

### ✨ Ajouté

- **Export PNG avec choix de la qualité** 🖼️
  Au clic sur **PNG**, une fenêtre propose la résolution de l'image via un curseur à **5 crans** — de « Moins bonne qualité — Plus légère » à « Meilleure qualité — Plus lourde », le cran central étant la **qualité originale** :

  | Cran | 1 | 2 | 3 (défaut) | 4 | 5 |
  | :--- | :---: | :---: | :---: | :---: | :---: |
  | Résolution | ÷ 2 | ÷ 1,5 | × 1 | × 1,5 | × 2 |

  Un réglage permet de choisir **« Demander à chaque fois »** (par défaut) ou une **qualité par défaut** fixe (la fenêtre ne s'affiche alors plus).

- **Association des fichiers `.zpl`** 🔗
  - Invite au démarrage pour définir Ultimate ZPL Viewer comme application **par défaut** des `.zpl` (avec « Ne plus me demander »).
  - Réglage dédié dans **Général** (bouton grisé si c'est déjà fait).
  - L'application apparaît désormais **directement** dans le menu **« Ouvrir avec »** du clic droit, sans avoir à parcourir les fichiers.

- **Documentation** 📄 — ajout d'un `README.md` illustré et de ce `CHANGELOG.md`.

### 🐛 Corrigé

- **Traductions manquantes après une mise à jour** 🌍
  Les nouvelles chaînes livrées par une mise à jour s'affichent désormais correctement (fusion des clés dans les fichiers de langue au démarrage) au lieu d'afficher des clés brutes ; les traductions personnalisées de l'utilisateur sont préservées.

---

## [1.0.0] — 2026-07-27

### 🧱 Moteur de rendu ZPL

- **Graphiques `^GF` / `^GFA` compressés `:Z64:` et `:B64:`** 🖼️
  Prise en charge des images encodées en **base64 + compression zlib** (`:Z64:`) et en **base64 simple** (`:B64:`), en plus de l'hexadécimal et de la compression **ACS** déjà gérés.
  ➜ Les logos de transporteurs (par ex. **Colissimo**) s'affichent désormais correctement, au lieu d'apparaître sous forme de rayures.

- **Code 128 (`^BC`) — codes d'invocation de sous-ensemble `>5`, `>6`, `>7`** 📊
  Prise en charge de la bascule explicite vers les sous-ensembles **C** (`>5`), **B** (`>6`) et **A** (`>7`). Auparavant ignorés, ces codes laissaient les suites de chiffres en sous-ensemble B (1 symbole par chiffre) au lieu du sous-ensemble C (1 symbole pour **2** chiffres).
  ➜ **Correction des codes-barres trop larges** qui débordaient de l'étiquette : la largeur est désormais conforme et le code reste parfaitement scannable.

### 🐛 Corrigé

- **Lancement depuis Visual Studio** 🚀
  L'erreur *« The project needs to be deployed before we can debug. Please enable Deploy in the Configuration Manager »* ne se produit plus. L'application est désormais **non packagée** (plus de dépendance au déploiement MSIX) et se lance directement comme un exécutable classique.

---

<sub>Légende — 🧱 moteur ZPL · ✨ ajout · 🔄 modification · 🐛 correction · 🔒 sécurité</sub>
