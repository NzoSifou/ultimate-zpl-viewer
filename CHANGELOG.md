# 📋 Changelog

Toutes les modifications notables de **Ultimate ZPL Viewer** sont consignées dans ce fichier.

Le format s'appuie sur [Keep a Changelog](https://keepachangelog.com/fr/1.1.0/) et le projet suit le [versionnage sémantique](https://semver.org/lang/fr/).

---

## [1.1.1] — 2026-07-28

### 🧱 Moteur de rendu ZPL

- **Polices intégrées `P` à `V`** 🔤
  Les sept polices `^AP` … `^AV` étaient rendues comme une police générique à la
  taille brute demandée : sur une étiquette **Chronopost**, presque tous les textes
  sortaient minuscules. Elles utilisent désormais la bonne fonte (celle de la
  police `0`) et leur vraie taille de cellule.

- **Taille des polices bitmap = multiple entier de la cellule** 📏
  Comme sur une imprimante Zebra, la hauteur et la largeur demandées à `^A` sont
  maintenant arrondies au **multiple entier** de la cellule de base de la police,
  chacune de son côté, et ne peuvent jamais descendre **sous** cette cellule.
  ➜ `^ABN,30,15` s'imprime en 3 × 11 par 2 × 7 et `^AQN,10,10` à la taille 28 × 24,
  exactement comme la référence.

- **Ancrage vertical et graisse des polices bitmap** 📐
  Hauteur d'encre, largeur des caractères et bande blanche au-dessus des majuscules
  recalées police par police (`A`–`H`, `P`–`V`) : les textes tombent à la bonne
  taille **et** au bon endroit.

- **Tiret long des polices `P` à `V`** ➖
  Le tiret Zebra (barre longue et épaisse) était réservé à la police `0`. Il
  s'applique désormais aussi aux polices `P`–`V`, qui partagent la même fonte
  (« FR — CHR — 0437 — JAG1 » sur l'étiquette Chronopost). Sa position verticale
  en `^FO` a par ailleurs été recalée.

- **Glyphes déformés dans l'aperçu selon le zoom** 🔍
  Les polices bitmap dont la cellule est **plus haute que large** (`^ABN,30,15` =
  3 × en hauteur mais 2 × en largeur) écrasaient leurs glyphes : le « 2 » de
  « 9585 7542 48K » sortait barré, et l'artefact changeait avec le niveau de zoom.
  L'aperçu dessine désormais ces textes avec un tracé qui supporte la compression,
  puis leur rend leur graisse — l'export PDF/PNG, qui n'était pas concerné, est
  inchangé.

- **Rappel de champ `^FN` sans format stocké** 🔁
  Un `^FNn` placé directement dans l'étiquette, dont la donnée est fournie plus
  loin par un couple `^FNn^FD…` (sans `^DF`/`^XF`), est maintenant remplacé par sa
  valeur. ➜ Les **deux codes-barres des étiquettes Geodis**, jusque-là absents,
  s'affichent et sont scannables.

### 🐛 Corrigé

- **Association `.zpl` — retour clair à l'utilisateur** 🔗
  La fenêtre « Définir par défaut » indique désormais si l'opération a **réussi**
  (message de confirmation) ou **échoué**. En cas d'échec — Windows interdit de
  remplacer un choix déjà enregistré par l'utilisateur — un message explique la
  marche à suivre manuelle : clic droit sur un fichier `.zpl` → « Ouvrir avec » →
  « Ultimate ZPL Viewer » → cocher « Toujours ». L'invite ne réapparaît alors plus
  au démarrage (puisqu'elle ne pourrait pas aboutir automatiquement).

- **Ordre des fenêtres au démarrage** 🪟
  La confirmation d'installation de l'imprimante virtuelle (succès ou erreur)
  s'affiche maintenant entièrement **avant** l'invite d'association `.zpl` — les
  deux fenêtres ne se recouvrent plus.

### ✨ Ajouté

- **Aide en ligne de commande** ⌨️
  `--help` (ou `-h`) affiche la liste des options disponibles (ouverture de
  fichier, `--hide editor,toolbar`, conversion `--pdf` / `--png` avec `--dpmm`,
  `--rotate`, `--margin`, `--unit`…) au lieu d'ouvrir l'application.

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
