# Ennemis physiques : prototype du fork

Ce travail vise des interactions inspirees de Blade & Sorcery dans BannerlordVR.
Ce prototype **ne reproduit pas encore la physique complete de Blade & Sorcery**.
Il compile contre le Bannerlord installe localement ; aucun essai avec casque ni
profilage de bataille en jeu n'a encore ete realise.

## Ce qui est code

| Interaction | Implementation actuelle | Limite |
| --- | --- | --- |
| Poings | Gachette pour fermer le poing vide, contact balaye entre deux poses, vitesse minimale, delai de 350 ms par cible et par main | Volumes spheriques approches, equilibrage a verifier en jeu |
| Saisie vivante | Grip de la main vide pres de la tete, du cou ou d'une main ; orientation du cou et rotation des articulations du bras | Pose uniquement : pas de ragdoll actif complet, pas d'immobilisation de l'IA ni de deplacement de sa capsule |
| Saisie de corps | Ressort amorti sur l'os du ragdoll natif deja actif | Pas d'activation forcee des corps endormis ou retires par le moteur |
| Coup de lame/estoc | Second reglage experimental, balayages le long de l'arme, distinction coupe/perforation d'apres les donnees de l'arme | Longueur bornee a 2,5 m, axe local Z presume ; pas de contact exact avec les maillages |
| Arme plantee dans un corps | Contrainte laterale amortie sur le ragdoll, retrait axial libre, liberation sur ecart excessif ou apres 8 s | Uniquement les corps, pas de resistance mecanique de l'arme tenue ni de lesion volumetrique |
| Cou | Volume distinct et degats de coupe/perforation comme les autres parties | Pas encore de geste d'egorgement specifique, saignement ou lesion anatomique |
| Retour haptique | Action OpenXR de vibration, requetes consommees sur le thread de rendu | Compile ; sensation et profils materiels non verifies avec casque |

Les degats passent par `Agent.RegisterBlow` : attribution au joueur, evenements de
coup et regles de mortalite du jeu. La valeur du dommage utilise une formule
explicite simplifiee tenant compte de l'armure ; ce n'est pas le calcul natif
complet. Les cibles munies d'un bouclier en main ou en garde sont ecartees des
coups physiques en attendant des collisions de parade plus precises.

## Budget et transitions

Par defaut, le prototype est desactive. Une fois active :

- Admission jusqu'a 4 m, retrait au-dela de 5 m, au plus 6 ennemis detailles.
- Au plus 128 agents examines par tick, par curseur circulaire. Avec 1 000 agents
  et 90 ticks/s, un parcours complet demande environ 8 ticks ; ce chiffre est un
  budget theorique, **pas une mesure de FPS**.
- En cas de saturation, remplacement par une cible plus proche ; les cibles
  tenues ou transpercees sont prioritaires tout en restant soumises au rayon de sortie.
- Tableaux de corps reutilises, au plus 16 points par corps, au plus 33 trajets
  d'echantillons d'arme par main. La selection ne parcourt pas toute l'armee a chaque tick.
- Cavaliers, montures, montee/descente, chute/saut, echelles, objets utilisables,
  positions assises et drapeaux scripts sont exclus. Cela doit etre confirme
  pendant les transitions de chaque animation du jeu, pas seulement a l'arret.
- Aucune interaction lorsque le joueur est monte, dans les menus, en calibration,
  hors mission `Battle`, en multijoueur ou avec une arme de tir equipee.
- Relachement au changement de mission/joueur, perte de suivi, saut temporel,
  desactivation ou depassement de distance ; les references aux anciens agents
  sont liberees. Les demandes haptiques sont limitees a 250 ms.
- Le suivi exige une pose de visee **et** de prise valide et suivie. Un echantillon
  natif vieux de plus de 250 ms est refuse. Pas de coup sur le premier echantillon.
- Les deplacements du joueur seuls ne declenchent pas les poings ; une rotation
  du poignet peut declencher une attaque de lame. Les obstacles sont verifies par
  rayon du moteur en ignorant l'entite du joueur.

Le mod ne change pas le root des ennemis, leur navigation, leur IA ni leur etat
vivant en cadavre pour provoquer artificiellement un ragdoll. Le controle physique
complet d'un ennemi vivant reste a developper avec une restauration fiable de l'IA,
de la capsule, des contacts et de l'animation.

## Activation et verification

Voir `config/EnemyPhysics.preview.cfg`. Ajouter ces reglages au fichier personnel
`Documents/Mount and Blade II Bannerlord/Configs/BannerlordVR.cfg`, puis redemarrer.
Le fichier d'exemple n'ecrase aucune calibration existante. `motion_hands=1` et
`enemy_physics=1` sont necessaires. `enemy_physics_weapon_damage=1` active separement
les essais de lame et de penetration ; ils sont desactives par defaut.

Compilation sans installation automatique :

```powershell
rtk git submodule update --init --recursive
rtk proxy dotnet build BannerlordVR.slnx -c Release '-p:BannerlordDir=D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord' -p:SkipDeploy=true
rtk proxy cmake -S native/BannerlordVR.Native -B build/native-cmake -A x64 -DBVR_ENABLE_OPENXR=ON
rtk proxy cmake --build build/native-cmake --config Release --parallel 6
rtk proxy .\build\physics-tests\BannerlordVR.PhysicsTests.exe
rtk proxy .\build\tests\BannerlordVR.MathTests.exe
rtk proxy powershell -NoProfile -File tools/test-native-preview.ps1
rtk proxy powershell -NoProfile -File tools/package-physics-preview.ps1
```

Le paquet contient uniquement les DLL du mod, son manifeste, les notices et
l'exemple de configuration. Le chargeur OpenXR de cette compilation est lie
statiquement ; Harmony reste fourni par le module `Bannerlord.Harmony` du jeu.
Ne pas melanger une DLL native d'une autre version avec la DLL C# de ce paquet.

## Preuves et limites de l'analyse

Source amont : `NotLooky/BannerlordVR`, commit
`bf11d5777766de17ea0e74c1078c87181b9fdb3f`. Le code publie reference des classes
`VrArmSeam`, `VrArmBones` et `VrArmIk` absentes de son historique. Ces appels ont
ete remplaces par un solveur CCD borne et les appels de diagnostic absents retires
pour rendre cette base compilable. Ce solveur doit lui aussi etre observe en VR.

REA a inspecte le fichier local `ThunderRoad.dll` (4 089 856 octets), SHA-256
`a48cf62cca919a4c13d04769f05c0133cef4f245bf45c0309c2262ffe323b443` : assembly
Unity/Mono contenant du CIL, MVID `7c3f68b0-6dd9-431c-b97d-3ea4fdbc55ec`.
Preuve : `ev_4a460375ae57ff4a6dabcf72700b8de9965f3576c6edcf1ded09179292060d55`.

L'inventaire REA des membres a ete refuse par sa limite de representation de
64 Mio ; couverture indisponible, preuve
`ev_8aff57beee95acea939c1301c4f469e1012e481f4cd9bc59dfa43e3e15f4e296`.
Une decompilation ciblee avec ILSpyCmd 9.1.0.7988 a ensuite servi a examiner les
classes `HandleRagdoll`, `Ragdoll` et `Damager`. Ce resultat ILSpy est une
reconstruction statique, pas une preuve d'execution ni une decompilation fournie
par REA. Les fichiers reconstruits sont conserves localement dans `.analysis/`,
exclus du depot et du paquet. Aucun code ou DLL de ThunderRoad n'est distribue.

Observations statiques : separation animation/etats physiques, ressorts et
amortissement, contraintes et collisions par partie du corps, profondeur de
penetration, nettoyage des prises. Traduction choisie ici : conserver le moteur
et les ragdolls de Bannerlord et ajouter des interactions limitees autour du joueur.
Une analyse statique ne prouve ni un comportement jouable ni son cout en bataille.

## A terminer pour l'objectif complet

- [ ] Backend de ragdoll actif des vivants, avec limites articulaires et collisions.
- [ ] Maintien et desequilibre du corps, recuperation et reprise fiable de l'IA.
- [ ] Contacts exacts arme/bouclier/corps, resistance de la lame et parade.
- [ ] Saignement, geste d'egorgement, lesions et effet de la profondeur des coups.
- [ ] Bras et tete saisis simultanement, deux mains sur un corps, limites anatomiques.
- [ ] Essais VR : perte de suivi, recentrage, menu, mort, suppression du corps,
      echelle, cavalier/desarconnement, montage/demontage, changement de mission.
- [ ] Mesures CPU, temps de frame et p95/p99 avec 100/500/1 000 agents ; adaptation
      eventuelle du nombre de corps et de la frequence a un budget mesure.

Les tests automatises couvrent les mathematiques des contacts, ressorts et gestes,
ainsi que les tests amont de transformations et ABI. Ils ne couvrent pas les
appels natifs de physique executes dans Bannerlord.

Validation effectuee le 19 octobre 2026 : compilation Release C# sans avertissement,
compilation native OpenXR reussie (avertissements deja presents dans les sources
amont), 4 020 verifications physiques reussies, suite mathematique amont reussie,
chargement reel de la DLL native et verification des refus d'entree/haptique hors
session. Le test de chargement n'initialise ni OpenXR ni les hooks de rendu.
