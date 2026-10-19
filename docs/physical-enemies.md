# Ennemis physiques : prototype du fork

Ce travail vise des interactions inspirees de Blade & Sorcery dans BannerlordVR.
Ce prototype **ne reproduit pas encore la physique complete de Blade & Sorcery**.
Il compile contre le Bannerlord installe localement ; aucun essai avec casque ni
profilage de bataille en jeu n'a encore ete realise.

## Ce qui est code

| Interaction | Implementation actuelle | Limite |
| --- | --- | --- |
| Poings | Gachette pour fermer le poing vide, raycast natif des membres entre deux poses, os retourne par le moteur, vitesse minimale, delai de 350 ms par cible et par main | Equilibrage et mouvement des cibles pendant le tick a verifier en jeu |
| Saisie vivante | Par defaut, pose de la tete et des mains ; option supplementaire de ragdoll natif du corps entier, forces sur les os et suspension temporaire de l'IA | Activation et desactivation observees sur un ennemi sans casque ; saisies/efforts VR non valides ; pas encore d'equilibre debout pilote par ressorts |
| Saisie de corps | Ressort amorti sur l'os du ragdoll natif deja actif | Pas d'activation forcee des corps endormis ou retires par le moteur |
| Coup de lame/estoc | Second reglage experimental, balayages natifs le long de l'arme, distinction coupe/perforation d'apres les donnees de l'arme | Longueur bornee a 2,5 m, axe local Z presume ; volumes orientes des parades, pas de contact exact avec les maillages |
| Arme plantee dans un corps | Contrainte laterale amortie sur le ragdoll, retrait axial libre, liberation sur ecart excessif ou apres 8 s ; peut activer le backend vivant si l'option est active | Pas de resistance mecanique de l'arme tenue ni de lesion volumetrique ; la prise en charge native d'un vivant dure au plus 6 s |
| Cou | Contact natif distinct ; option de coupe longitudinale avec tete tenue par l'autre main, seuil de 12 cm et rejet des tremblements | Detection du geste testee mathematiquement, pas encore en VR ; saignement et lesion anatomique a faire |
| Retour haptique | Action OpenXR de vibration, requetes consommees sur le thread de rendu | Compile ; sensation et profils materiels non verifies avec casque |

L'option de ragdoll vivant reagit egalement aux coups : une reaction de 600 ms est
demandee, et l'effort du coup est mis en attente jusqu'a confirmation de l'activation.
Le moteur ne recoit aucune force sur un ragdoll absent. Cela ne constitue pas encore
un controleur d'equilibre debout : l'ennemi peut s'effondrer pendant cette reaction.

Les degats passent par `Agent.RegisterBlow` : attribution au joueur, evenements de
coup et regles de mortalite du jeu. La valeur du dommage utilise une formule
explicite simplifiee tenant compte de l'armure ; ce n'est pas le calcul natif
complet. Les membres sont interroges par `Mission.RayCastForClosestAgentsLimbs` :
un allie ou cavalier plus proche bloque la trajectoire sans recevoir les degats
de ce prototype. Les armes et boucliers utilisent leurs boites locales reelles,
transformees dans leur orientation actuelle. On peut toucher autour d'un bouclier ;
il ne protege plus automatiquement tout le corps. Sans boite exploitable sur un
bouclier ajoute par un mod, une protection conservatrice demeure. Les maillages
exacts et la trajectoire du bouclier entre deux poses restent a traiter.

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
- Budget CPU logiciel de 1,5 ms par defaut (`enemy_physics_budget_ms`). Les nouvelles
  admissions n'utilisent que 35 % de cette cible ; leur boucle verifie le temps
  tous les 16 candidats. Apres surcharge durable, la limite descend progressivement
  jusqu'a un corps ; elle remonte apres 180 echantillons de marge durable. Les corps
  tenus ne sont pas supprimes pour satisfaire une statistique.
- Moyenne et p95/p99 des 256 derniers echantillons du travail du mod dans le journal.
  Le cout du controleur de recuperation des vivants est inclus. Le travail de
  simulation interne du moteur et le rendu sont **exclus** : ce budget logiciel
  n'est pas une limite dure du temps de frame ni un benchmark de bataille.
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

Le mod n'ecrit pas directement le root ni la navigation d'un ennemi et n'appelle
pas `AddAsCorpse` ou une methode de mort pour activer le backend vivant. Le ragdoll
est gere par le moteur. Avec cette option experimentale, l'IA est mise en pause
temporairement et ses entrees de mouvement sont neutralisees. Une pause deja
presente n'est pas retiree par le mod. Le retour a la locomotion et a l'equilibre
debout, le suivi de la capsule et la stabilite aux transitions restent a observer.

Le controleur distingue activation, simulation et recuperation : 350 ms maximum
pour constater l'activation, 6 s maximum de prise en charge, puis demande de fin
et attente de l'etat natif `Disabled`. Une vraie mort transfere le ragdoll au jeu.
Les callbacks de fin de mission et de suppression nettoient les references avant
la destruction des agents. Une montee a cheval demande immediatement le relachement.
Un delai de recuperation depasse 1,5 s restaure l'IA et desactive ce backend pour
la mission, sans ecriture speculative dans la memoire du jeu. Cela ne prouve pas
que le moteur a effectivement retire le ragdoll : ce cas doit etre teste en mission.

## Activation et verification

Voir `config/EnemyPhysics.preview.cfg`. Ajouter ces reglages au fichier personnel
`Documents/Mount and Blade II Bannerlord/Configs/BannerlordVR.cfg`, puis redemarrer.
Le fichier d'exemple n'ecrase aucune calibration existante. `motion_hands=1` et
`enemy_physics=1` sont necessaires. `enemy_physics_weapon_damage=1` active separement
les essais de lame et de penetration ; ils sont desactives par defaut.
`enemy_physics_living_ragdoll=1` active separement le backend natif des vivants,
sur saisie d'un point du corps ou penetration. Il reste desactive par defaut.
Le premier essai en jeu active le ragdoll mais echoue a observer sa desactivation.
Apres correction, un probe isole observe activation, mouvement et desactivation
avec conservation de la vie et depause de l'IA. Son utilisation jouable en VR
n'est pas encore validee.
La correction native est limitee au moteur dont l'empreinte est documentee dans
`native-ragdoll-findings.md` ; toute autre version est refusee. Les efforts,
le relevement naturel et la locomotion apres saisie restent a tester.

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

## Coupe au cou experimentale

`enemy_physics_neck_cut=1` ajoute un geste distinct des coups rapides. Il exige
egalement `enemy_physics_weapon_damage=1`, une epee ou dague infligeant des degats
tranchants, la gachette pressee et une prise sur la tete ou le cou avec l'autre
main. Le rayon natif de la lame doit toucher le cou du meme ennemi et franchir
les controles d'obstacle et d'equipement. La coupe reserve ce contact afin de
ne pas infliger simultanement un coup rapide.

Le detecteur exige au moins 12 cm de deplacement longitudinal relatif, continu,
en 0,08 a 1,5 s, borne par le mouvement reel du controleur. Il annule les
allers-retours, refuse la locomotion seule et recommence apres perte de contact,
changement d'ennemi, rupture de suivi ou changement important d'axe. Une coupe
ne se repete pas tant que le contact reste continu. Les degats passent par
`RegisterBlow`, avec une approximation d'armure ; aucun changement direct des
points de vie. Le saignement differe et l'anatomie des lesions restent a faire.
Seules les mathematiques du geste sont testees automatiquement : la detection
native du cou et le resultat en VR ne sont pas encore valides.

## Test isole du backend vivant

Le commutateur de developpement `enemy_physics_runtime_probe=1` saute
l'initialisation OpenXR. Il execute une seule activation puis liberation du
ragdoll d'un ennemi IA a pied, situe a plus de 20 metres du joueur, sans appliquer
de coup ni de force supplementaire. Utiliser uniquement une bataille personnalisee,
avec `enemy_physics=0`, `enemy_physics_living_ragdoll=0` et `motion_hands=0`.
Sauvegarder le fichier de configuration avant cet essai et le restaurer apres.

Le journal `BannerlordVR.log` produit des lignes `PHYSICS_PROBE`. Un resultat
`PASS` exige d'avoir observe l'etat physique actif, un deplacement du bassin
superieur a 2 cm, puis la physique desactivee, l'agent vivant avec sa sante
initiale et l'IA depausee. Un ennemi absent, une interruption ou un contexte
incompatible ne constitue pas une validation. Ce test ne prouve pas la qualite
du relevement, des interactions VR ou les performances d'une grande bataille.
Desactiver ce commutateur et redemarrer pour retrouver le fonctionnement VR.

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
L'analyse native supplementaire, ses identites de fichiers et ses preuves sont
detaillees dans [native-ragdoll-findings.md](native-ragdoll-findings.md).

## A terminer pour l'objectif complet

- [ ] Validation en mission du backend natif vivant code, de ses limites articulaires et collisions.
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
amont), 4 098 verifications physiques, de gestes et de transitions reussies, suite mathematique amont reussie,
chargement reel de la DLL native et verification des refus d'entree/haptique hors
session. Le test de chargement n'initialise ni OpenXR ni les hooks de rendu.
Les tests du controleur vivant utilisent un hote simule : ils prouvent l'ordre des
appels et le nettoyage de son automate, pas le resultat du solveur natif en jeu.
