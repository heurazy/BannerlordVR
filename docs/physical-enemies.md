# Ennemis physiques : prototype du fork

Ce travail vise des interactions inspirees de Blade & Sorcery dans BannerlordVR.
Ce prototype **ne reproduit pas encore la physique complete de Blade & Sorcery**.
Il compile contre le Bannerlord installe localement ; aucun essai avec casque ni
profilage de bataille en jeu n'a encore ete realise.

## Ce qui est code

| Interaction | Implementation actuelle | Limite |
| --- | --- | --- |
| Poings | Gachette pour fermer le poing vide, raycast natif des membres entre deux poses, os retourne par le moteur, vitesse minimale, delai de 350 ms par cible et par main | Equilibrage et mouvement des cibles pendant le tick a verifier en jeu |
| Saisie vivante | Par defaut, pose de la tete et des mains ; option supplementaire de ragdoll natif du corps entier, forces sur les os et suspension temporaire de l'IA | Activation et desactivation observees sur un ennemi sans casque ; saisies/efforts VR non valides |
| Equilibre | Soutien optionnel du bassin et du torse, compensation de poids et amortissement, references capturees avant activation, appui au sol controle | Essais natifs en echec : montee du corps puis perte d'appui ; sans pas correctifs ni relevage anime |
| Saisie de corps | Ressort amorti sur l'os du ragdoll natif deja actif | Pas d'activation forcee des corps endormis ou retires par le moteur |
| Coup de lame/estoc | Second reglage experimental, balayages natifs le long de l'arme, distinction coupe/perforation d'apres les donnees de l'arme | Longueur bornee a 2,5 m, axe local Z presume ; volumes orientes des parades, pas de contact exact avec les maillages |
| Arme plantee dans un corps | Contrainte laterale amortie sur le ragdoll, retrait axial libre, liberation sur ecart excessif ou apres 8 s ; peut activer le backend vivant si l'option est active | Pas de resistance mecanique de l'arme tenue ni de lesion volumetrique ; la prise en charge native d'un vivant dure au plus 6 s |
| Cou | Contact natif distinct ; option de coupe longitudinale avec tete tenue par l'autre main, seuil de 12 cm et rejet des tremblements | Detection du geste testee mathematiquement, pas encore en VR ; lesion anatomique a faire |
| Saignement | Option distincte apres perte de vie reelle par coupe/perforation, doses differees via RegisterBlow, attribution et expiration ; profondeur maximale de penetration aggrave une blessure existante | Test terrestre isole reussi ; profils simplifies, pas de lesion volumetrique ni d'effets visuels ajoutes |
| Retour haptique | Action OpenXR de vibration, requetes consommees sur le thread de rendu | Compile ; sensation et profils materiels non verifies avec casque |

L'option de ragdoll vivant reagit egalement aux coups : une reaction de 600 ms est
demandee, et l'effort du coup est mis en attente jusqu'a confirmation de l'activation.
Le moteur ne recoit aucune force sur un ragdoll absent. Sans l'option de soutien
decrite ci-dessous, l'ennemi peut s'effondrer pendant cette reaction.

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
  Les couts du controleur de recuperation et des blessures sont inclus. Le travail de
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
presente n'est pas retiree par le mod. Un suivi apres liberation a observe le
retour du deplacement de l'agent sur dix secondes. L'aspect du relevage, le suivi
visuel de la capsule et la stabilite aux transitions restent a observer.

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
points de vie. Le saignement differe dispose d'une option distincte ; l'anatomie
des lesions reste a faire.
Seules les mathematiques du geste sont testees automatiquement : la detection
native du cou et le resultat en VR ne sont pas encore valides.

## Soutien actif experimental

`enemy_physics_active_balance=1` ajoute un soutien uniquement aux corps vivants
dont le ragdoll est possede par ce mod. La capture initiale exige deux os de pied
distincts, au moins un pied sur le monde fixe, des hauteurs de bassin/torse plausibles et une
masse totale comprise entre 30 et 200. Elle utilise les os declares par le
monstre, sans supposer que tous les humanoides ont les memes indices. Les deux
rayons de sol sont ensuite renouveles au plus toutes les 100 ms par corps.

Le moteur ajoute des forces continues au bassin et au torse, avec compensation
du poids, un amortissement et une montee sur 150 ms. A la premiere activation,
le pont versionne lit la masse des corps physiques uniques du ragdoll, via le
getter moteur identifie et son verrou de scene. Cette masse remplace la masse
globale de l'agent ; un refus de lecture ou une valeur hors plage annule le soutien.
La reference verticale suit le sol mesure, pas un pied qui monte ; le deplacement
des pieds ne peut donc relever lui-meme la cible du moteur.
L'effort cumule est borne a 1 800 unites de force par corps ; la correction
horizontale est bornee a trois unites d'acceleration et ne s'applique pas sans
appui confirme. Aucune pose, capsule ou position racine n'est teleportee.

Le soutien cede si le centre estime du corps sort de la zone des pieds de plus
de 28 cm, si le bassin s'effondre, si le
torse penche trop ou si l'appui disparait plus de 120 ms. Un pied leve n'elargit
pas la zone de soutien. Les forces s'effacent
sur 120 ms ; le corps peut ensuite tomber librement. Une discontinuite de pose
ou un tick superieur a 100 ms annule immediatement le soutien. La liberation,
la mort, un montage ou une action incompatible arretent egalement ce travail.
Il n'y a pas encore de pas correctifs, de musculature articulaire complete ni
de transition animee de relevage. Les tests mathematiques incluent un modele
independant de deux masses a 30/60/90/144 Hz ; ils ne reproduisent pas les
collisions et articulations de PhysX.

Les essais natifs ont valide la lecture de masse, l'application des forces et
leur arret, mais **pas le maintien debout**. Le dernier essai applique des efforts
sur 101 ticks, puis perd les appuis et cede ; le bassin chute ensuite jusqu'a
0,566 m sous sa position initiale. La cadence du solveur et la repartition des
efforts restent a verifier avant de proposer cette option comme jouable.

## Saignement experimental

`enemy_physics_bleeding=1` enregistre les coupes et perforations du prototype
seulement apres une perte de vie reelle d'au moins un point. Le profil depend
de la partie touchee et du dommage initial apres armure ; il ne simule pas les
vaisseaux. Une penetration plus profonde peut aggraver la blessure existante,
avec un proxy borne a 20 cm ; le retrait ou les oscillations ne l'aggravent pas
repetitivement. Les doses ulterieures n'appliquent pas une seconde reduction
d'armure. Elles passent par `RegisterBlow`, sans ecriture directe de sante,
sans force ajoutee et avec les drapeaux sans son et sans reaction d'impact.

Le gestionnaire conserve au plus 64 blessures et traite au plus quatre entrees
dues par tick. Chaque blessure produit au plus une dose de six points toutes
les 500 ms, dure au plus huit secondes initialement et douze secondes en cas
d'aggravation, avec un budget maximal de 96 points. Il n'accumule pas de rafale
apres une longue interruption. Les blessures persistent lorsque les mains
perdent le suivi ou que l'ennemi sort du rayon ; elles sont nettoyees a sa mort,
a sa suppression ou en fin de mission. Un squelette remplace est recherche par
nom d'os et partie du corps, sinon la blessure est abandonnee.

Le 19 octobre 2026, un test sans casque sur un ennemi eloigne a observe un coup
initial de deux points, deux doses differees d'un point, trois callbacks avec
attribution au joueur, puis expiration et ennemi vivant a 96 points de vie.
Journal local : `.analysis/wound-probe-20261019-pass.log`. Aucun ragdoll n'a ete
active. Ce test ne valide pas le contact de lame VR, un saignement mortel ou le
resultat visuel. Le commutateur de diagnostic `enemy_physics_wound_probe=1`
necessite egalement `enemy_physics_runtime_probe=1` et le saignement active ;
il est reserve a une bataille personnalisee terrestre, pas a une campagne.

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
- [ ] Validation VR du saignement, du geste d'egorgement et de la profondeur ; lesions anatomiques et effets visuels.
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
amont), 4 133 verifications physiques, de gestes, de blessures, de soutien et de transitions reussies, suite mathematique amont reussie,
chargement reel de la DLL native et verification des refus d'entree/haptique hors
session. Le test de chargement n'initialise ni OpenXR ni les hooks de rendu.
Les tests du controleur vivant utilisent un hote simule : ils prouvent l'ordre des
appels et le nettoyage de son automate, pas le resultat du solveur natif en jeu.
