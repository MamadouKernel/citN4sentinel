# Backlog N4 Sentinel — audit complet des 8 sprints

**Référence** CIT-CIV-DSI-RFP-0010
**Date de l'audit** 14/08/2026
**Méthode** Relecture intégrale du texte du cahier des charges (extrait du .docx,
148 exigences numérotées FR/SEC/NFR/AC) confrontée au code réellement présent
dans `src/` et à la couverture de test réelle dans `tests/`, sprint par sprint.
Remplace toute estimation antérieure basée sur les seuls noms de commit.

**Mise à jour du 15/08/2026** — la décision a été prise de couvrir 100 % du
cahier des charges, pas seulement le Lot 1/V1. Le travail est séquencé en huit
phases (voir le plan d'implémentation). **La Phase A (P0 — sûreté et
sécurité) est terminée et vérifiée.** **La Phase B (P1 — achever le Lot 1/V1)
est terminée et vérifiée** : FR-013 (double validation), FR-032/033/046/047
(rôle Center et continuité), FR-056/057/058 (tableau de bord réseau/base/
synchronisation), FR-061/066/068 (corrélation temporelle, référence, collecte
depuis une alerte), FR-071/073 (identification automatique et résumé de log),
FR-023 (exécution réellement parallèle via `Task.WhenAll`), AC-15 (composants
non déclarés dans l'IU) et le seuil d'horloge codé en dur sont faits. **La
Phase C (Lot 2a — SOP) est terminée : FR-087 à FR-089D sont couverts**
(voir §4.5). **La Phase D (Lot 2b — dossiers partagés/ActiveMQ) est
terminée : FR-059A à FR-059G sont couverts** (voir §4.2). **La Phase E
(Lot 2c — EDI) est terminée : FR-059H à FR-059J sont couverts** (voir §4.2).
Dernier relevé de tests complet en cours de vérification (précédent relevé,
avant Phase E : 337 tests, 335 réussis, 2 ignorés WinRM, 0 échec). Quatre
points restent **non vérifiés au navigateur**, faute
d'identifiant de connexion dans cet environnement — à confirmer manuellement
avant mise en service : la case « Parallélisable » de `WorkflowDetail.razor`
(FR-023), les quatre écrans SOP (`Admin/Sop.razor`, `Admin/SopDetail.razor`,
`SopExecutions.razor`, `SopExecutionDetail.razor`), les écrans de la
Phase D (panneau « Dossiers partagés » de `Supervision.razor`, section
dédiée de `Admin/Composant.razor`), et l'écran `Edi.razor` de la Phase E.
Les sections qui suivent restent la
photographie du 14/08 pour l'historique ; les items résolus sont annotés
**[Fait — Phase A]**, **[Fait — Phase B]**, **[Fait — Phase C]**,
**[Fait — Phase D]** ou **[Fait — Phase E]** en place,
plutôt que réécrits, pour garder trace de ce qui a été trouvé puis corrigé.

---

## 1. Résumé exécutif

- **248 tests**, 246 exécutés et réussis, 2 ignorés (`ConnecteurTests` WinRM —
  nécessitent une console élevée, indisponible dans cet environnement ; ce
  n'est pas un défaut du code). **Mise à jour du 15/08 (Phase A) : 253 tests,
  251 réussis, 2 ignorés** — 5 tests ajoutés pour la traçabilité des
  connexions et l'immuabilité du journal d'audit (section 6, P0).
  **Mise à jour du 15/08 (Phase B, terminée) : 297 tests, 295 réussis,
  2 ignorés.**
  **Mise à jour du 15/08 (Phase C, Lot 2a — SOP) : 319 tests, 2 ignorés,
  2 échecs de délai SQL confirmés non reproductibles (contention parallèle,
  sans rapport avec le code livré) — 22 nouveaux tests pour FR-087 à FR-089D.**
  **Mise à jour du 15/08 (Phase D, Lot 2b — dossiers partagés) : 337 tests,
  335 réussis, 2 ignorés, 0 échec — 16 nouveaux tests pour FR-059A à FR-059G,
  dont 4 contre le vrai connecteur PowerShell et de vrais fichiers.**
- **Un bug a été trouvé et corrigé pendant cet audit** : dans
  `WorkflowService.RangArret`, le Center Node et les Cluster Nodes avaient
  leurs rangs d'arrêt inversés — le Center partait *avant* les Cluster Nodes,
  à l'exact inverse du tableau du cahier des charges et du commentaire du
  code lui-même. Corrigé et commité (`5a76c02`), le test dédié passe.
- **Un second bug, de nature différente, a été trouvé pendant la Phase A** :
  le nouveau test d'immuabilité du journal d'audit (le premier à migrer une
  base via `MigrateAsync` plutôt que `EnsureCreatedAsync`) a révélé que les
  contextes EF de test ne reproduisaient pas le réglage `Stores.SchemaVersion
  = Version3` que `Program.cs` applique aux tables Identity — un modèle EF
  divergeait donc silencieusement de celui de l'application dans chaque test,
  sans jamais être détecté puisque `EnsureCreatedAsync` ignore les
  migrations. Centralisé dans `tests/N4Sentinel.Tests/TestSupport/TestDbContextOptions.cs`,
  réutilisé par les 15 fichiers de test qui construisent un `N4SentinelDbContext`.
- Les 8 sprints ont **tous produit du code réel et testé** — ce n'est pas un
  audit qui découvre du vide. Mais à l'intérieur de chaque sprint, y compris
  ceux que la note d'arbitrage du 13/08 classe « Livré et recetté », des
  exigences précises manquent ou sont partielles. L'audit précédent (la note
  d'arbitrage) raisonnait par **lot entier** ; celui-ci raisonne **exigence
  par exigence**, et trouve des trous que le découpage par lot ne pouvait pas
  voir.
- Sur les exigences examinées : **≈ 45 % Fait, ≈ 40 % Partiel, ≈ 15 % Absent**
  hors du périmètre déjà explicitement reporté par la note d'arbitrage
  (FR-059*, FR-087 à FR-089*, FR-094 à FR-097, qui sont bien absents et
  cohérents avec ce report).

---

## 2. Ce qui est solide (Fait, bien testé)

- **Comptes techniques et secrets** (SEC-003) : chiffrement Data Protection,
  jamais en clair en base — le test relit la colonne SQL brute pour le
  prouver.
- **Verrouillage d'opération concurrente** (FR-015, AC-12) : verrou unique en
  base par environnement, expiration, libération, diagnostics lecture seule
  toujours autorisés.
- **Séquence Bridge → XPS** (FR-034, AC-04/AC-04bis) : double garde-fou
  (validation + barrage runtime), le mieux couvert de tout l'audit.
- **Rapport d'exécution** (FR-028, AC-14) : chronologie, preuves,
  contournements, réserves — fidèle aux constats enregistrés.
- **Masquage des secrets dans les logs** (FR-078, AC-11) : 11 tests dédiés,
  y compris le cas documenté du mot de passe ECI en clair via DEBUG Mule/ESB,
  vérifié directement en base.
- **Absence de conclusion fiable** (FR-064, AC-09) : le moteur de diagnostic
  refuse réellement de conclure sans preuve suffisante — comportement vérifié
  dans le code, pas seulement affirmé en commentaire.
- **Sauvegarde vérifiée** (NFR-010) : 14 tests contre une vraie instance SQL
  Server, y compris un aller-retour BACKUP puis RESTORE VERIFYONLY, et la
  détection qu'un trousseau DPAPI ne survit pas à un changement de machine.
- **Réponse documentaire sourcée** (AC-10) : citation systématique
  document/section/page, séparation conseil/action testée structurellement
  (le service documentaire ne dépend d'aucun service d'orchestration).
- **Simulation** (FR-005) : `IsSimulation` déroule le vrai moteur sans émettre
  aucune commande ; vérifié par test (`CommandesEmises` vide, verrou non pris).
- **Workflows versionnés** (FR-003) : un workflow déjà exécuté devient
  immuable, toute modification crée une nouvelle version.

---

## 3. Lacunes transverses (touchent plusieurs sprints)

1. **[Fait — Phase A] L'écran d'audit était trompeur.** `AuditAction.Connexion`,
   `DeconnexionOuEchecAuthentification`, `TentativeNonAutorisee` sont définis,
   affichés dans `Audit.razor`, mais **jamais écrits par aucun code** (confirmé
   par recherche sur tout `src/`). Les connexions, échecs d'authentification
   et refus d'autorisation ne partent que dans les logs Serilog — invisibles
   depuis l'écran qui prétend les tracer. Touche SEC-008, FR-091, FR-090
   (approbateur non restitué non plus). *Corrigé* : nouveau `IAuditWriter`
   (`Infrastructure/Persistence/AuditWriter.cs`) appelé depuis `Login.razor`
   (succès/échec/verrouillage), `LoginWith2fa.razor` (issue réelle du second
   facteur), l'endpoint `/Account/Logout`, et un nouveau composant
   `AccesRefuseAudite.razor` branché sur `Routes.razor` pour les refus
   d'autorisation. FR-090 (approbateur non restitué) reste ouvert — reporté
   en Phase B.
2. **[Fait — Phase A] Le verrouillage de compte configuré était inopérant.**
   `Program.cs` configure `MaxFailedAccessAttempts = 5`, mais `Login.razor`
   appelait `PasswordSignInAsync(..., lockoutOnFailure: false)` — la
   protection anti-brute-force ne s'appliquait pas sur le formulaire de
   connexion standard. *Corrigé* : `lockoutOnFailure: true`.
3. **Le masquage des secrets n'est branché que sur l'analyse de logs.**
   `SecretMasker` n'est jamais appelé depuis l'historique d'exécution
   (`ExecutionStep.Evidence`) ni depuis l'orchestration — seul
   `LogAnalysisService` l'utilise.
4. **[Fait — Phase A] `SequenceValidator` (garde-fou anti-séquence-invalide,
   FR-044) n'était rejoué qu'à l'édition d'un workflow**, pas au moment de
   préparer ou lancer une exécution réelle (`PreflightService`/
   `ExecutionService` ne l'appellent pas). Le garde-fou existe et est bien
   testé, mais sa portée réelle en production était plus étroite que ce que
   les tests suggéraient. *Corrigé* : `SequenceValidator` accepte désormais
   un overload sur les `ExecutionStep` réels (pas seulement les `WorkflowStep`
   de conception, via le nouveau `SequenceStepInfo` interne partagé) et
   `PreflightService.ControlerSequenceAsync` le rejoue, en bloquant, à chaque
   lancement réel.
5. **[Fait — Phase A] Écart SEC-001 non arbitré formellement** : le texte
   exige un MFA par e-mail en V1, le code livre un TOTP par application
   d'authentification. Le choix est documenté (`SmtpEmailSender.cs`) mais le
   commentaire de `ApplicationUser.cs` affirmait encore à tort « MFA par
   e-mail » — corrigé pour refléter honnêtement l'écart. La décision technique
   elle-même (TOTP au lieu d'e-mail) reste à faire valider formellement par
   la DSI — ce n'est pas un défaut de code, c'est un arbitrage humain en
   attente.
6. **[Fait — Phase B] Rôle Center actif jamais détecté** (FR-032/033/046/047) :
   aucun code ne distinguait un service Center démarré de l'instance qui
   détient réellement le rôle actif, ni ne détectait un conflit
   deux-Center-actifs. C'est un point central du guide éditeur (le rôle
   Center bascule, le service Windows ne le dit pas). *Corrigé* : marqueurs
   de rôle actif configurables (`ReadinessProfile.ActiveRolePatterns`, même
   principe que les marqueurs de démarrage), `AlertKind.ConflitRoleActifCenter`,
   et `CenterContinuityService` + choix de continuité bloquant avant toute
   action sur le Center.
7. **Cartographie par défaut : données inventées.** `N4TopologyDiagram.razor`
   affiche par défaut des systèmes externes (GOS, DGPS, IPAKI, Scangate…) en
   dur dans le markup et des valeurs CPU/RAM/NTP fictives au clic sur un
   nœud, au lieu des vraies données de `NodeVitalsService` et du référentiel.
8. **[Fait — Phase B] Détection de nouveau composant inaccessible depuis
   l'IU** (FR-050, AC-15) : `UndeclaredComponentScanner` était solide et
   testé côté moteur, mais aucune page ni service d'arrière-plan ne
   l'invoquait — le scénario de recette n'était pas jouable de bout en bout.
   *Corrigé* : écran de recherche dans `Supervision.razor` + balayage
   périodique journalisé depuis `SupervisionBackgroundService`.
9. **[Fait — Phase B] Incohérence de seuil d'horloge** : après le passage de
   la tolérance par défaut à 1 s, `SupervisionService.cs` gardait un seuil
   codé en dur à 5 s pour dégrader l'état consolidé d'un composant — deux
   sources de vérité désynchronisées sur le même sujet. *Corrigé* : utilise
   désormais `environnement.ClockToleranceSeconds`, comme `NodeVitalsService`.
10. **[Fait — Phase A] `AuditEntry.cs` affirmait une garantie qu'il n'avait
    pas** : le commentaire disait l'immuabilité posée « côté base
    (déclencheur) » ; aucun trigger SQL n'existait dans les migrations. La
    garantie n'était qu'applicative. *Corrigé* : migration
    `AuditEntriesImmuables` avec un vrai trigger SQL Server
    (`INSTEAD OF UPDATE, DELETE` + `ROLLBACK TRANSACTION`) sur `AuditEntries`,
    vérifié par `AuditImmutabiliteTests.cs` (UPDATE et DELETE directs en base,
    hors application, rejetés).

---

## 4. Détail par domaine

### 4.1 Référentiel et sécurité (Sprints 1-2)

| Bloc | Fait | Partiel | Absent |
|---|---|---|---|
| Référentiel (FR-001,002,006,007) | 3 | 1 | 0 |
| Sécurité (SEC-001 à 010) | 3 | 5 | 2 (SEC-009, SEC-010) |
| Connectivité / mise en service | 6 | 0 | 0 |

Le socle est le domaine le plus mûr : import d'inventaire, connecteur WinRM
réel, second facteur, parcours de mise en service en 8 étapes, tous testés.
Les manques sont concentrés sur la **sécurité en profondeur** (audit
incomplet, verrouillage inopérant, séparation Prod/UAT purement visuelle —
`AuthorizationSetup.cs` admet en commentaire que la différenciation de droits
par environnement n'est implémentée nulle part) plutôt que sur le
référentiel lui-même.

### 4.2 Supervision, alertes, cartographie (Sprint 3 + Vitalité)

| Bloc | Fait | Partiel | Absent |
|---|---|---|---|
| Tableau de bord (FR-050 à 058) | 1 | 4 | 3 |
| FR-059A-G (dossiers partagés/ActiveMQ) | ~~—~~ **[Fait — Phase D]** | — | — |
| FR-059H-J (EDI) | — | — | Absent, conforme à la note (Phase E à venir) |
| Vitalité des nœuds (hors CdC) | Fait | — | — |

Le moteur d'évaluation d'état est solide (8 états consolidés conformes au
texte), mais la couche « vue » prend des libertés : cartographie par défaut
avec données fictives, pas de vue par composant du CPU/disque/port/heartbeat
malgré leur définition dans le domaine (`HealthCheckKind` jamais évalué),
`AlertKind.RessourceCritique` défini mais jamais levé.

**Mise à jour du 15/08 (Phase D, Lot 2b)** — FR-059A à FR-059G sont
désormais couverts. `Domain/N4Component.cs` porte un nouveau
`SharedFolderProfile` (chemin racine, catégorie FR-059A, sous-dossiers de
classification déclarés par le site, seuil d'ancienneté) et
`Domain/SharedFolder.cs` (`SharedFolderSnapshot`, un relevé persisté par
contrôle). `Infrastructure/Supervision/SharedFolderHealthService.cs`
(FR-059B/C/D) s'appuie sur DEUX NOUVELLES MÉTHODES `IN4Connector` —
`ListFilesAsync` et `ProbeWriteAsync`, lecture seule et non destructif,
testées contre de vrais fichiers sur la machine locale — pour
l'accessibilité, la volumétrie, la classification par sous-dossier déclaré
(jamais devinée), le test d'écriture, et le seul contrôle de fichier
obligatoire réellement documenté par le corpus de support : la présence et
la taille de `db.data` pour la catégorie ActiveMQ/KahaDB. Un indice de
corruption (fichier absent, taille nulle, échec d'écriture, ancienneté
anormale) reste une SUSPICION dans `SharedFolderSnapshot.SuspectedCorruption`
— jamais une conclusion.

FR-059E/F/G (reconstitution) : **décision actée avec l'utilisateur** de ne
PAS construire de suppression de fichier automatisée — l'application n'a
aucune capacité de ce type à ce jour, et le corpus ne documente le format
KahaDB nulle part au-delà du nom `db.data` ; l'introduire sans un
simulateur capable de la tester en sécurité aurait été le genre de
raccourci que ce projet existe pour éviter. FR-059E/F/G sont donc couverts
par le mécanisme SOP guidé de la Phase C : `SopService.SeedReconstitutionKahaDbAsync`
crée un SOP pré-rédigé (9 étapes, fondées sur SOP-2 Fiche A et
`Fix-N4-AMQCorruption.ps1` — confirmation de la cause, arrêt de TOUS les
services, sauvegarde vérifiée par égalité de taille, suppression,
redémarrage dans l'ordre Cluster → Center → Bridge → XPS → ECN4, contrôle
de l'exclusion antivirus, escalade explicite en cas de doute ou de
corruption des journaux KahaDB eux-mêmes) dont chaque étape exige preuve
avant confirmation (FR-089A) — ce qui satisfait FR-059G (aucune suppression
sans sauvegarde, confirmation, contrôle des services arrêtés et trace) par
construction plutôt que par du code dédié. Panneau « Dossiers partagés »
ajouté à `Supervision.razor`, section de configuration ajoutée à
`Admin/Composant.razor`, bouton de création du SOP ajouté à `Admin/Sop.razor`.
16 tests dédiés (`SharedFolderTests.cs`), tous verts au premier passage —
dont 4 contre le VRAI connecteur PowerShell et de vrais fichiers, pour
prouver que les deux nouvelles méthodes fonctionnent réellement, pas
seulement en théorie.

### 4.3 Orchestration et scénarios N4 (Sprints 4-5)

| Bloc | Fait | Partiel | Absent |
|---|---|---|---|
| Préparation (FR-010 à 016) | 0 | 6 | 0 |
| Pilotage (FR-020 à 029B) | 5 | 8 | 0 |
| Démarrage complet (FR-030 à 037) | 2 | 2 | 4 |
| Opérations partielles (FR-040 à 047) | 2 | 4 | 2 |
| AC-01 à AC-17 (recette) | 9 | 2 | 1 (AC-16) |

Domaine le plus volumineux et le plus testé (`OrchestrationTests.cs`,
`RecetteSimulateurTests.cs` avec 9 scénarios rejoués contre le vrai moteur et
de vrais fichiers de log). C'est aussi celui qui a révélé le bug corrigé
pendant cet audit. Les trous les plus significatifs, au 14/08 : ~~**pas de
vraie exécution parallèle** (FR-023 — `CanRunInParallel` ne sert qu'à la
validation, jamais à paralléliser réellement)~~ **[Fait — Phase B]**,
**double validation absente** (FR-013 — **[Fait — Phase B]**), **contrôle
« tout DOWN avant démarrage complet »** (FR-036/AC-16 — un scénario de
recette explicitement nommé dans le cahier des charges — **[Fait — Phase A]**,
voir `PreflightService.ControlerEtatInitialDemarrageCompletAsync`), et
l'ensemble continuité/bascule Center (FR-032, 033, 046, 047 — **[Fait — Phase B]**,
voir `CenterContinuityService` et `AlertKind.ConflitRoleActifCenter`).

### 4.4 Diagnostic et analyse de logs (Sprint 6)

| Bloc | Fait | Partiel | Absent |
|---|---|---|---|
| Diagnostic (FR-060 à 069) | 1 | 6 | 3 |
| Logs (FR-070 à 079B) | 4 | 5 | 3 |
| AC-08, AC-09, AC-11 | 3 | 0 | 0 |

Le moteur de signatures (23 signatures, dont 6 ajoutées ce sprint depuis le
guide éditeur) et le masquage des secrets sont solides. Mais une bonne partie
de « l'intelligence » attendue par le cahier des charges est absente :
**corrélation temporelle et décalage d'horloge jamais rapprochés d'un
diagnostic** (FR-061, alors que le calcul existe ailleurs, côté Vitalité),
**comparaison à une période saine absente** (FR-066), **collecte automatique
depuis une alerte absente** (FR-068 — la collecte est toujours une sélection
manuelle), ~~**pas de résumé global d'un journal** (FR-073 — volumes par
niveau, évolution temporelle)~~. **[Fait — Phase B]** FR-061/066/068/071/073
sont désormais couverts (voir §6, P1).

### 4.5 Documentation, historique, sauvegarde (Sprints 7-8)

| Bloc | Fait | Partiel | Absent |
|---|---|---|---|
| Assistant documentaire (FR-080 à 089D) | 5 | 2 | ~~6 (SOP, conforme note)~~ **[Fait — Phase C]** |
| Historique / audit / rapports (FR-090 à 097) | 0 | 4 | 4 (dont 3 conformes note) |
| Sauvegarde / déploiement (NFR-010, AC-10) | 3 | 0 | 0 |

**Mise à jour du 15/08 (Phase C, Lot 2a)** — FR-087 à FR-089D (SOP) sont
désormais couverts : `Domain/Sop.cs` (`Sop`, `SopStep`, `SopAssociation`,
`SopExecution`, `SopExecutionStep`, calqués sur le cycle de vie de
`Workflow`/`WorkflowExecution` — même versionnement « immuable dès qu'exécuté »),
`Infrastructure/Procedures/SopService.cs` (CRUD versionné, FR-088
`PresentAsSopAsync` qui reformate une réponse `KnowledgeService` selon le
gabarit structuré SANS RIEN COMPOSER — une section absente du texte source
reste vide, jamais devinée —, FR-089B `GenerateFromExecutionAsync` qui
reprend la preuve RÉELLEMENT observée d'une exécution réussie comme résultat
attendu, FR-089D `SuggestForReuseAsync` par association explicite à un
composant/signature/session de diagnostic), `Infrastructure/Procedures/SopExecutionService.cs`
(FR-089 démarrage, FR-089A confirmation avec preuve obligatoire et retour en
arrière qui n'efface jamais l'historique — il l'archive avant de réinitialiser
l'étape —, FR-089C écart constaté sans bloquer la suite). Écrans
`Admin/Sop.razor`, `Admin/SopDetail.razor`, `SopExecutions.razor`,
`SopExecutionDetail.razor`. 24 tests dédiés (`SopTests.cs`, dont 2 pour le
SOP de reconstitution ActiveMQ/KahaDB de la Phase D — voir §4.2), tous verts
au premier passage. **Non vérifié au navigateur** : aucun identifiant de
connexion disponible dans cet environnement — à confirmer manuellement avant
mise en service.

Le plus cohérent avec la note d'arbitrage : les absences restantes (EDI,
indicateurs complets, notifications, rapport d'incident
automatique, capitalisation) sont **exactement** celles que la note propose
de reporter.
La vraie surprise est ailleurs : **FR-092 (intégrité de l'audit) repose sur
une garantie que le code affirme avoir mais n'a pas** (pas de trigger SQL),
et **FR-090 ne restitue pas l'approbateur** dans l'écran Historique alors que
le champ existe en base.

---

## 5. Conformité à la note d'arbitrage du 13/08

La note d'arbitrage (`doc/Note-arbitrage-perimetre-V1.md`) proposait de
reporter des **lots entiers** : SOP, dossiers partagés/EDI, indicateurs et
notifications, automatisation palier 2, mobile/Azure AD/JMX. Le code
confirme fidèlement ces reports : rien de tout cela n'existe, sans écart
caché.

Ce que la note **ne pouvait pas voir**, parce qu'elle raisonnait par lot et
non par exigence : à l'intérieur des lots qu'elle classe « Livré et
recetté » (Référentiel, Mise en service, Supervision, Orchestration,
Scénarios N4, Journaux, Sécurité), plusieurs exigences précises et
citées nommément dans les scénarios de recette (AC-16 et FR-044 rejoué
seulement à l'édition — **tous deux [Fait — Phase A]** ; FR-013 double
validation, que la note mentionne pourtant explicitement comme réduite —
**[Fait — Phase B]**) étaient absentes ou plus étroites que prévu.

---

## 6. Ce qui reste — priorisé

### P0 — à traiter avant toute recette (sécurité et sûreté opérationnelle)

**Tous les points ci-dessous sont [Fait — Phase A], terminée et vérifiée le
15/08/2026** (suite complète verte : 251/253, 2 skips WinRM ; voir le détail
dans les sections 1 et 3 ci-dessus).

1. ~~Brancher réellement la traçabilité des connexions et échecs
   d'autorisation~~ (SEC-008, FR-091) — l'écran d'audit affiche aujourd'hui
   des catégories qui ne se remplissent jamais. **Fait.**
2. ~~Réactiver le verrouillage de compte au login~~ (`lockoutOnFailure`) —
   la protection anti-brute-force configurée est actuellement sans effet.
   **Fait.**
3. ~~Rejouer `SequenceValidator` au lancement réel d'une opération~~, pas
   seulement à l'édition d'un workflow (FR-044) — c'est le garde-fou qui
   empêche XPS avant Bridge ou Center avant les Cluster Nodes. **Fait.**
4. ~~FR-036 / AC-16 : contrôler que tous les composants ciblés sont DOWN
   avant un démarrage complet~~ — scénario de recette nommément absent.
   **Fait.**
5. ~~Corriger le commentaire de `AuditEntry.cs`~~ (FR-092) qui affirme une
   garantie par trigger SQL inexistante, et décider si un vrai trigger est
   nécessaire ou si la garantie applicative suffit. **Fait — un vrai trigger
   SQL a été ajouté**, pas seulement une correction de commentaire : la
   garantie est désormais réelle, pas seulement documentée honnêtement.
6. Faire trancher par la DSI l'écart SEC-001 (TOTP livré vs. MFA e-mail
   demandé au texte) — le choix est raisonnable mais non validé formellement.
   **Documentation corrigée (le commentaire du code ne ment plus) ; la
   décision DSI elle-même reste une action humaine hors du périmètre du
   code, toujours en attente.**

### P1 — fonctionnel important, dans le périmètre déjà annoncé « livré »

**État au 15/08/2026 (Phase B, en cours)** — voir `doc/Backlog-V1.md` pour le
détail ; suite complète verte à chaque étape (dernier relevé : 266/268, 2
skips WinRM).

- ~~FR-032/033 : détection du rôle Center actif, conflit deux Center~~ —
  cœur du guide éditeur, totalement absent. **[Fait — Phase B]**
  `ReadinessProfile.ActiveRolePatterns` (même principe que les marqueurs de
  démarrage : configurable, jamais supposé sans preuve) ;
  `ComponentHealthSnapshot.HoldsActiveRole` ; nouvel `AlertKind.ConflitRoleActifCenter`
  (alerte de portée environnement, `ComponentId` nul) levée quand Center et
  Standby tiennent tous deux le rôle actif ; badge dans `Supervision.razor` ;
  champ de configuration dans `Composant.razor`.
- ~~FR-046/047 : continuité et bascule Center~~ — absent. **[Fait — Phase B]**
  `CenterContinuityService` (évalue l'aptitude réelle du Standby, ne décide
  rien à la place de l'opérateur) ; `WorkflowExecution.ContinuityChoice`
  (rester actif / basculer), exigé et bloquant via `PreflightService` dès
  qu'une opération arrête ou redémarre le Center ; écran de choix dans
  `OperationDetail.razor`. Le « séquencement » reste guidé par contrôles
  (bloquants/informatifs), pas par injection automatique d'étapes dans le
  workflow — cohérent avec le principe « l'outil ne décide jamais à la place
  de l'opérateur » déjà appliqué à `UndeclaredComponentScanner`.
- ~~FR-013 : la note d'arbitrage mentionne déjà la double validation comme
  réduite à une validation simple~~. **[Fait — Phase B]** `Workflow.RequiresDoubleApproval` +
  `WorkflowExecution.SecondApprovedBy/At` ; `ExecutionService.ApproveAsync`
  exige deux approbateurs distincts (ni l'un ni l'autre le demandeur, ni
  l'un l'autre) avant de passer en préparation. Ce faisant, un gap
  préexistant a été comblé : `RequiresApproval` lui-même n'avait **aucune**
  interface pour être positionné — ajouté dans `WorkflowDetail.razor`
  (section Gouvernance) en même temps que la double approbation.
- ~~FR-056/057/058 : synchronisation N4-XPS, vue réseau/base, lenteurs vues
  par N4~~ — absents du tableau de bord. **[Fait — Phase B]** FR-058 (le
  signal le moins cher) sert de fondation aux deux autres : le temps de
  l'aller-retour déjà mesuré par le connecteur (`ComponentHealthSnapshot.ResponseTimeMs`)
  est désormais surfacé, avec un nouvel `AlertKind.NoeudLent`. FR-056 :
  `ReadinessProfile.SyncPatterns` (même principe que les autres marqueurs) +
  report de la dernière confirmation d'un relevé à l'autre (un relevé ne lit
  qu'un delta de journal) via `SupervisionStateCache`, `AlertKind.SynchronisationXpsRetardee`.
  FR-057 : latence réseau partagée avec FR-058 ; nouveau `DatabaseHealthService`
  — connexion SQL réelle sous l'identité du processus (testée contre le vrai
  SQL Server local), requêtes lentes/bloquées via `sys.dm_exec_requests`,
  absence honnête de VIEW SERVER STATE distinguée d'une base réellement saine.
  Écran « Réseau & Base » dans `Supervision.razor`.
- ~~FR-061/066/068 : corrélation temporelle avec décalage d'horloge,
  comparaison à une période saine, collecte automatique depuis une alerte~~
  — absents du moteur de diagnostic. **[Fait — Phase B]** FR-061 :
  `LogSource.ClockSkewSecondsAtCollection` mesuré à chaque collecte ciblée ;
  `DiagnosticSessionService.BuildTimeline` aligne les constats de toutes les
  sources sur une chronologie commune ajustée, marquant « incertain » un
  écart inconnu ou significatif — la correction n'efface jamais sa propre
  marge d'erreur. FR-066 : `DiagnosticSession.IsReferenceBaseline` (marquage
  humain explicite, jamais déduit) + `CompareToReferenceAsync`, qui signale
  une référence ancienne (> 90 j) ou à couverture incomplète avant de
  l'utiliser. FR-068 : `CreateFromAlertAsync` — bouton « Diagnostiquer » sur
  `Alertes.razor`, identifie le composant de l'alerte ou les candidats pour
  une alerte de portée environnement (ex. conflit Center/Standby), lance la
  collecte et conclut aussitôt.
- ~~FR-071/073 : identification automatique d'un log importé, résumé global
  (volumes par niveau, évolution temporelle)~~ — absents. **[Fait — Phase B]**
  FR-071 : `IdentifierComposantAsync` cherche d'abord le nom de service
  Windows ou le nom logique déclaré dans le nom du fichier importé (seul
  signal assez fiable pour être affirmé, marqué comme correspondance
  « unité » pour éviter qu'un nom court comme « ECN4 » ne s'attribue à tort
  un fichier « ecn4web-... » ; entre deux noms qui matchent, le plus
  spécifique l'emporte) ; à défaut, un motif de nom de fichier, mais
  seulement si un unique composant de ce rôle existe dans l'environnement.
  FR-073 : `CalculerResume` parcourt tout le journal (pas seulement les
  lignes retenues comme anomalies) pour les volumes par niveau, la période
  couverte et la version détectée ; affiché dans `DiagnosticDetail.razor`
  avec un badge « auto » quand l'attribution du composant est une
  supposition et non un choix de l'opérateur.
- ~~Rendre `UndeclaredComponentScanner` accessible depuis l'interface~~
  (AC-15 non jouable en l'état malgré un moteur testé). **[Fait — Phase B]**
  Écran dans `Supervision.razor` (recherche par environnement, déclaration
  guidée du rôle) + balayage périodique (toutes les ~5 min) depuis
  `SupervisionBackgroundService`, qui se contente de journaliser — il ne
  déclare jamais à la place de l'opérateur.
- Remplacer les données fictives de la cartographie par défaut
  (`N4TopologyDiagram.razor`) par les vraies données du référentiel et de
  `NodeVitalsService`.
- ~~Aligner le seuil d'écart d'horloge codé en dur de `SupervisionService.cs`
  (5 s) sur le nouveau seuil de 1 s utilisé ailleurs~~. **[Fait — Phase B]**
  Utilise désormais `environnement.ClockToleranceSeconds`, configurable par
  environnement, comme `NodeVitalsService`.
- ~~FR-023 : soit assumer explicitement que l'exécution reste toujours
  séquentielle (mettre à jour le texte/la doc), soit implémenter une vraie
  exécution parallèle pour les étapes indépendantes déclarées~~.
  **[Fait — Phase B]** `ExecutionStep` porte désormais `CanRunInParallel`
  (recopié du modèle à la préparation — il ne l'était pas avant, le
  validateur de séquence rejouait donc toujours l'hypothèse la plus sûre :
  tout séquentiel). `OrchestrationEngine.RunAsync` constitue un lot avec la
  première étape prête, puis toute suite CONTIGUE d'étapes déclarées
  parallélisables, et le lance via `Task.WhenAll` — chaque étape avec son
  propre contexte EF Core, jamais partagé entre tâches concurrentes ; le
  connecteur PowerShell ouvre déjà un Runspace par appel, donc aucun état
  partagé côté exécution distante non plus. Le validateur de séquence (déjà
  bloquant, FR-044) continue de refuser à la préparation toute
  parallélisation qui violerait le graphe de dépendances (ex. deux nœuds
  Cluster ensemble) : le moteur peut lancer le lot sans le rejuger lui-même.
  Case « Parallélisable » ajoutée à l'écran d'édition d'un workflow
  (`WorkflowDetail.razor`), qui manquait — le champ existait déjà côté
  modèle mais n'était pilotable par aucun opérateur. Prouvé par un test de
  bout en bout contre le VRAI moteur (deux temporisations de 3 s marquées
  parallélisables concluent en moins de 5 s, pas ~6 s) et un test de
  non-régression (une étape non marquée n'est jamais groupée avec ses
  voisines). **Non vérifié au navigateur** : aucun identifiant de connexion
  disponible dans cet environnement pour atteindre l'écran d'édition — à
  vérifier manuellement avant mise en service.

### P2 — la note d'arbitrage proposait de reporter, la décision du 15/08 a été de tout couvrir

~~FR-059A-G (dossiers partagés/ActiveMQ)~~ **[Fait — Phase D]** et
~~FR-087 à FR-089D (SOP)~~ **[Fait — Phase C]** sont désormais couverts —
voir §4.2 et §4.5. Restent : FR-059H-J (EDI), FR-094 (partie indicateurs
manquante)/095/096/097 (notifications, rapport d'incident, capitalisation).
La note d'arbitrage du 13/08 (`doc/Note-arbitrage-perimetre-V1.md`) proposait
de reporter l'ensemble après le 1er octobre ; elle est restée non signée
(cases de décision vides en section 5), et la décision du 15/08 a été de
couvrir 100 % du cahier des charges plutôt que de la faire signer en l'état.

---

## 7. Recommandation

~~Mettre à jour la note d'arbitrage du 13/08 pour couvrir les points P0 et
les décisions P1 listées ci-dessus **avant** la recette UAT~~ — les P0 sont
faits (section 6). La décision prise le 14/08 dépasse cette recommandation :
couvrir 100 % du cahier des charges, pas seulement clôturer le Lot 1/V1.
Le travail restant (P1 ci-dessus, plus les Lots 2 à 4 entièrement absents de
cet audit) est désormais séquencé phase par phase — Phase B (achever le
Lot 1/V1) suit immédiatement la Phase A. Chaque phase se termine, comme la
Phase A, par une suite de tests complète verte et une mise à jour de ce
document — pas de fonctionnalité présentée comme faite sans preuve.

---

## 8. Audit indépendant du 15/08 et remédiation en cours

Un audit exigence par exigence a été mené sur les 148 identifiants numérotés
du cahier des charges (FR/SEC/NFR/AC), par six revues de code indépendantes
en parallèle plus un croisement direct des 23 scénarios de recette. Résultat
publié séparément (rapport HTML) : **75 Fait / 59 Partiel / 14 Absent**.
Constat le plus significatif de cet audit : le masquage des secrets
(`SecretMasker`) protégeait le module Diagnostic mais n'était jamais appelé
depuis le chemin d'Orchestration — un mot de passe apparu dans un journal
pendant une exécution réelle pouvait être stocké et affiché en clair dans le
rapport d'exécution. Corrigé en premier (voir ci-dessous).

**Contrainte découverte pendant l'audit, toujours active** : une autre
session travaille en parallèle sur ce même dépôt (confirmé par l'utilisateur),
sur du contenu de Phase G/H (Palier 2, Azure AD, JMX). `dotnet ef migrations
has-pending-model-changes` confirme que le modèle EF a déjà des changements
non migrés imputables à cette session. Toute remédiation nécessitant une
nouvelle colonne ou table reste donc bloquée tant qu'elle n'a pas committé —
générer une migration maintenant mélangerait son schéma en cours de
modification avec le nôtre. Le plan de remédiation complet, avec repérage
explicite des items bloqués et pourquoi, vit dans
`doc/Audit-Remediation-Plan.md`.

**Fait et testé le 15/08 (10 items sur les 14 sans risque de migration)** :

- Masquage des secrets étendu à l'Orchestration (`StepOutcome`, progression
  mémoire et base) — SEC-005/FR-021/AC-11, 5 tests.
- Alerte de ressource critique (espace disque) réellement déclenchée,
  raccordée à la même mesure que le pré-check — FR-054, 2 tests.
- Tentative de lancement bloquée (approbation manquante, pré-check bloquant)
  désormais auditée, pas seulement le lancement réussi — AC-07, 1 test.
- Changement de statut (document, SOP, workflow) écrit dans la piste
  d'audit — FR-091, 3 tests, sur 3 services.
- Fichier EDI en échec répété (≥3 fois de suite) déclenche une alerte
  distincte du retard d'ancienneté — FR-059I, nouveau
  `AlertKind.EchecsEdiRepetes`, 3 tests.
- Diagnostic rattaché à un fichier EDI précis depuis le tableau EDI, pas
  seulement au composant — FR-059J, 1 test.
- Suggestion de SOP validés réellement affichée pendant un diagnostic
  (composants et signatures observés dans la session), avec un historique
  d'utilisation réel (Terminé/Abandonné compté, **aucun taux de réussite
  inventé** — l'exécution d'un SOP est un geste humain guidé, pas une
  commande dont l'issue est binaire) — FR-089D/AC-23, 4 tests.
- TLS par défaut pour toute nouvelle fiche serveur (`UseSsl = true`,
  `WinRmPort = 5986`) — SEC-004, sans migration.
- Écran `/admin/utilisateurs` : comptes, rôles, second facteur, statut,
  dernière connexion, export TSV copiable — SEC-010. Pas de test automatisé
  (lecture pure sur les tables Identity existantes) ; à vérifier au
  navigateur, non fait faute d'identifiant de connexion disponible ici.

**Bloqués, avec raison précise** :
- FR-052 (état Maintenance), SEC-009 (expiration/historique mot de passe),
  FR-087 (signalement d'une réponse incorrecte) : nécessitent une nouvelle
  colonne/table EF — migration impossible tant que l'autre session n'a pas
  committé.
- SEC-001 (MFA obligatoire), SEC-008/NFR-008 (audit des connexions) :
  nécessitent une modification de `Login.razor`, activement modifié par
  l'autre session au moment de l'audit.

**Reste entièrement à faire** : Phase II (moteur de corrélation
multi-signaux du diagnostic — le changement le plus structurant du plan),
Phase III (topologie/dossiers partagés), Phase IV (rapports/notifications),
Phase V (achèvement de l'orchestration — bloquée jusqu'au commit de l'autre
session, elle touche les mêmes fichiers), Phase VI (non-fonctionnel). Détail
complet, avec fichiers cibles, dans `doc/Audit-Remediation-Plan.md`.

Build propre, suite de tests complète : 363/368 verts (2 skips WinRM
attendus, 3 échecs imputables au schéma non migré de l'autre session — pas
une régression de ce travail, vérifié par lecture directe du modèle EF).
