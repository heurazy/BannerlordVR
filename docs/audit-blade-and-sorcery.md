# Audit : Blade & Sorcery face à BannerlordVR.Physics

État au 22 octobre 2026, branche `codex/physical-enemy-interactions` (commit `9823fde`).

Méthode : j'ai comparé le code décompilé de B&S (`ThunderRoad.dll`, 969 fichiers) et son catalogue de données (`bas.jsondb` : créatures, cerveaux, dommages, effets, compétences, sorts) avec le code du module `BannerlordVR.Physics` et celui du mod VR officiel (BannerlordVR 0.2.6-alpha.2). Chaque manque cite le système B&S d'où il vient et ses valeurs exactes quand elles existent.

Légende des priorités :

- **P1** : se voit à chaque combat, faisable, et c'est ce qui sépare le plus le mod de B&S.
- **P2** : apport réel, faisable, mais moins visible ou plus coûteux.
- **P3** : secondaire, ou très difficile dans Bannerlord.
- **Hors sujet** : magie et systèmes propres à l'univers de B&S. Listés pour être complet.

---

## 1. Ce qui est déjà fait

Ces systèmes existent et suivent B&S. Ils ne sont pas repris dans la suite.

| Système | B&S d'origine | Dans le mod |
|---|---|---|
| Ragdoll actif des vivants | `Ragdoll`, `RagdollPart`, pins position et rotation | `ActiveRagdollBody`, `RagdollPinDrive`, `RotationPin` |
| Déstabilisation, chute, relevage | `Creature.FallState`, `BrainModuleHitReaction.OnCycle` (3 s au sol, 3 s pour se relever) | `ActiveRagdollCycle` |
| Table de poussée (stagger léger, moyen, complet, déstabilisation) | `PushBehaviour`, niveaux par matériau | `PushReaction` |
| Coups de poing | `Damager_Punch`, `DamageModifier_Punch` | `PunchImpact` |
| Saisies (cou, tête, poignet, torse) et traîner un corps | `HandleRagdoll`, joint 5000 N/m, 3000 N max | `EnemyGrab`, `RagdollGrabRules` |
| Pénétration, estoc profond, embrochage, garde | `Damager.Penetrate`, `penetrationDeepDepth`, skewer | `BodyPenetration`, `PenetrationJoint`, `PenetrationModifiers` |
| Entaille qui glisse le long de la lame | `penetrationSlideDamper`, profondeur 3 mm | mode cut de `BodyPenetration` |
| Désarmement par estoc dans le bras | `BrainModuleEquipment.allowArmStabDisarm` | `Disarm` |
| Lames lancées qui se plantent | dommage de pointe en vol | `ThrownBlade` |
| Démembrement (cou, coude, poignet, genou, cheville) | `dismembermentMinVelocity` 12 m/s, angles 90° et 35° | `Dismemberment`, `LimbHide` (hook natif du moteur) |
| Casque qui vole à la décapitation | `sliceVelocityMultiplier` 0,5, `sliceSeparationForce` 3,5 | `SeveredProp` |
| Assommage par coup contondant à la tête | `SkillDiscombobulate` : 5 m/s, 6 s | `Dismemberment.KnocksOut`, `ActiveRagdollBody.KnockOut` |
| Sang (entrée, intérieur, sortie, cou qui coule) | `Effect_PenetrationFlesh`, `PenetrationDeepBleeding` | `BloodFx`, `WoundFlow` |
| Lâcher les armes à la mort | `BrainModuleDeath.dropObjectsDelay` 0 à 0,5 s | `LivingRagdollMissionLogic.DropDue` |
| Boucliers et lames ennemis solides | armes rigidbody | `ShieldBlock`, `BladeClash` |
| Parade qui annule l'attaque | `BrainModuleMelee.OnWeaponCollision`, `recoilMinWeaponSpeed` 1 m/s | `BladeClash`, `LivingRagdollMissionLogic.Parry` |
| Épée qui pivote dans la main au choc | joint main 5000 N/m et 1000 N·m/rad | `BladeClash.Push` |
| Coups légers encaissés sans réaction | push level 0, `minimumDamageToCancelAttack` 5 % | `PhysicalHit.Deliver` |
| Lame tenue par l'autre main (demi-épée) | poignées multiples sur l'item | `HalfSword`, plus `VrTwoHand` du mod officiel |
| Holsters, arc, arbalète, lancer, prise à deux mains | `Holder`, `ItemModuleBow`, poignées | mod VR officiel |

---

## 2. Combat au corps à corps

### 2.1 Riposte de l'ennemi après une parade (P1)

B&S `BrainModuleMelee` :
- `recoilNothingRiposteChance` = (0,25, 0,75). Après une parade, 25 % de chances que l'ennemi recule (stagger), 50 % qu'il ne se passe rien, 25 % qu'il riposte.
- `minMaxRiposteDelay` = 0,75 à 1,5 s.
- `riposteSpeedAdditiveModifier` = -0,05 par riposte enchaînée : il riposte plus lentement à chaque fois.
- `BrainModuleDefense.shieldRiposteChance` = 0,3 : 30 % de riposte après un blocage au bouclier.

Dans le mod, chaque parade donne le même résultat : arrêt, rembobinage, retour en garde. L'ennemi ne contre-attaque jamais plus vite après une parade. C'est ce qui rend les duels de B&S nerveux.

**À faire :** tirer au sort entre recul, rien et riposte. Pour une riposte, lancer une attaque du moteur (`EventControlFlags` ou `SetActionChannel` sur une attaque) après le délai.

### 2.2 Feintes (P2)

B&S `SkillFeintingSwings` : l'ennemi commence une attaque, l'annule et en lance une autre d'un autre côté. Bannerlord a ses propres feintes d'IA (`AIDecideOnAttackingContinuation`), mais pas déclenchées par le joueur. Il faudrait les rendre plus fréquentes quand le joueur garde sa lame immobile.

### 2.3 Esquive (P2)

B&S `BrainModuleDefense` :
- `dodgeEnabled` = vrai, recharge de 0,5 à 1 s (`minMaxDodgeCooldown`).
- `aboutToBeHitTiming` = 0,6 s : l'IA réagit 0,6 s avant l'impact prévu.
- `waitDodgeTime` = 0,06 s.

L'ennemi esquive en arrière ou sur le côté quand le joueur arme un coup. Dans Bannerlord, l'IA ne fait que bloquer (le mod officiel a `VrEnemyGuard`). Une esquive demanderait de déplacer l'agent (un petit pas en arrière via `MovementInputVector`) quand une lame rapide arrive.

### 2.4 Blocage physique de l'ennemi : sa garde suit la lame (P2)

B&S `BrainModuleDefense` :
- `parryDetectionRadius` 5 m, `parryCircleRadius` 0,6 m, `parryAdjustSpeed` 4.
- `shieldUsesIK` et `oneHandUsesIK` : l'IA place son arme ou son bouclier sur la trajectoire de la lame du joueur, avec de la cinématique inverse.

Dans Bannerlord, la garde de l'IA n'a que quatre directions (haut, bas, gauche, droite) et une pose fixe. Le mod officiel choisit la bonne direction (`VrEnemyGuard`), mais l'arme ne suit pas la lame du joueur au centimètre. On pourrait corriger la pose de garde par IK sur les os du bras (comme on écrit déjà les os pour le démembrement).

### 2.5 Désarmement par saisie du bras ou de l'arme (P1)

B&S `BrainModuleEquipment` :
- `allowArmGrabDisarm` = vrai, `grabDisarmPushLevel` = 2 : saisir l'avant-bras de l'ennemi et tirer fort lui fait lâcher son arme.
- `handHitDisarmPushLevel` = 2 : un coup assez fort sur la main désarme aussi.

Compétence `WeaponTheft` : saisir l'arme de l'ennemi par sa lame et la lui arracher.

Le mod ne désarme que par un estoc dans le bras. Saisir le poignet existe déjà (`GrabHandle.Wrist`) : il reste à ajouter le lâcher au-delà d'un seuil de force, et la prise de l'arme ennemie.

### 2.6 Désarmement sur bouclier déstabilisé (P3)

B&S `BrainModuleDefense.disarmOnBlockDestabilize` = vrai : quand un coup lourd déstabilise l'ennemi à travers son bouclier, il lâche ce qu'il bloque. Le mod transmet déjà la poussée (`BlockTransfer`), mais sans désarmer.

### 2.7 Coup de pied (P3)

B&S a `Damager_Kick`, `DamageModifier_Kick` et la compétence `MightyKick`. Le tableau de poussée `Kick` existe déjà dans `PushReaction`. Il manque le pied : sans traqueur de pied, B&S déduit le coup de pied du mouvement du joueur. Possible avec un bouton plus un geste, mais peu naturel en VR.

### 2.8 Pommeau et plat de la lame (P2)

Dans B&S, chaque arme a plusieurs damagers : le tranchant coupe, la pointe perce, le pommeau et le plat frappent en contondant. Dans le mod, une lame est toujours tranchante ou perçante, selon la vitesse et l'angle. Il manque :
- un coup de pommeau, qui est contondant et peut donc assommer (règle de Discombobulate) ;
- un coup avec le plat, contondant, qui pousse sans couper.

Il suffit de savoir où, le long de la lame, a eu lieu le contact (c'est déjà calculé : `hitAlong`) et l'orientation du tranchant.

### 2.9 Arme qui reste coincée dans un bouclier ou un objet (P2)

Dans B&S, une hache ou une épée peut se planter dans un bouclier en bois ou une table, et il faut tirer pour la libérer. Dans le mod, le bouclier ennemi arrête la lame mais ne la retient jamais.

---

## 3. Corps, gore et démembrement

### 3.1 Tête et membres coupés qui tombent comme de vrais objets (P1)

B&S `Ragdoll.SliceCoroutine` : la partie coupée devient un morceau de ragdoll séparé. Elle tombe, roule, peut être ramassée et lancée, et saigne (`SliceFleshChild`).

Le mod cache la tête ou le membre (palette du squelette écrasée) et ne lance que le casque. Rien ne tombe quand on coupe un bras ou une jambe, et la tête disparaît sans retomber.

**À faire :** comme pour le casque, créer une entité dynamique avec une copie du maillage du corps limitée aux os coupés. C'est le point difficile : les maillages de corps de Bannerlord sont skinnés et partagés. Une première version pourrait ne montrer que la tête, avec son maillage de visage, ou une copie figée du maillage posée là où était le membre.

### 3.2 Plaies visibles sur la peau et l'armure (P1)

B&S a `EffectModuleReveal` et `RevealDecal`. Chaque coupure ou trou s'imprime sur la texture du corps et de l'armure : on voit où l'ennemi a été touché, la plaie reste et elle saigne.

Rien de tel dans le mod. Bannerlord n'a pas de système de décals sur maillage skinné accessible en C#. Deux pistes :
- des décals de sang sur l'armure, via le système de décals du moteur, à vérifier par rétro-ingénierie ;
- un petit maillage « plaie » attaché à l'os touché (`AddComponentToBone`), qui suit le corps.

### 3.3 Sang au sol et sur les objets (P2)

B&S `Effect_DropBlood` (`EffectModuleDecal`) : chaque goutte qui touche le sol y laisse une tache. Le mod fait des particules qui disparaissent sans laisser de trace. Le moteur de Bannerlord a des décals de sang au sol pour ses propres coups : il faudrait trouver comment les appeler.

### 3.4 Démembrement aux épaules et aux hanches, et coupe du torse (P3)

Les articulations de B&S sont tête et cou, bras, avant-bras, mains, cuisses, mollets et pieds. Le mod coupe au cou, au coude, au poignet, au genou et à la cheville, mais pas à l'épaule ni à la hanche. Une coupe du torse en deux n'existe pas non plus dans B&S de base.

### 3.5 Membres coupés : seul le cou a été vraiment testé (P1)

Les coupes de bras et de jambes passent par les mêmes hooks natifs que la tête, mais n'ont jamais été vérifiées en jeu. Il reste à vérifier :
- que la main ou l'avant-bras disparaît bien ;
- que l'arme tenue tombe (`DropItem` sur la main coupée) ;
- qu'une jambe coupée fait tomber l'ennemi (déstabilisation forcée).

### 3.6 Dégâts de chute et d'impact du ragdoll (P2)

B&S :
- `DamageModifier_Ragdoll` : un corps qui percute un mur ou le sol prend des dégâts (dès 5 m/s, assommage forcé à partir de 6 à 7 m/s) ;
- `fallAliveDestabilizeHeight` = 5 m ;
- les créatures lancées se blessent.

Le mod a `ApplyRagdollImpact`, mais il n'a jamais été mesuré en jeu, et lancer un ennemi n'est pas vraiment possible (voir 4.2).

---

## 4. Saisies et interactions avec les corps

### 4.1 Étranglement (P1)

B&S :
- saisir le cou d'une créature vivante et serrer, c'est l'étrangler ;
- `HandleRagdoll` a des données `chokeDamage`, `chokeDamageInterval` et `chokeDamageVariation` ;
- `BrainModuleSpeak.speakOnChoke` : l'ennemi émet des sons étouffés ;
- il se débat (`BrainModuleGrabbed`) puis perd connaissance ou meurt.

Le mod a la saisie du cou et l'égorgement à la lame (`NeckCuts`, désactivé par défaut), mais pas l'étranglement à mains nues. La saisie existe déjà : il reste à infliger des dégâts dans la durée quand la gâchette est serrée, à couper la voix, puis à assommer (même règle que le KO).

### 4.2 Soulever et lancer un ennemi (P1)

B&S :
- `grabThrowMinVelocity` = 2 m/s (`BrainModuleGrabbed` et `BrainModuleDeath`) : lâché à plus de 2 m/s, le corps est lancé ;
- avec les deux mains, on soulève un ennemi entier.

Dans le mod, la force de portée est volontairement plafonnée (`CarryMaxForce`, et « trop fort » corrigé au poids plus un quart). Un vivant saisi au cou est tiré, pas soulevé ni lancé. Il faudrait un mode à deux mains qui porte le poids, et un lâcher qui transmet la vitesse de la main au ragdoll.

### 4.3 L'ennemi saisi se débat (P2)

B&S `BrainModuleGrabbed` :
- `freeOneHand` après `freeOneHandDelay` = 2 s ;
- `freeBothHand` après `freeBothHandDelay` = 5 s.

L'ennemi saisi frappe la main du joueur, se dégage, et ne reste pas inerte. Dans le mod, l'ennemi saisi est en pause d'IA tant que la physique le tient.

### 4.4 Bouclier humain (P3)

Dans B&S, on peut tenir un ennemi devant soi : les flèches et les coups des autres le touchent. Bannerlord calcule les projectiles dans son moteur. Je n'ai pas vérifié si le ragdoll tenu les arrête. Sinon, il faudrait rediriger vers lui les dégâts destinés au joueur.

### 4.5 Ramasser et manipuler n'importe quel objet (P3)

Dans B&S, tout objet du décor est physique et se saisit : chaises, tonneaux, cadavres, armes au sol. Le mod officiel ramasse les armes au sol (`VrGrab`). Le reste du décor de Bannerlord est statique pour la plupart des objets.

### 4.6 Objets cassables (P3)

B&S `Breakable` : caisses, tonneaux et lanternes cassent sous les coups. Bannerlord a des objets destructibles (portes, béliers) avec leurs propres règles de dégâts, mais pas les petits objets.

---

## 5. Intelligence et réactions des ennemis

### 5.1 Peur, panique et reddition (P1)

B&S `BrainModuleFear` :
- quand ses alliés meurent autour de lui, ou face à un démembrement, l'ennemi panique ;
- il se recroqueville (`cowerAnimationID` = `HumanCower`) pendant `cowerDuration` = 10 s, crie (`panicScreamDialogID`), ou fuit ;
- la compétence `Intimidation` le déclenche par des gestes.

Bannerlord a un moral et la fuite d'unités, mais pas de réaction individuelle à ce que fait le joueur. Un ennemi qui voit son voisin décapité devrait reculer ou fuir. On peut s'appuyer sur le moral du moteur (`AgentMoraleInteractionLogic`) : baisser fortement le moral des agents proches quand le joueur démembre ou égorge.

### 5.2 Voix et cris liés à la physique (P2)

B&S `BrainModuleSpeak` :
- `audioHitChance` = 1 : chaque coup donne un cri ;
- `audioFallVelocityThreshold` = 5 m/s : cri quand il chute vite ;
- `neckStabSilencelevel`, `headStabSilenceLevel` : un estoc au cou ou à la tête le rend muet, et le muet ne crie plus ;
- `speakOnChoke` : sons étouffés pendant l'étranglement ;
- le mouvement de la bouche est synchronisé.

Bannerlord joue des cris de douleur avec ses propres coups. Pour les coups du mod (`RegisterBlow`), à vérifier : un égorgé ou un étranglé ne devrait pas crier normalement. `Agent.MakeVoice` permet de jouer des voix précises.

### 5.3 Animation de mort qui finit en ragdoll (P2)

B&S `BrainModuleDeath` : selon le coup (estoc, feu, électricité), une animation de mort joue (`stabAnimationId`, etc.), puis le corps passe en ragdoll si on le pousse (`unpinDistance` 1 m, `unpinMaxAngle` 70°).

Le mod a son propre ragdoll de mort sur la lame, et Bannerlord ses animations de mort. Il reste à enchaîner proprement l'animation de mort et le ragdoll du mod quand le joueur touche le corps pendant qu'il tombe.

### 5.4 Furtivité et assassinat (P3)

B&S `BrainModuleDetection` et la compétence `Skulker` : l'ennemi n'entend ni ne voit le joueur derrière lui, et un estoc dans le dos ou au cou d'un ennemi qui ne voit pas tue en silence (niveaux de silence ci-dessus). Bannerlord a peu de furtivité en bataille, mais les missions de Bannerlord (prisons, ruelles) en ont. Bonus de dégâts dans le dos et silence : faisable.

### 5.5 Ennemi qui ramasse une arme (P3)

B&S `BrainModuleEquipment` : un ennemi désarmé ramasse une arme au sol ou dégaine une arme de rechange. Bannerlord le fait déjà en partie (l'IA ramasse les armes). À vérifier après nos désarmements.

---

## 6. Joueur

### 6.1 Ralenti (P2)

B&S :
- le ralenti est un pouvoir de base du joueur (`Spell_SlowTime`), avec `SlowMotionVolume` et les compétences `FocusedReflexes`, `PrecisionFocus`, `CombatFocus`, `HyperFocus` ;
- jauge de concentration, temps divisé.

Bannerlord a un ralenti (`Mission.Scene.SlowMotionMode` et facteur de vitesse), mais il doit être ajusté pour la VR : la tête du joueur doit rester en temps réel. C'est la fonction de B&S la plus copiée par les autres jeux VR. Faisable mais délicat : la physique du mod suppose un pas de temps réel.

### 6.2 Force et poids des armes (P2)

B&S :
- `forceMaxPosition` = 3000 N et `forceMaxRotation` = 250 N·m pour la main du joueur ;
- deux fois plus à deux mains (`forcePositionSpringDamper2HMult`) ;
- une arme lourde traîne derrière la main : elle met du temps à accélérer et à s'arrêter.

Le mod officiel a `VrWeight` (poids ressenti). Notre `BladeClash` utilise ces valeurs pour les chocs, mais une masse lourde ne traîne pas pendant un mouvement libre. À vérifier contre `VrWeight`.

### 6.3 Dégâts de chute du joueur (P3)

B&S `playerFallDamageCurve` et `playerHortizontalFallDamageCurve`. Bannerlord gère déjà les chutes à sa manière.

### 6.4 Escalade et nage (Hors sujet)

`RagdollHandClimb`, `BrainModuleSwim`, `WaterHandler` : le joueur grimpe sur les rebords avec les mains, et les corps flottent. Bannerlord ne s'y prête pas (échelles de siège gérées par le mod officiel).

---

## 7. Armes à distance

### 7.1 Flèches qui se plantent dans les corps physiques (P2)

B&S : une flèche est un item qui pénètre (`DamageModifier_PierceArrow`) et reste plantée dans la partie touchée, puis bouge avec le ragdoll et peut être arrachée. Bannerlord plante ses flèches à sa manière (attachées à l'os). Il faudrait vérifier qu'elles suivent notre ragdoll, que les flèches du joueur poussent le corps (poussée de projectile de B&S) et qu'on peut les arracher à la main.

### 7.2 Lancer : ricochet et rattrapage (P3)

Compétences `RicochetThrow`, `Telethrow`, `StaggerThrow` : un lancer peut ricocher sur une armure ou faire chanceler. `ThrownBlade` gère le rebond et la pointe qui se plante ; le reste est de la magie de B&S.

---

## 8. Hors sujet : magie et univers de B&S

Pour être complet, ces systèmes existent dans B&S mais n'ont pas leur place dans Bannerlord :

- **Sorts** : feu, foudre, gravité, et leurs fusions (`Spell_Fire`, `Spell_Lightning`, `Spell_Gravity`, `SpellMerge`).
- **Télékinésie** (`Spell_Telekinesis`).
- **Imprégnation des armes** (`Imbue` : lames de feu ou de foudre).
- **Mana, cristaux, arbres de compétences**.
- **États spéciaux** : brûlure, électrocution, lévitation (`Status_Burning`, `Status_Electrocute`, `Status_Floating`).
- **Potions et liquides** (`LiquidHealth`, `LiquidPoison`).
- **Golem et boss scriptés**.

---

## 9. Ordre de travail proposé

1. **Riposte après parade** (2.1) : petit travail, gros gain pour les duels.
2. **Étranglement** (4.1) : s'appuie sur la saisie du cou existante.
3. **Désarmement en saisissant le bras ou l'arme** (2.5).
4. **Tête et membres coupés qui tombent** (3.1), et tester les coupes de membres (3.5).
5. **Peur et fuite des ennemis proches** (5.1).
6. **Pommeau et plat de la lame** (2.8).
7. **Plaies visibles et sang au sol** (3.2, 3.3) : recherche moteur nécessaire.
8. **Soulever et lancer un ennemi** (4.2).
9. **Ralenti** (6.1).
10. Le reste selon les envies.

Ce document ne vérifie rien en jeu : il compare des sources. Chaque point devra être testé en VR une fois fait, comme le reste du mod.
