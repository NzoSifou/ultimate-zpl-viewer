# 📋 Changelog

Toutes les modifications notables de **Ultimate ZPL Viewer** sont consignées dans ce fichier.

Le format s'appuie sur [Keep a Changelog](https://keepachangelog.com/fr/1.1.0/) et le projet suit le [versionnage sémantique](https://semver.org/lang/fr/).

---

## [1.6.0] — 2026-09-10 — transformer un document entier

Jusqu'ici on modifiait une étiquette un champ à la fois. Un bouton
**« Transformer »** dans la barre d'outils en change deux choses d'un coup, sur
tout le document : la **densité** pour laquelle il est écrit, et son
**orientation**. Les deux se combinent, ou se prennent séparément.

Rien n'est régénéré. Comme le mode édition, le moteur **réécrit les nombres là
où ils sont** : les commentaires, les sauts de ligne et les commandes qu'il ne
connaît pas ressortent à l'octet près. Et tout passe en une seule modification :
un Ctrl+Z rend le document d'avant.

### ✨ Ajouté

- **Convertir la densité** 🔍
  Six, huit, douze ou vingt-quatre points par millimètre. Toutes les longueurs
  sont recalculées — coordonnées, corps de texte, hauteurs de codes-barres,
  épaisseurs de traits, largeurs de bloc — et **l'étiquette garde la taille
  qu'elle a dans le monde réel**. Ce sont les nombres qui changent, pas le format :
  une étiquette de 100 × 150 mm reste 100 × 150 mm, et l'aperçu ne bouge pas d'un
  pixel. Les images embarquées (^GF) sont **redessinées** avec plus ou moins de
  pixels plutôt que laissées à leur taille, sans quoi elles seraient les seules à
  changer de dimension sur l'étiquette.

  Un module de code-barres est un nombre entier de points, et il n'a pas toujours
  d'image exacte à la nouvelle densité : trois points à huit par millimètre en
  font quatre et demi à douze, et **ni quatre ni cinq ne sont exacts**. La
  différence n'est pas un détail d'arrondi : un module est un multiplicateur, et un
  Code 128 en compte deux cents, si bien que le demi-point devient un dixième de la
  largeur du code-barres. La fenêtre demande donc de quel côté pencher — **plutôt
  plus grand** par défaut, parce qu'un fournisseur indique une largeur de module
  MINIMALE : la dépasser reste dans les clous, passer dessous peut en sortir. Et
  quand le compte ne tombe pas juste, elle le dit, avec l'écart en pourcentage.

  Avec un plancher dans tous les cas : jamais moins de deux points, en deçà
  desquels aucun lecteur ne déchiffre les barres. Et une étiquette qui n'a jamais
  écrit `^BY` se voit remettre celui sur lequel elle comptait, dans les nouveaux
  points.

- **Pivoter le document** 🔄
  Un quart, un demi ou trois quarts de tour. Chaque champ est replacé et son
  orientation avancée ; un quart de tour échange aussi la largeur et la hauteur de
  l'étiquette, et met les cadres, les images et les codes-barres sur le côté avec
  elle. C'est une vraie réécriture du code, pas une rotation de l'affichage — le
  bouton « Tourner » à côté, lui, ne touche toujours qu'à l'aperçu.

  Le point délicat est l'ancrage. Un ^FO désigne le coin haut-gauche de la boîte
  d'un champ, et un coin n'est pas un point : tournez le champ et c'est un autre
  coin qui devient le haut-gauche. Cette boîte n'est pas recalculée ici, elle est
  demandée au moteur de rendu, qui mesure déjà chaque champ pour l'aperçu. Un ^FT
  échappe à la règle et tombe juste : il ancre une ligne de base, qui est un point
  matériel des lettres et voyage avec elles.

- **La fenêtre dit ce qu'elle va faire, et ce qu'elle laisse** 📐
  La taille après transformation est annoncée avant de valider. Et les commandes
  que le moteur n'a pas su réécrire sont **nommées, avec leur ligne**, au lieu
  d'être passées sous silence : une image rappelée par son nom (^XG), un ^MU qui
  exprime les coordonnées en pouces, un ^JM qui déclare lui-même la densité.
  Une police interne à l'imprimante y figure aussi : elle n'existe qu'en multiples
  entiers de sa propre taille, et son corps ne peut donc pas suivre exactement.

### 🔄 Modifié

- **Un bloc de texte pivoté revient enfin à la ligne** 📄
  Un ^FB rendu de travers était dessiné sur une seule ligne, qui filait hors de
  l'étiquette. Le découpage en lignes vaut maintenant pour les quatre
  orientations : le bloc est mis en page dans son propre sens de lecture, puis ce
  sens est tourné avec le champ. Une étiquette dont le texte tenait sur une ligne
  est rendue exactement comme avant.

### 🐛 Corrigé

- **Le mode visualisation refusait les modifications venues de l'application** ✏️
  L'éditeur y est en lecture seule, et Monaco refuse alors une modification
  programmée comme il refuse une frappe — Ctrl+Z compris. Une modification qui
  vient de l'application n'est pas une frappe : le verrou est levé le temps de
  l'appliquer, puis remis.

### 🔧 Détails

- Un ^GD qui penche à droite est stocké avec une hauteur négative ; la boîte qu'il
  occupe est la même dans les deux sens, et elle est désormais lue comme telle.
- Une image ^GF n'était redessinée que si l'on pivotait aussi le document : une
  conversion de densité seule la laissait à son nombre de pixels, donc seule à ne
  pas changer de taille sur l'étiquette.
- La boîte d'un code-barres pivoté était lue à l'endroit, et un PDF417 pivoté
  n'était pas pivoté du tout — ses modules sont maintenant tournés avec le champ,
  de sorte que la boîte, le dessin et la conversion voient la même forme.
- Deux commandes à insérer au même endroit se prenaient pour des doublons et
  l'une était jetée.
- Changer la densité depuis la liste de la barre d'outils réécrit ^PW et ^LL à elle
  seule. La conversion vient de le faire, pour toutes les longueurs : la liste se
  tait quand c'est la conversion qui la déplace.

---

## [1.5.1] — 2026-09-09 — la ligne de commande, le type des imprimantes, et huit corrections

### ✨ Ajouté

- **Une page pour la ligne de commande** ⌨️
  L'application répond à une poignée d'options, et le seul moyen de l'apprendre
  était de la lancer avec `--help` depuis un terminal — ce que personne ne fait
  avec un logiciel qu'on ouvre en double-cliquant. La même liste est maintenant
  dans les paramètres, mise en page pour être lue : une carte par famille
  d'options, l'option à gauche dans la police d'un terminal, ce qu'elle fait à
  droite, et quatre exemples que l'on copie d'un bouton. « À propos » y renvoie.

- **Chaque imprimante garde son type** 🖨️
  Windows ne dit jamais « celle-ci est une imprimante d'étiquettes » : l'application
  lit le nom du pilote et devine. La devinette se corrigeait à la main à chaque
  impression, et ce qui était choisi s'écrivait en douce — une réponse d'un jour
  devenait définitive sans le dire.

  Le choix se fait maintenant exprès. Sous le type, dans la fenêtre d'impression,
  un bouton texte le fixe pour cette imprimante ; la liste affiche alors la réponse
  au lieu de reposer la question, et le même bouton rend la main. Imprimer n'écrit
  plus rien.

  Les paramètres listent toutes les imprimantes avec le type que prend chacune et
  d'où il vient — choisi, ou détecté. En sélectionner une propose trois réponses :
  les deux types, et la détection automatique, qui nomme ce qu'elle a deviné.

- **Le fichier de langue s'ouvre d'un clic** 🌐
  Un bouton à côté de la liste des langues ouvre le fichier JSON de celle qui est
  sélectionnée, pour qui traduit ou corrige.

### 🔄 Modifié

- **Le chemin du fichier s'affiche toujours au survol d'un onglet** 🏷️
  C'était un réglage. Un réglage est une question qu'il faut poser à quelqu'un, et
  celle-ci ne coûtait rien à laisser active.

- **« Taille de la sélection » passe dans « Mode édition »** 📐
  C'est l'épaisseur du cadre qui entoure un élément sélectionné : elle est de ce
  côté-là.

- **La version n'affiche plus que trois nombres, y compris dans la mise à jour** 🔢

### 🐛 Corrigé

- **« Définir par défaut » restait actif alors que l'application l'était déjà** 📄
  Windows n'enregistre pas ce choix de la même façon selon la manière dont il a
  été fait : notre propre identifiant si l'application s'est déclarée, un
  `Applications\<exe>` si le choix est passé par « Ouvrir avec → Toujours ». Seul
  le premier était reconnu. La question posée est maintenant celle qui compte :
  la commande derrière l'identifiant lance-t-elle cet exécutable ?

- **« Impression rapide » restait proposée sans imprimante nommée** 🖨️
  Elle exige que toutes les réponses de la fenêtre d'impression soient réglées
  d'avance. L'imprimante en fait partie : « dernière imprimante sélectionnée »
  n'est pas une réponse au premier lancement d'une installation neuve, et lancer
  une impression sur une imprimante que personne n'a nommée n'est pas une chose à
  faire sans demander. L'imprimante choisie doit aussi être présente : une
  imprimante sélectionnée un jour et débranchée depuis ne nomme plus rien.

  Et la liste des imprimantes ne prévenait personne quand on en changeait : l'état
  du bouton était calculé une fois à l'ouverture de la page et plus jamais, si
  bien que ce qu'il disait en arrivant était ce qu'il disait pour toujours.

- **La mise à jour proposait de revenir en arrière** ⬇️
  Elle compare d'abord l'empreinte du programme d'installation, ce qui répond à
  « est-ce le même build ? » — une autre question. Remplacez le fichier d'une
  version publiée et toutes les machines qui avaient installé l'ancien diffèrent
  de lui, y compris celles qui font tourner quelque chose de **plus récent** :
  on leur proposait la version qu'elles avaient déjà quittée. Une version
  inférieure n'est plus jamais proposée.

- **« Mode au démarrage » avait une largeur à lui** 🧱
  La carte était ajoutée en dehors de la grille de la page et prenait la largeur
  de son contenu, plus étroite qu'une colonne. Elle en fait deux, comme « Zoom par
  défaut ».

### 🔧 Détails

- Les curseurs système sont créés une fois pour toutes et conservés. `InputCursor`
  est un objet dont le finaliseur le FERME, et fermer un curseur système libère la
  poignée que Windows est peut-être en train d'afficher ; les séparateurs en
  fabriquaient un neuf à chaque passe de mise en page.

---

## [1.5.0] — 2026-09-07 — mode édition : dessiner l'étiquette

L'application savait montrer une étiquette ; elle sait maintenant la faire. Un
interrupteur en haut de l'aperçu ouvre un **mode édition** où l'étiquette se
dessine à la souris — poser du texte, un code-barres, une forme, une image, les
déplacer, les redimensionner, les empiler, les copier.

Rien n'est régénéré : chaque geste réécrit **uniquement les chiffres de la
commande concernée**, à sa place dans le fichier. Le ZPL que vous aviez écrit
reste le ZPL que vous aviez écrit, commentaires et mise en forme compris, et
chaque modification passe par la pile d'annulation de l'éditeur — Ctrl+Z défait
un déplacement comme il défait une frappe.

### ✨ Ajouté

- **Deux modes, un interrupteur** 👁️✏️
  *Visualisation* est ce que l'application a toujours fait : le ZPL est affiché en
  lecture seule et un clic sur l'aperçu pointe le code derrière un élément.
  *Édition* déverrouille le texte et transforme l'aperçu en plan de travail. Le
  mode appartient au **document** : un onglet peut être ouvert en lecture pendant
  qu'un autre se dessine. Un réglage décide du mode d'ouverture — toujours en
  lecture, toujours en édition, ou celui qui servait la dernière fois.

- **Placer un élément sur l'étiquette** 🧰
  Une plaque d'outils flottante : texte, code-barres (treize symbologies, avec un
  aperçu de chacune avant de choisir), formes (rectangle, ligne, ellipse, cercle,
  bloc plein), image. On arme l'outil, on clique — ou on fait glisser pour
  dimensionner la forme. **Maj enfoncée**, l'outil reste armé : on pose autant
  d'éléments qu'on veut sans revenir à la plaque entre chacun.

- **Déplacer, redimensionner, tourner, dupliquer, supprimer** ✋
  À la souris ou aux flèches du clavier (un point, dix points avec Maj, aligné au
  millimètre avec Ctrl). Les poignées d'angle redimensionnent ce que ZPL sait
  redimensionner. Sélectionner et déplacer sont **deux outils distincts** : la
  flèche prend un élément pour le lire sans risquer de le bouger.

- **Écrire le texte là où il s'imprime** ⌨️
  Double-clic (ou F2) sur un texte et le curseur s'ouvre **dans les mots eux-mêmes**,
  dans la police, la taille et l'orientation réelles du champ — pas dans une boîte
  à côté. Double-clic pour un mot, triple-clic pour tout, glisser pour sélectionner.
  Entrée termine, Maj+Entrée passe à la ligne, Échap abandonne.

- **Les propriétés de l'élément sélectionné** 🎛️
  Une barre flottante se pose contre l'élément et se déplie sur ses réglages :
  police, hauteur, largeur, inversion vidéo pour un texte ; contenu, symbologie,
  hauteur des barres, largeur de barre, texte lisible pour un code ; dimensions et
  épaisseur pour une forme. Chaque valeur est lue dans le ZPL réel, y compris
  quand elle vient d'un `^BY` posé ailleurs dans le fichier.

- **Poser une image** 🖼️
  N'importe quel PNG, JPEG ou BMP devient un `^GF` monochrome. Deux encodages au
  choix : hexadécimal ACS, compatible avec toutes les imprimantes, ou `:Z64:`,
  environ quatre fois plus compact mais qui demande un firmware Zebra récent.
  Seuillage ou tramage Floyd–Steinberg, seuil réglable, inversion, taille libre —
  et un bouton rouvre la fenêtre plus tard avec les réglages qui ont servi.

- **Plusieurs éléments à la fois** 🗂️
  Ctrl+clic pour ajouter ou retirer, un lasso depuis une zone vide, Ctrl+A pour
  tout prendre. Le groupe se déplace et se supprime d'un bloc.

- **Copier, couper, coller** 📋
  Ce qui voyage est **le ZPL lui-même**, en texte : une copie se colle dans le
  code, dans une autre étiquette, dans un e-mail — et ce qu'on a copié depuis
  n'importe où se colle sur l'étiquette.

- **Décider quel élément est dessiné au-dessus** 🔃
  Deux boutons montent ou descendent l'élément d'un cran, en déplaçant son champ
  dans le fichier : l'ordre de dessin de ZPL est l'ordre du texte.

- **Une catégorie de paramètres pour le mode édition** ⚙️
  Où se posent les plaques flottantes et dans quel sens elles se déplient. Chaque
  plaque prend une position — accrochée à l'un des huit bords et coins, choisie en
  pointant une image de l'aperçu, ou libre et déplaçable par ses poignées — et une
  orientation, en colonne ou en ligne. Deux plaques visant le même endroit se
  partagent la place, l'une au-dessus de l'autre. La barre de l'élément se pose au
  choix au-dessus ou en dessous de ce qu'elle sert.

- **Un bandeau d'état pour les travaux longs** ⏳
  Export PDF, export PNG, impression, rendu d'un gros document, recherche de mise
  à jour : une ligne en bas de la fenêtre, avec un bouton d'abandon quand la tâche
  peut être abandonnée.

- **Les mises à jour s'installent depuis l'application** ⬇️
  La recherche interroge GitHub, propose la version trouvée avec ses notes, et
  installe. La fenêtre reparaît à la fermeture si la mise à jour a été remise à
  plus tard.

### 🔄 Modifié

- **Le curseur dit ce que le clic va faire** 🖱️
  Une croix quand un outil va poser quelque chose, les quatre flèches sur un
  élément qu'un glisser déplacerait, la main ouverte quand l'étiquette dépasse de
  la vue, le curseur de texte sur les mots qu'on est en train de taper. Chaque cas
  a été vérifié en lisant le curseur que Windows affiche réellement.

- **Les menus des codes et des formes tiennent en une ligne par famille** 📐
  Neuf codes linéaires, puis quatre codes 2D, puis les cinq formes sur une ligne.
  Ils se repliaient sur trois lignes de cinq quelle que soit la place disponible :
  une fenêtre volante fait 456 points de large quoi qu'on lui donne.

- **La feuille des raccourcis couvre l'aperçu** ⌨️
  Trois sections de plus — mode visualisation, mode édition, saisie de texte sur
  l'étiquette. La moitié de ce à quoi l'aperçu répond est un modificateur maintenu
  pendant un clic, donc clics, double-clics, glissers et molette y ont leur propre
  touche dessinée.

- **La catégorie Impression est mise en page comme les autres** 🖨️
  Un tiers de fenêtre par carte, comme Général et Document.

- **La version n'affiche plus que trois nombres** 🔢
  Le quatrième est un compteur de compilation qui n'apprend rien à personne.

### 🐛 Corrigé

- **Le thème clair laissait les plaques vides** 🌗
  Les icônes dessinées comme des formes sont peintes depuis le code, et le code
  demandait la couleur d'encre à l'**application**, dont le thème n'est pas
  forcément celui de la page — « Sombre & aperçu clair » est exactement ce cas.
  L'encre revenait blanche : blanc sur blanc. Les icônes se repeignent aussi quand
  le thème change sous elles, ce que rien ne faisait.

- **L'aperçu se déplaçait pendant la saisie d'un texte** 🧷
  Glisser sur les lettres pour les sélectionner faisait défiler l'étiquette. La
  zone de saisie est un enfant du canevas comme un autre, garée hors champ pour ne
  pas avaler les clics, et tout ce qui décidait de « l'amener à l'écran » faisait
  défiler l'étiquette entière pour l'atteindre. La vue est notée à l'ouverture du
  curseur et remise en place dès que quoi que ce soit la déplace.

- **La hauteur et la largeur des barres étaient mal lues** 📏
  `^BY` est modal et souvent écrit **avant** le champ qu'il gouverne, parfois
  plusieurs lignes plus haut : une hauteur affichée à 1 sur un code-barres
  visiblement plus grand. Les valeurs en cours sont maintenant suivies depuis le
  début du fichier.

- **Un bloc plein ne pouvait pas être plus haut que large** ⬛
  Il se carrait. ZPL élargit une boîte à au moins l'épaisseur de sa bordure, et
  c'est la hauteur qui lui était donnée comme bordure pour la remplir. Le côté le
  plus **court** la remplit aussi bien sans rien gonfler.

- **Ouvrir puis refermer les propriétés d'un code-barres faisait planter** 💥
  Un contrôle gardé d'une reconstruction à l'autre reste enfant de la ligne où il
  était, et l'ajouter à un second parent lève une exception. Plus rien n'est
  conservé entre deux reconstructions.

- **La barre de l'élément fuyait vers le bord** ↔️
  Elle s'écartait toujours vers la droite de la plaque d'outils, ce qui suivait
  cette plaque au bord droit dès qu'on l'y mettait. Elle ne s'écarte plus que
  quand elle passe réellement sous une plaque, et du côté dont elle est déjà la
  plus proche.

- **Une plaque libre revenait dans le coin quand on la verrouillait** 📍
  Sa position était relue à l'écran à la fin du glisser — une question qui peut
  rester sans réponse, auquel cas le repli était le coin haut-gauche. C'est le
  calcul du glisser qui est retenu.

### 🔧 Détails

- Les modifications passent par la pile d'annulation de Monaco : un déplacement,
  un redimensionnement ou une frappe sur l'étiquette s'annulent avec Ctrl+Z comme
  n'importe quelle édition de texte.
- Les encodeurs `^GF` ont été vérifiés par 300 allers-retours dans les deux
  encodages contre les décodeurs de l'application : aucun octet de différence.
- 313 clés de langue utilisées dans le code, toutes présentes dans les deux
  fichiers de langue, qui comptent 1593 clés chacun sans divergence.
- Le XAML compilé (`.xbf`) et l'index des ressources (`.pri`) sont publiés avec
  l'application, et la compilation échoue s'ils venaient à manquer : sans eux une
  application non empaquetée ne trouve pas son interface et ne démarre pas.
- Vérifié sans régression sur les jeux de test DPD, Mondial Relay et GLS : taille,
  rendu et légende identiques.

---

## [1.4.1] — 2026-09-02 — volet éditeur et tenue sous charge

Deux chantiers : trois retouches d'ergonomie sur le volet éditeur, puis une passe
de tests agressifs (fichier de 1,8 Mo à 20 000 champs, ligne unique de 200 000
caractères, UTF-16, octets nuls, `^PW0`, `^FO999999`, ouvertures et rotations en
rafale) qui a sorti sept défauts.

### ✨ Ajouté

- **La largeur de l'éditeur est conservée** ↔️
  Mettez l'éditeur à la moitié de la fenêtre : il y sera encore au prochain
  démarrage. La valeur *appliquée* est bornée à la fenêtre courante — une largeur
  enregistrée sur un grand écran ne peut donc pas pousser la poignée hors de vue
  sur un plus petit, ce qui n'aurait laissé aucun moyen de récupérer l'aperçu.
  Réélargir la fenêtre rend à l'éditeur la taille choisie.

- **Un bandeau pendant le rendu des documents lourds** ⏳
  Une étiquette de plusieurs milliers d'éléments met quelques secondes à se
  dessiner, et le dessin occupe l'interface pendant ce temps. Le bandeau paraît
  *avant* que ça arrive, au lieu d'une fenêtre qui semble bloquée sans raison.

### 🔄 Modifié

- **Les boutons flottants de l'éditeur s'écartent de la minimap** ↔️
  Ils la recouvraient et avalaient ses clics. La largeur à dégager est mesurée sur
  les éléments **tels qu'ils sont peints** : les chiffres de mise en page de Monaco
  placent le bord de la minimap quelques pixels à droite de sa position réelle, et
  s'y fier laissait le bouton la frôler à 2 px.

- **Le bouton de repli de l'éditeur ne bouge plus** ◀️
  Il quitte la frontière éditeur/aperçu pour le bord de la fenêtre, du côté de
  l'éditeur (à droite si les volets sont inversés) : dans la gouttière que laisse
  la marge de la carte quand l'éditeur est ouvert, au même pixel quand il est
  fermé. Seul le chevron pivote. La bande centrale ne sert plus qu'à
  redimensionner — viser la poignée ne peut plus fermer l'éditeur par accident.

- **La barre de titre nomme le document actif** 🏷️
  Elle retombait sur le seul nom du produit dès le deuxième onglet ouvert, ce qui
  vidait aussi le texte de la barre des tâches et rendait deux fenêtres
  indistinguables.

- **Ouvrir un fichier déjà ouvert active son onglet** 📑
  Au lieu d'en empiler une seconde copie, avec ses propres modifications, sur le
  même chemin.

- **L'info-bulle d'un onglet est lisible en entier** 💬
  Elle s'affiche sous l'onglet et non plus par-dessus la barre d'outils, et se
  replie sur plusieurs lignes. Ancrée sur l'onglet le plus à gauche, elle
  débordait de la fenêtre et perdait le début du chemin qu'elle est justement
  chargée de montrer.

- **La légende indique quand l'aperçu est plafonné** 📐
  `(limité)` apparaît quand le rendu est ramené à 50 cm, au lieu d'afficher une
  taille qui contredisait silencieusement les champs de la barre d'outils.

### 🐛 Corrigé

- **Un gros document figeait l'application, définitivement** 🧊
  Sur une étiquette de 20 000 champs, maintenir la rotation gelait l'application
  plus de sept minutes, la mémoire montant à 4,8 Go sans jamais redescendre.

  Ce n'était pas le dessin : **chaque rotation relançait l'analyse complète du
  ZPL** — deux à quatre secondes — sur un texte identique, et les résultats
  s'empilaient. L'analyse ne dépend plus que des deux seules choses qu'elle lit,
  le texte et la densité. Le dessin est fusionné par-dessus : une rafale peint son
  dernier état, une fois, au lieu de chaque état à la suite.

  Ouverture du fichier : 985 Mo → **306 Mo**. Douze rotations : figé pour de bon →
  **réactif, 788 Mo**, et trois passes de dessin au lieu de douze.

- **L'application disparaissait sans un mot dans deux situations** 💥
  Ouvrir dix fichiers coup sur coup, ou zoomer en rafale sur une grande étiquette
  avec plusieurs onglets. Aucune des deux n'était une exception — d'où un
  `crash.log` resté vide : c'était un emballement du rendu.

  Le déclencheur : **le zoom est hérité d'un onglet à l'autre**. Passer d'une
  étiquette minuscule (ajustée à ~2000 %) à une étiquette de 500 mm demandait, le
  temps d'une passe, de rasteriser des dizaines de milliers de pixels de côté. Le
  plafond de zoom se calcule maintenant *avant* que le canevas grandisse.

  Deux causes voisines corrigées au passage : les éléments qui commencent au-delà
  de l'étiquette ne sont plus construits (un `^BY99` de 400 caractères court sur
  435 000 points, des milliers de barres invisibles), et une boucle de mise en page
  qui empêchait l'affichage de se stabiliser.

  Dix fichiers en rafale : processus mort → **réactif, 201 Mo**. Cinquante crans de
  zoom sur 500 mm : machine à court de mémoire → **190 Mo, stable**.

- **Entrée lançait l'impression** ⌨️
  Taper une valeur dans la fenêtre d'impression puis appuyer sur Entrée pour la
  valider envoyait le travail à l'imprimante, sans confirmation. La touche remontait
  des champs numériques jusqu'au bouton par défaut. Elle valide toujours le champ,
  mais seul un clic sur « Imprimer » imprime.

- **Un fichier vide ne signalait rien** 📄
  Alors qu'un fichier texte quelconque signalait bien ses deux erreurs. Il indique
  désormais ses `^XA` et `^XZ` manquants comme n'importe quel non-étiquette.

- **`^PW0` donnait une étiquette de 0,25 mm** 🔬
  Que l'aperçu devait ensuite grossir à 5000 %. Une taille **déduite du contenu** a
  maintenant un plancher de 5 mm ; une taille explicitement demandée reste intacte,
  un `^PW2` reste un `^PW2`.

### 🔧 Détails

- Les plantages sont désormais journalisés par trois canaux au lieu d'un : XAML,
  CLR et tâches non observées. À noter qu'un *failfast* XAML natif reste hors de
  portée de tout gestionnaire managé — aucun code ne peut l'intercepter.
- Les ouvertures de fichiers sont sérialisées : deux lancements simultanés
  reprenaient l'un dans l'autre au milieu de la lecture disque.
- Le plafond de zoom est calculé par document plutôt que fixé à ×20 ; une étiquette
  ordinaire atteint ×19,6, donc rien ne change en pratique.
- Vérifié sans régression sur les jeux de test DPD, Mondial Relay, GLS et
  Chronopost : taille, rendu et légende identiques.

---

## [1.4.0] — 2026-08-22 — premier démarrage guidé, inspection et impression

### ✨ Ajouté

- **Fenêtre d'impression** 🖨️
  La liste d'imprimantes qui occupait la barre d'outils disparaît, et le bouton
  « Imprimer » ouvre une fenêtre qui **montre ce qui va sortir** : l'aperçu occupe
  les trois quarts de la largeur et se redessine à chaque réglage touché.

  | Réglage | |
  | :-- | :-- |
  | Imprimante | les imprimantes du poste, sauf l'imprimante virtuelle de l'application |
  | Type d'imprimante | Imprimante classique / Imprimante thermique (ZPL brut) |
  | Nombre d'exemplaires | 1 ou plus |
  | Exemplaires par page | 1 par défaut — plusieurs fois la même étiquette sur une feuille |
  | Mise en page | Portrait / Paysage / Portrait (inversé) / Paysage (inversé) |
  | Taille du papier | les formats proposés par l'imprimante |
  | Marges | en mm ou en cm, aucune par défaut |

  Chaque champ s'ouvre sur la valeur par défaut définie dans les paramètres.

  Sur une imprimante classique, l'étiquette **remplit la page** : elle occupe tout
  l'espace que les marges lui laissent, en gardant ses proportions. Une étiquette
  en paysage sur une A4 portrait prend donc toute la largeur, et sa hauteur suit.
  Rien n'est déformé — les marges sont là pour lui donner moins de place.

  Au-delà d'un **exemplaire par page**, cet espace se divise en autant de cases
  égales, une par copie, et **la division suit la mise en page** : une page en
  portrait se coupe en bandes horizontales, une page en paysage en colonnes. Quatre
  étiquettes sur une A4 portrait donnent quatre bandes ; les mêmes en paysage, quatre
  colonnes.

  Le découpage **se replie tout seul quand la place est gâchée** : dès qu'une case
  laisse à côté de l'étiquette autant de vide que l'étiquette occupe — les deux
  marges réunies valent la largeur du document — c'est qu'une seconde y tiendrait,
  et l'autre axe se divise à son tour. Tant que ça reste vrai, il continue. Huit
  étiquettes sur une A4 portrait donnent donc **deux colonnes de quatre**, et non
  huit bandes étriquées.

- **Deux façons d'envoyer, selon le type d'imprimante** 🔀
  Une **imprimante thermique** reçoit le **ZPL brut**, qu'elle compose elle-même —
  c'est ce qui garantit la fidélité. Une **imprimante classique** reçoit le rendu
  de l'étiquette, mis en page par Windows. Le type est deviné d'après le pilote,
  modifiable dans la fenêtre, et retenu pour cette imprimante.

  Sur une imprimante thermique, seuls le nombre d'exemplaires (`^PQ`) et
  l'inversion (`^POI`) sont transmissibles : le langage n'a aucune commande pour
  tourner une étiquette entière, et c'est l'imprimante qui choisit son média. La
  taille du papier et les marges y sont donc **grisées**, plutôt qu'acceptées puis
  ignorées en silence.

- **Quatre valeurs par défaut d'impression** ⚙️ (section **Impression**)
  Nombre d'exemplaires, exemplaires par page, mise en page et marges. Chacune au
  choix **fixe** ou **reprise de la dernière impression**. Le mode et sa valeur
  sont empilés à droite de la carte, face au texte. Sur « reprendre la dernière
  valeur » le champ de saisie **disparaît** au lieu d'être grisé : il n'y a rien à
  y écrire.

- **Impression rapide** ⚡
  Le bouton « Imprimer » imprime sans ouvrir la fenêtre. L'interrupteur est
  **grisé** dès qu'un des quatre réglages reprend la dernière valeur : ce qui
  sortirait ne serait alors pas connu d'avance. Quand il est actif, survoler le
  bouton annonce ce qui va partir — `Zebra ZD421, x1, Portrait, aucune marge`.

- **Taille de la sélection** 📏 (section **Éditeur et aperçu**)
  Épaisseur du cadre d'inspection, de 1 à 10 px.

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

### 🗑️ Retiré

- La liste déroulante d'imprimantes de la barre d'outils.
- Le réglage « Confirmer avant impression » et sa boîte de confirmation : la
  fenêtre d'impression montre ce qui va sortir, ce qu'un oui/non ne faisait pas.

### 🐛 Corrigé

- **Les textes réécrits n'arrivaient jamais chez un utilisateur existant** 🌍
  Les fichiers de langue sont recopiés dans le dossier de l'utilisateur au premier
  lancement, et la mise à jour n'y **ajoutait** que les clés absentes, sans jamais
  toucher à une valeur déjà présente — pour ne pas écraser une traduction
  personnalisée. Mais une phrase **reformulée** garde sa clé : l'ancien libellé
  restait donc à l'écran pour toujours.

  L'application conserve désormais, à côté de vos fichiers de langue, une copie de
  ce qui était livré à la dernière fusion. Une valeur que vous avez toujours telle
  qu'elle a été livrée n'a pas été touchée : la nouvelle formulation la remplace.
  Une valeur qui en diffère est la vôtre, et elle est laissée intacte. Même règle
  pour les clés supprimées. Vos fichiers actuels sont sauvegardés en `.bak` lors de
  cette première mise à niveau, puisque rien ne permettait de trancher avant.

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
