# 📋 Changelog

Toutes les modifications notables de **Ultimate ZPL Viewer** sont consignées dans ce fichier.

Le format s'appuie sur [Keep a Changelog](https://keepachangelog.com/fr/1.1.0/) et le projet suit le [versionnage sémantique](https://semver.org/lang/fr/).

---

## [1.4.0] — 2026-08-22 — premier démarrage guidé et inspection

### ✨ Ajouté

- **Mode inspection** 🔎 (icône dans la barre d'outils)
  Une icône qui s'allume à la couleur de l'application — celle que vous avez
  choisie si vous en avez personnalisé une. Tant qu'elle est allumée, l'aperçu et
  le code se désignent mutuellement, **dans les deux sens** :

  | Vous cliquez… | …et |
  | :-- | :-- |
  | un élément de l'aperçu | il est **encadré** sur toute la place qu'il occupe, et le code qui le produit est **surligné** — l'éditeur défile jusqu'à lui s'il est hors écran |
  | une ligne de code | la ligne est **surlignée** et l'élément correspondant est **encadré** dans l'aperçu |

  Ce qui est désigné, c'est le **champ entier** — de son `^FO`/`^FT` jusqu'à son
  `^FS` — et non la seule commande sous le curseur : c'est le champ qui fait
  l'élément. Un champ qui produit plusieurs traits (un code-barres et sa ligne
  d'interprétation) est encadré d'un seul tenant.

  Un trait `^GB` d'un ou deux points est invisible à viser : le clic dispose d'une
  petite tolérance, exprimée en pixels à l'écran, donc constante quel que soit le
  zoom. Quand plusieurs champs se recouvrent, c'est le plus petit qui l'emporte —
  le code-barres plutôt que le cadre qui l'entoure.

  Le mode est **désactivé par défaut** : il change ce que fait un clic sur
  l'aperçu, donc il s'active délibérément. Son état est conservé d'une session à
  l'autre.

- **Assistant de premier démarrage** 👋
  Au premier lancement, l'application enchaînait les boîtes de dialogue : polices,
  imprimante virtuelle, fichiers `.zpl`, taille d'écran. Il fallait toutes les
  traiter avant de voir quoi que ce soit du logiciel, sans savoir combien il en
  restait. Elles sont remplacées par **une page unique**, en plein écran, qui se
  parcourt étape par étape :

  | # | Étape | |
  | :-- | :-- | :-- |
  |  | Bienvenue sur Ultimate ZPL Viewer | page d'accueil |
  | 1 | Polices | **obligatoire** — sans elles l'aperçu ne correspond pas à l'impression, donc pas de bouton pour passer |
  | 2 | Imprimante virtuelle | facultative |
  | 3 | Fichiers `.zpl` | facultative |
  | 4 | Écrans | tous les écrans connectés, avec leur taille |
  | 5 | Récapitulatif | ce qui a été fait, passé ou échoué |

- **Indicateur d'étapes** 🔢
  Un numéro par étape dans une pastille, reliée par une barre qui se remplit. Une
  étape franchie porte une coche — sauf si elle a été **passée** ou a **échoué**,
  auquel cas elle porte un tiret gris : une coche verte dirait que quelque chose a
  été fait alors que non.

- **Les étapes déjà satisfaites restent visibles** ✅
  Polices déjà installées, imprimante déjà créée, `.zpl` déjà associés : l'étape
  s'affiche marquée comme faite au lieu d'être sautée en silence. Pour les écrans,
  une taille lue automatiquement apparaît dans un champ **verrouillé** : elle est
  montrée, pas demandée.

- **Deux sorties au récapitulatif** 🚪
  Entrer dans l'application, ou ouvrir directement ses paramètres.

- **« Étape obligatoire pour continuer »** ℹ️
  Une étape qu'on ne peut pas passer le dit sur sa propre ligne, plutôt que de le
  glisser en fin de paragraphe. La formulation vit dans une clé partagée : les
  prochaines étapes obligatoires diront exactement la même chose.

- **Le résultat de l'installation de l'imprimante est affiché** ✅
  L'étape enchaînait en silence une fois l'installation finie. Elle annonce
  maintenant **« Installée avec succès ! »** avec une coche verte et propose
  **Suivant** — ou **« Installation échouée »** avec une croix rouge, un
  **« Détails »** repliable qui montre l'erreur, et **Réessayer**. Le « Passer »
  disparaît après un succès : il n'y a plus rien à passer.

- **L'imprimante virtuelle est présentée comme recommandée** 🖨️
  Sa ligne d'action passe en couleur d'accent, et le texte lève le frein du
  téléchargement : l'installation est en un clic et incluse dans l'application.

- **Une coche verte par écran** 🖥️
  Sur chaque carte, dès que l'écran a une taille exploitable — détectée ou saisie,
  en direct pendant la frappe. On voit d'un coup d'œil ce qu'il reste à renseigner.

Le bouton **Passer** est volontairement discret : c'est une sortie, pas une
invitation. Et le fond n'est pas un aplat gris — un dégradé diagonal teinté de la
couleur d'accent de l'application.

### 🔧 Détails

- Le passage par l'assistant est enregistré dans `onboarding.json`, à côté des
  réglages. **Le désinstalleur le supprime** : une réinstallation repropose
  l'assistant, sans toucher aux réglages, aux fichiers de langue ni au thème de
  couleurs — une réinstallation sert souvent à réparer, pas à tout perdre.
- L'installation des polices redémarre l'application (un processus WinUI en cours ne
  voit pas une police fraîchement installée) : l'assistant **reprend à l'étape
  suivante** au lieu de repartir du début.
- L'assistant ne s'affiche que dans la première fenêtre : ouvrir un fichier dans une
  seconde fenêtre n'y renvoie pas.
- Barre de titre dépouillée pendant l'assistant : les boutons paramètres, barre
  d'outils et plein écran mènent à une application où l'utilisateur n'est pas encore
  entré.
- Traduit en français et en anglais comme le reste de l'interface.

---

## [1.3.1] — 2026-07-31 — raccourcis clavier

### ✨ Ajouté

| Raccourci | Effet |
| :-- | :-- |
| `Ctrl` + `W` | Fermer l'onglet courant (la confirmation reste si le document a des modifications non enregistrées) |
| `Ctrl` + `Maj` + `W` | Fermer tous les onglets, avec la même confirmation document par document |
| `Ctrl` + `T` | Nouveau document dans un **onglet**, quel que soit le réglage d'ouverture |
| `Ctrl` + `N` | Nouveau document dans une **fenêtre** |
| `Ctrl` + `Maj` + `T` | Rouvrir le dernier onglet fermé — ou la dernière **fenêtre** fermée avec tous ses onglets |
| `Ctrl` + `Tab` / `Ctrl` + `Maj` + `Tab` | Onglet suivant / précédent (rebouclage aux extrémités) |
| `Ctrl` + `1` … `9` | Aller à l'onglet correspondant |
| `Ctrl` + `0` | Aller au dernier onglet |
| `Alt` + `Z` | Retour à la ligne dans l'éditeur |
| `Ctrl` + `M` | Afficher ou masquer la minimap |

**Fichier et export**

| Raccourci | Effet |
| :-- | :-- |
| `Ctrl` + `O` | Ouvrir un fichier (respecte le réglage « onglet ou fenêtre » de la barre d'outils) |
| `Ctrl` + `Maj` + `S` | Enregistrer sous… |
| `Ctrl` + `Maj` + `E` | **E**xporter en PDF |
| `Ctrl` + `Maj` + `I` | Exporter en **i**mage PNG |
| `Ctrl` + `D` | Dupliquer l'onglet |

**Affichage**

| Raccourci | Effet |
| :-- | :-- |
| `Ctrl` + `,` | Ouvrir les paramètres — `Échap` (ou de nouveau `Ctrl` + `,`) pour en sortir |
| `F11` | Entrer et sortir du plein écran |
| `Ctrl` + `B` | Afficher ou masquer la **b**arre d'outils |
| `Ctrl` + `E` | Afficher ou masquer l'**é**diteur |
| `Ctrl` + `G` | Afficher ou masquer la **g**rille |
| `Ctrl` + `L` | Afficher ou masquer les numéros de **l**igne |
| `Ctrl` + `Maj` + `1` | Aperçu à 100 % |
| `Ctrl` + `Maj` + `9` | Ajuster l'aperçu à la fenêtre |
| `Ctrl` + `Maj` + `R` | Tourner l'aperçu de 90° |
| `Ctrl` + `/` | **Afficher la liste de tous les raccourcis** |

**Aide-mémoire des raccourcis** ⌨️
`Ctrl` + `/` ouvre une fiche récapitulative : chaque commande sur une ligne, ses
touches dessinées en pastilles à droite, regroupées par thème (onglets et fenêtres,
fichier, affichage, aperçu, éditeur de code). Elle passe à une seule colonne et
défile sur une petite fenêtre, et elle est traduite comme le reste de l'interface.
Sur un clavier AZERTY la barre oblique demande `Maj` : la forme avec `Maj` répond
donc aussi, tout comme la touche `/` du pavé numérique.

> `Ctrl` + `Maj` + `0` aurait été le pendant naturel de `Ctrl` + `Maj` + `9`, mais
> Windows le réserve à l'échelle du système pour une méthode de saisie
> (`HKCU\Control Panel\Input Method\Hot Keys\00000104`) : la touche n'atteint aucune
> application. C'est donc `Ctrl` + `Maj` + `1` qui donne le 100 %. Le raccourci en `0`
> reste déclaré, pour les machines qui le laissent libre.

Les bascules `Alt`+`Z`, `Ctrl`+`M`, `Ctrl`+`G` et `Ctrl`+`L`, ainsi que la taille du
texte modifiée au clavier, mettent à jour le **réglage correspondant** dans la page
Paramètres.

Tous ces raccourcis fonctionnent aussi lorsque le curseur est dans l'éditeur, qui
sinon les intercepterait. Trois d'entre eux reprenaient une commande de l'éditeur :
celles-ci sont déplacées et restent disponibles — `Alt`+`G` (aller à la ligne),
`Alt`+`L` (sélectionner la ligne), `Alt`+`D` (sélectionner l'occurrence suivante).

### 🐛 Corrigé

- **Taille du texte de l'éditeur** 🔤
  La hauteur de ligne restait figée quand la taille de police changeait : le texte
  paraissait tassé en grand et flottant en petit, alors que `Ctrl` `+`/`-` donnait un
  bien meilleur résultat. Les deux avancent désormais ensemble, et `Ctrl` `+`/`-`
  ajuste le réglage « Taille de police » au lieu de dériver à côté.

- **`Ctrl` `+` / `-` hors de l'éditeur** 🔍
  Ces touches ajustent maintenant le zoom de l'**aperçu** dès que le curseur n'est
  plus dans l'éditeur. Cliquer dans l'aperçu lui donne le focus, et masquer l'éditeur
  le lui retire.

- **`Ctrl` + `P`** 🖨️
  Ouvrait la boîte d'impression du navigateur depuis l'éditeur — donc imprimait le
  **code**. Envoie désormais l'étiquette à l'imprimante sélectionnée, en respectant
  le réglage de confirmation.

- **`Ctrl` + `W` avec un seul document** 🗂️
  Ne faisait rien. La barre d'onglets est masquée quand il n'y a qu'un document, et
  un `TabView` masqué ne signale aucune sélection : le raccourci ne trouvait donc pas
  l'onglet à fermer — exactement dans le cas le plus courant.

- **`Ctrl` + `N`** 🪟
  Ouvrait une fenêtre entièrement noire, puis l'application s'arrêtait. La page de la
  nouvelle fenêtre n'était pas encore dans l'arbre visuel, donc sans `XamlRoot`, et la
  boîte « Nouveau fichier » ne pouvait pas s'y afficher.

---

## [1.3.0] — 2026-07-31 — onglets et fenêtres

L'application se comporte désormais comme un navigateur : une seule instance, des
documents qui se rangent en onglets, et des onglets qui se détachent en fenêtres.

### ✨ Ajouté

- **Ouverture en onglets** 🗂️
  Ouvrir un second fichier `.zpl` alors que l'application tourne déjà ne lance plus
  une deuxième copie du programme : le document rejoint la fenêtre déjà à l'écran.

- **Trois réglages d'ouverture indépendants** ⚙️ (carte **Général**)

  | Réglage | Choix |
  | :-- | :-- |
  | Fichiers ouverts depuis l'Explorateur | dans un nouvel onglet **ou** dans une nouvelle fenêtre |
  | Bouton « Ouvrir un fichier » | dans un nouvel onglet **ou** dans une nouvelle fenêtre |
  | Lancer l'application sans fichier | ouvrir une nouvelle fenêtre vide **ou** revenir à la fenêtre déjà ouverte |

- **« Ouvrir dans une nouvelle fenêtre »** 🪟
  Nouvelle entrée au clic droit sur un onglet : le document part dans sa propre
  fenêtre.

- **Détacher et rattacher un onglet à la souris** ↔️
  Glisser un onglet hors de la fenêtre en fait une fenêtre à part ; le déposer sur
  la barre d'onglets d'une autre fenêtre l'y rattache. Un document seul n'ayant pas
  d'onglet visible, il se déplace en glissant la **barre de titre** de sa fenêtre sur
  la zone d'onglets de celle d'arrivée. Quand le dernier document d'une fenêtre s'en
  va, la fenêtre se referme d'elle-même.

- **Restauration de la disposition des fenêtres** 💾
  L'option « rouvrir les derniers fichiers » restaure désormais quel document était
  dans quelle fenêtre, et non plus une liste à plat.

---

## [1.2.0] — 2026-07-31 — couverture des commandes ZPL

Passe de fond sur le moteur : l'objectif est qu'une étiquette **jamais vue** s'affiche
juste du premier coup, plutôt que de corriger commande par commande à chaque nouveau
transporteur.

### 🧱 Nouveaux codes-barres

| Commande | Symbologie | Vérification |
| :-- | :-- | :-- |
| `^BA` | Code 93 (+ 2 caractères de contrôle) | largeur identique à la référence |
| `^BK` | Codabar (start/stop A–D) | à 1 point près |
| `^B1` | Code 11 (1 ou 2 caractères de contrôle) | à 1 point près |
| `^BM` | MSI (schémas de contrôle A/B/C/D) | à 1 point près |
| `^BP` | Plessey (+ CRC) | à 3 points près |
| `^BI` / `^BJ` | 2 of 5 industriel / standard | motifs relevés sur la référence |
| `^BL` | LOGMARS | identique, ligne d'interprétation **au-dessus** |
| `^B9` | UPC-E | identique |
| `^BS` | Supplément UPC/EAN 2 ou 5 chiffres | identique |
| `^BZ` / `^B5` | POSTNET / PLANET (barres à hauteur variable) | identique |

- **`^B4`, `^BB`, `^BT`** (Code 49, CODABLOCK, TLC39) suivent désormais la référence,
  qui n'imprime que la donnée en texte.
- **`^BF`** (MicroPDF417) et **`^BR`** (GS1 DataBar) ne sont pas encodés : `^BF` réserve
  sa zone, `^BR` n'affiche rien — comme la référence — au lieu de recracher la donnée.
- **UPC-A / EAN-13** : le premier chiffre s'imprime bien **à gauche** du symbole et le
  chiffre de contrôle **à droite**, en dehors des barres, au lieu de décaler tout le
  code. Un chiffre débordant du symbole n'est plus rogné par le bord de l'étiquette.

### ✨ Nouvelles commandes de mise en page

- **`^FW`** — orientation par défaut des champs (textes et codes-barres suivent).
- **`^LR`** — impression inversée sur toute l'étiquette.
- **`^PM`** — impression en miroir.
- **`^MU`** — coordonnées en pouces ou en millimètres au lieu des points.
- **`^FV`** — variable de champ (y compris en remplissage d'un `^FN`).
- **`^FP`** — impression directionnelle : caractères empilés verticalement.
- **`^CW`** — alias de police : la lettre désigne une police téléchargée, qui n'est donc
  plus arrondie à une cellule bitmap.
- **`~DY` + `^IM` / `^IL`** — téléchargement et rappel d'images stockées (logos), en plus
  de `~DG` / `^XG`.
- **`^SF`, `^ID`, `^IS`** reconnues explicitement.

### 🐛 Corrigé

- **Portée de `^A` et de `^CF`** 🔤
  `^A` habille **son seul champ** : après le `^FS`, la police revient à celle définie
  par `^CF` (ou à la police par défaut). Elle restait auparavant active pour tous les
  champs suivants, ce qui grossissait tout un bloc dès qu'un champ isolé changeait de
  taille. Idem pour l'orientation, qui retombe sur `^FW`.
- **Police par défaut** — un `^FD` sans `^A` ni `^CF` s'imprimait beaucoup trop gros :
  c'est désormais la police **A en 9 × 5**, celle qu'utilise une imprimante au démarrage.
- **Lettres de police inconnues** — elles ne sont plus arrondies à une cellule bitmap
  qui ne les concerne pas.
- **Ligne d'interprétation** — les chiffres qui s'impriment **hors** du symbole (premier
  chiffre et clé d'un UPC-A / UPC-E) ne sont plus rognés par le bord d'une étiquette
  dimensionnée automatiquement.

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
