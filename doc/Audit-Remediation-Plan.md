# Plan de remédiation — atteindre 100 % du cahier des charges

**Origine** : audit indépendant du 15/08/2026 (voir la synthèse publiée), qui
a évalué 148 exigences numérotées à 75 Fait / 59 Partiel / 14 Absent.
Ce document convertit chaque écart en action concrète, organisée en phases,
pour fermer les 73 exigences Partiel/Absent une par une.

**État au 19/08/2026** : **Phase I (14/14), Phase II (11/11), Phase III (6/6).**
Suite de tests complète au vert : **504 tests, 0 échec**.

> **Deux défauts réels trouvés en vérifiant, pas en lisant.** Ils ne
> figuraient dans aucune ligne du plan :
>
> 1. **Le bouton « Annuler » échouait pendant qu'une exécution tournait.**
>    `RequestCancelAsync` enregistrait la demande via l'entité suivie ; le
>    moteur écrivant sur la même ligne au même moment, le jeton `RowVersion`
>    levait une `DbUpdateConcurrencyException`. L'annulation échouait donc
>    précisément quand on s'en sert : pendant une opération en cours. Corrigé
>    par un `ExecuteUpdateAsync` — l'annulation est un DRAPEAU que le moteur
>    lira à l'étape suivante, pas une modification concurrente de son état.
>    Même famille que le défaut trouvé au rejeu du scénario 5.
> 2. **La réinterrogation FR-024 ne s'exécutait jamais** (voir Phase III, 6).
>
> Le premier a été trouvé parce qu'un test échouait une fois sur deux en suite
> complète et jamais isolément. Il aurait été commode de le classer « test
> instable » : c'était un vrai défaut, sur le chemin d'annulation.
Les 4 items de Phase I qui restaient bloqués (3, 4, 6, 7) le sont désormais :
l'autre session a committé (`c10b66c`), ce qui a levé les deux verrous — le
modèle EF n'a plus de changement non migré, et `Login.razor` est stabilisé.
L'item 10 (FR-087), lui aussi marqué bloqué, est livré.

Sur la Phase II, l'essentiel était déjà livré par l'autre session ; **le seul
écart réel était FR-071**, et il n'était pas là où le plan le situait. La
détection de version, de type de journal et de fuseau existait ; ce qui
manquait était l'inférence du composant **à partir du contenu**, que le code
refusait explicitement de tenter. Livré, avec la prudence que le sujet exige
(voir la section FR-071 ci-dessous).

> **Le blocage migration est levé.** `dotnet ef migrations
> has-pending-model-changes` répond désormais « No changes have been made to the
> model since the last migration ». Les annotations « ⛔ migration bloquée »
> conservées ci-dessous sont **historiques** : elles expliquent pourquoi ces
> items ont attendu, pas où ils en sont.

**Régression corrigée au passage** : deux fichiers de tests d'interface
(`UI/HistoryComponentsTests.cs`, `UI/SopComponentsTests.cs`) cassaient la
compilation du projet de tests — référence à un `ExecutionHistoryService`
inexistant, API bUnit obsolète, et surtout des assertions
`MarkupMatches(".*texte.*")` qui **ne pouvaient pas passer** :
`MarkupMatches` compare du balisage, pas une expression régulière. Corrigés et
vérifiés — 93 tests d'interface au vert.

**Contexte d'écriture** : une autre session travaille en ce moment sur ce même
dépôt (confirmé par l'utilisateur), sur du contenu de Phase G/H — Palier 2,
Azure AD, JMX. Ce document sert de base commune : chaque phase indique son
**risque de collision** avec les fichiers déjà touchés par cette session
(`AutomationLevel.cs`, `N4Environment.cs`, `Workflow.cs`, `WorkflowExecution.cs`,
`OrchestrationEngine.cs`, `AzureAdAuthProvider.cs`, `JmxMonitoringService.cs`,
`DependencyInjection.cs`, `App.razor`, `Login.razor`, `OperationDetail.razor`,
`manifest.json`, `sw.js`), pour que le travail puisse être séquencé sans se
marcher dessus. Ne pas commencer une phase « Risque élevé » avant que l'autre
session ait committé son état.

> `doc/Note-arbitrage-perimetre-V1.md` (13/08) proposait de reporter SOP,
> dossiers partagés, EDI, notifications, palier 2 et Azure AD/JMX après le
> 1er octobre. Cette note est **dépassée** par la décision ultérieure de viser
> 100 % du cahier des charges — elle n'a jamais été signée — mais elle explique
> pourquoi certaines de ces fonctions n'existaient pas avant les phases C à H.

---

## Phase I — Corrections ponctuelles à fort impact, faible surface

**Risque de collision : faible.** Fichiers isolés (Diagnostic, Supervision,
Knowledge, Sop, Identity) que l'autre session ne touche pas actuellement.
À faire en premier.

| # | Fait | Exigence | Action | Fichier(s) cible |
|---|---|---|---|---|
| 1 | ✅ | SEC-005 / FR-021 / AC-11 | `SecretMasker` appliqué à la construction de tout `StepOutcome`, plus au flux de progression (mémoire + `ProgressMessage` en base) | `StepExecutor.cs`, `OrchestrationEngine.cs` — 5 tests (`OrchestrationMasquageTests.cs`) |
| 2 | ✅ | FR-054 | Cas `RessourceCritique` ajouté à `AlertService.Detecter()`, mesure d'espace disque remontée par `SupervisionService.EvaluateComponentAsync` (même seuil que le pré-check) | `SupervisionService.cs`, `AlertService.cs` — 2 tests (`AlertesTests.cs`) |
| 3 | ✅ | FR-052 | `MaintenanceUntil` sur `N4Component` ; la maintenance déclarée prime sur tout relevé — on n'affiche pas « indisponible » pour un composant qu'on a soi-même mis à l'arrêt | `SupervisionService.cs`, `N4Component.cs` |
| 4 | ✅ | SEC-001 | Second facteur exigé pour agir. Réglage global `SecondFacteurExigePourAction` sur les 4 politiques d'action, **et refus inconditionnel en Production** posé dans le pré-check — une politique d'autorisation ne connaît pas l'environnement visé, elle ne voit que des rôles ; seul le pré-check sait sur quoi porte l'opération | `AuthorizationSetup.cs`, `SecondFacteurRequirement.cs`, `PreflightService.cs` — 4 tests |
| 5 | ✅ | SEC-004 | `N4Server.UseSsl = true` et `WinRmPort = 5986` par défaut pour toute nouvelle fiche serveur (aucune migration nécessaire — pas de valeur par défaut Fluent API configurée) | `N4Server.cs` |
| 6 | ✅ | SEC-009 | `PasswordHistoryRecord` + `PasswordHistoryValidator` (refus de réemploi), `PasswordChangedAt`/`PasswordExpiresAt` sur `ApplicationUser` — migration `AddPasswordHistoryRecord` | `Infrastructure/Identity/*` |
| 7 | ✅ | SEC-008 / NFR-008 | Connexion, échec d'authentification et déconnexion écrits dans `AuditEntry` via `AuditWriter` | `Login.razor` |
| 8 | ✅ | FR-091 | `AuditAction.ChangementDeStatut` écrit dans `KnowledgeService.ValidateAsync/RevokeAsync`, `SopService.ChangeStatusAsync`, `WorkflowService.ChangeStatusAsync` (actor ajouté à `RevokeAsync`/les deux `ChangeStatusAsync`, qui ne le portaient pas) | 3 fichiers Infrastructure + 2 pages Razor — 3 tests dédiés |
| 9 | ✅ | AC-07 | Tentative de lancement bloquée (en attente d'approbation, pré-check bloquant) auditée via `TentativeNonAutorisee`, pas seulement le lancement réussi | `ExecutionService.cs` — 1 test |
| 10 | ✅ | FR-087 | Bouton « Signaler une réponse incorrecte » avec correction proposée facultative, **soumise à validation et jamais appliquée directement** ; revue des signalements sur la fiche document. Entité `KnowledgeCorrection`, migration `MaintenanceEtCorrectionsAudit` — le blocage de migration signalé le 18/08 est levé | `Knowledge.cs`, `KnowledgeService.cs`, `Documentation.razor`, `DocumentDetail.razor` |
| 11 | ✅ | FR-089D / AC-23 | `SuggestForReuseAsync` appelé depuis `DiagnosticDetail.razor` (composants + signatures observés dans la session) ; `GetUsageStatsAsync` ajouté — compte Terminé/Abandonné réel, **pas** de taux de réussite inventé | `SopService.cs`, `DiagnosticDetail.razor` — 4 tests |
| 12 | ✅ | FR-059I | Nouveau `AlertKind.EchecsEdiRepetes` (≥3 échecs consécutifs sur un même fichier), distinct du retard d'ancienneté | `AlertService.cs`, `Alert.cs` — 3 tests |
| 13 | ✅ | FR-059J | `DiagnosticSessionService.CreateFromEdiFileAsync` — session nommant explicitement le fichier/partenaire/statut ; bouton « Diagnostiquer » par ligne dans `Edi.razor` | `DiagnosticSessionService.cs`, `Edi.razor` — 1 test |
| 14 | ✅ | SEC-010 | Nouvel écran `/admin/utilisateurs` (comptes, rôles, second facteur, statut, dernière connexion) + export TSV copiable | `Admin/Utilisateurs.razor`, `NavMenu.razor` — pas de test automatisé (écran de lecture pure sur Identity), à vérifier en navigateur |

*(Les lignes 12 à 14 apparaissaient en double, dans leur rédaction d'origine
sans colonne de statut. Doublons retirés le 18/08 — les versions retenues sont
celles du tableau ci-dessus.)*

---

## Phase II — Moteur de diagnostic : corrélation multi-signaux

**Risque de collision : faible.** Entièrement dans `Infrastructure/Diagnostic/*`
et `Domain/Diagnostic.cs`, hors du périmètre de l'autre session.

C'est le changement le plus structurant de tout le plan : le moteur actuel ne
compare qu'une regex à une ligne (`LogAnalysisService.Correspond()`). Le
cahier des charges exige des règles qui combinent plusieurs signaux
(battement de vie **et** statut de service contradictoires, taille de file
positive **et** zéro consommateur, etc.).

**État au 18/08 : 11/11.**

| # | Fait | Exigence | Vérification |
|---|---|---|---|
| 1 | ✅ | FR-065 / §3.10.4 | Entité `CorrelationRule` (opérateur ET/OU/seuil sur plusieurs signaux), version et statut de validation sur `DiagnosticSignature` — migration `ReglesCorrelation` |
| 2 | ✅ | FR-061 | `ClockSkewSecondsAtCollection` sur `LogSource`, alimenté par la mesure live du connecteur ; la chronologie multi-sources en tient compte et le signale. Non mesurable sur un import manuel — et l'écran le dit plutôt que de supposer zéro |
| 3 | ✅ | FR-063 | `CounterEvidence` **et** `RuleVersion` sur `DiagnosticHypothesis` (et sur `DiagnosticSignature`) |
| 4 | ✅ | FR-066 | Période de référence + modes de comparaison — migration `ModesDeComparaisonReference` |
| 5 | ✅ | FR-067 | Archive d'escalade avec empreinte SHA-256 par fichier inclus (`HistoryService`) |
| 6 | ✅ | FR-068 | `SourceAlertId` sur `DiagnosticSession` ; collecte déclenchée depuis l'alerte critique |
| 7 | ✅ | FR-069 | `DiagnosticVerdict` compte 7 valeurs, dont `PlusieursCausesPossibles` et `InformationsInsuffisantes` |
| 8 | ✅ | **FR-071** | **Livré le 18/08** — voir ci-dessous |
| 9 | ✅ | FR-072/073/077 | Champs structurés (niveau, thread, classe, transaction) — migration `FR072_LogStructure` ; résumé par niveau et filtres présents |
| 10 | ✅ | FR-070/079/079B | Import d'archive `.zip` (`DiagnosticDetail.razor`), politique de rétention configurable — migration `PolitiqueRetention` ; fenêtre temporelle appliquée à la collecte |
| 11 | ✅ | Cycle incident (8 phases) | Migration `CycleDiagnostic8Phases`, `SourceAlertId` reliant détection → diagnostic |

### FR-071 — deviner l'origine d'un journal versé manuellement

L'infrastructure existait déjà (version, type de journal, fuseau détectés ;
identification du composant par le **nom de fichier**). Le manque réel était
l'inférence à partir du **contenu**, que le code refusait explicitement de
tenter.

Livré dans `Infrastructure/Diagnostic/OriginHeuristic.cs` (15 tests,
`OrigineJournalTests.cs`), migration `FR071_SuggestionOrigineJournal` :

- Trois signaux, du plus fiable au plus faible : **nom d'hôte déclaré**, puis
  **nom de composant déclaré**, puis **motif du catalogue de signatures**
  rattaché à un rôle (`AppliesToRole`). Les motifs ne sont pas codés en dur :
  ce que l'exploitation a appris de ses propres journaux sert à les
  reconnaître.
- **On suggère, on ne rattache pas.** Les champs `SuggestedComponentId` /
  `SuggestionEvidence` / `OriginAmbiguous` sont volontairement distincts de
  `ComponentId`. Un journal mal attribué est pire qu'un journal non attribué :
  il envoie la chronologie, la corrélation et l'intervention sur le mauvais
  serveur, sans que rien ne signale que c'était une supposition.
- **« Plusieurs candidats » et « aucun indice » restent deux états distincts.**
  Deux composants nommés dans le même journal (un composant en cite un autre —
  c'est banal) ne produisent aucune suggestion, mais l'ambiguïté est affichée.
- L'indice justificatif est montré à l'opérateur, extrait à l'appui, et il est
  prélevé sur le contenu **déjà masqué**.
- Règle de reconnaissance durcie pour le contenu : le nom doit former un jeton
  à lui seul. Les bornes « non alphanumériques » suffisent pour un nom de
  fichier, mais sur du contenu elles attribuaient « XPS » à tout journal
  mentionnant `SRV-N4-XPS` ou `com.navis.xps.Dispatcher`.
- Correctif attenant : `ContientCommeUnite` n'examinait que la **première**
  occurrence. Sur un journal entier, « ECN4 » apparaît le plus souvent d'abord
  au sein d'« ECN4Web » puis seul quelques lignes plus bas — le composant
  n'était alors jamais reconnu.

---

## Phase III — Supervision, dossiers partagés, EDI

**Risque de collision : faible à moyen.** `SupervisionService.cs` est proche
mais pas identique aux fichiers touchés par l'autre session — vérifier l'état
au moment de démarrer.

**État au 19/08 : 6/6.**

| # | Fait | Exigence | Vérification |
|---|---|---|---|
| 1 | ✅ | FR-050 | Nœuds alimentés par le référentiel (`Referential.GetComponentsAsync`) et non plus codés en dur ; serveur sans composant déclaré affiché sur la carte ; **dépendances déclarées visibles depuis la carte** (livré le 19/08, voir ci-dessous) |
| 2 | ✅ | FR-051 | `ConnectivityTester` : ping et test de port TCP sur le port WinRM du serveur **et** sur le port déclaré du composant. La table `ComponentHealthChecks` n'existe plus dans le modèle — elle a été retirée par la migration `RetraitControlesSanteEtModulesDemo` ; **l'action décrite à l'origine (« construire le moteur qui évalue cette table ») est donc périmée**, l'exigence est couverte autrement |
| 3 | ✅ | FR-057 | Le panneau « Réseau et base » affiche disponibilité, latence et requêtes lentes/bloquées réelles (`Supervision.razor`) |
| 4 | ✅ | FR-059B | `WriteLatencyMs` et `GrowthBytesPerHour` sur `SharedFolderSnapshot` — migration `CroissanceEtLatenceDossierPartage` |
| 5 | ✅ | FR-059D | Livré le 19/08 — voir ci-dessous |
| 6 | ✅ | FR-024 | `ResumeAsync` réinterroge `SupervisionService` pour les composants des étapes restantes. **Défaut corrigé le 19/08** : `execution.Steps` n'était jamais chargé (pas d'`Include`, pas de chargement différé sur ce contexte) — la boucle parcourait une collection vide et la reprise repartait sur un état de composant périmé, sans que rien ne le signale |

### FR-059D — suite donnée à une suspicion de corruption

Les indices de corruption étaient relevés mais rien ne disait ce qu'on en
avait fait. Ajoutés sur `SharedFolderSnapshot` (migration
`FR059D_SuiteSuspicionCorruption`, 5 tests) :

- `SopExecutionId` — la procédure lancée pour trancher, rattachée **au
  lancement** et non à la conclusion : c'est ce qui permet de retrouver les
  suspicions dont personne ne s'est occupé.
- `CorruptionConfirmed` en `bool?` — **trois états, pas deux**. `null` veut
  dire « pas encore tranché » et ne doit jamais se lire comme « rien à
  signaler » : c'est précisément la confusion qui laisse une corruption réelle
  sans suite. `GetSuspicionsEnAttenteAsync` répond à la question qui compte —
  non pas « a-t-on vu des indices », mais « en reste-t-il sans réponse ».
- L'infirmation est enregistrée au même titre que la confirmation : savoir
  qu'une suspicion était fausse évite de la relancer indéfiniment. Un constat
  écrit est exigé dans les deux cas.

### FR-050 — dépendances sur la carte : ce qui a été fait, et ce qui ne l'a pas été

**L'action décrite au plan était « dessiner les arêtes ». Ce n'est pas ce qui
a été livré, et c'est un choix assumé.** La carte est une grille responsive
(`grid-cols` avec points de rupture), pas un canevas à coordonnées : tracer un
trait entre deux cases exigerait de mesurer les positions en JavaScript et de
les recalculer à chaque redimensionnement. Un trait qui se désaligne
silencieusement sur une carte de topologie est pire que pas de trait.

Livré à la place, sur le nœud lui-même : un bouton « dépend de N » qui liste
les dépendances au survol et, au clic, **éclaire les nœuds dont ce composant
dépend**. L'information transmise est la même — « ceci dépend de cela » — sans
dépendre d'une mesure fragile. Jusqu'ici les dépendances n'existaient que sur
la fiche d'un composant, où il fallait déjà savoir lequel ouvrir.

Si le tracé géométrique est jugé nécessaire à la recette, il reste à faire et
suppose de passer la carte en SVG.

---

## Phase IV — Rapports, indicateurs, notifications

**Risque de collision : faible.** `SlaService.cs`, nouveau
`NotificationService`, `ExecutionReportService.cs`.

1. **FR-094** : ajouter au `SlaService` — étapes les plus lentes (déjà dans
   `ExecutionStep.Duration`), incidents par cause (`AlertKind`), erreurs
   récurrentes, temps de diagnostic moyen.
2. **FR-095** : `NotificationService` réel utilisant `SmtpEmailSender` déjà
   existant, déclenché par `OrchestrationEngine`/`AlertService` au lancement,
   en cas de blocage et à la fin — destinataires : demandeur + approbateurs +
   liste configurable par environnement.
3. **FR-096** : `IncidentReportService` qui assemble automatiquement, à la
   clôture d'une alerte/session de diagnostic, le rapport structuré complet
   (détection, prise en charge, fin, cause, preuves, actions, SOP associé).
4. **FR-028** : capturer la commande réellement envoyée (sous forme masquée)
   dans `ExecutionStep` et le rapport.

---

## Phase V — Orchestration : achever le Lot 1 (⚠ coordonner avec l'autre session)

**Risque de collision : élevé.** Touche directement `WorkflowService.cs`,
`WorkflowExecution.cs`, `OrchestrationEngine.cs`, `SequenceValidator.cs`,
`ExecutionService.cs` — le territoire exact où l'autre session travaille.
**Ne pas commencer avant qu'elle ait committé.**

1. **AC-05** : étendre la règle « Cluster un par un » de `SequenceValidator`
   à `StepAction.Arreter`, pas seulement `Demarrer`/`Redemarrer`.
2. **FR-029** : panneau d'aide à la décision sur une étape bloquée
   (hypothèses classées, temps restant avant timeout, action « collecter plus
   de preuves » qui ouvre un diagnostic pré-rempli).
3. **FR-029A/B** : logique symétrique « composant déjà arrêté, ignorer
   proprement » côté arrêt ; flux dédié « arrêt forcé » avec délai +
   confirmation + contrôle d'autorisation pour un service bloqué en Stopping.
4. **Séquence d'arrêt/démarrage** : générer les étapes de confirmation client
   (1-2) et de contrôle final (10) manquantes dans `WorkflowService.CreerAsync`.
5. **FR-031** : garde-fou sur les jobs conditionnels pendant un démarrage de
   nœuds.
6. **FR-035/FR-037** : validation fonctionnelle finale + recette technique
   consolidée comme porte de succès d'un démarrage complet.
7. **FR-043** : scénarios ciblés prédéfinis (Bridge+XPS, ECN4+ECN4Web,
   Center/Standby) en plus de la sélection libre.
8. **FR-046** : orchestrer réellement arrêt Standby → attente retour actif
   primaire → remise en service, au lieu d'un simple avertissement affiché.
9. **FR-011/013/014/015/016** : fenêtre d'intervention, ticket obligatoire en
   Production, matrice d'approbation par risque/composant, durée estimée
   agrégée à l'écran de confirmation, détection de conflit sur ressource
   partagée entre environnements, contrôle d'état initial étendu aux
   opérations partielles.
10. **FR-004/005** : dérogation explicite et tracée à l'interdiction de retry
    sur action destructrice ; simulation obligatoire avant validation d'un
    workflow.

---

## Phase VI — Modèle de données et non-fonctionnel

**Risque de collision : faible**, sauf item 3.

1. **NFR-006** : intégrer un outil de couverture (`coverlet` + rapport) pour
   remplacer « non mesuré » par un chiffre réel.
2. **NFR-007** : documenter/gérer explicitement l'absence de repli hors
   Windows pour la protection DPAPI du coffre à secrets, ou restreindre
   explicitement le déploiement à Windows.
3. **§3.19** : comportement dégradé explicite au-delà des 3 tentatives de
   reconnexion base (page de statut applicatif, health check exposé) —
   *dépend de `DependencyInjection.cs`, coordonner avec l'autre session.*
4. **Approbation** : évaluer si une entité `Approbation` dédiée apporte une
   valeur réelle par rapport aux champs actuels sur `WorkflowExecution`
   (actuellement fonctionnel — priorité basse).

---

## Hors de portée du code seul — nécessite une ressource externe

Ces exigences ne peuvent pas atteindre 100 % par du développement seul ; elles
dépendent d'une ressource que je n'ai pas :

| Exigence | Ce qu'il faut | Ce que je peux faire sans |
|---|---|---|
| Intégration LDAP/Active Directory | Un tenant Azure AD réel + identifiants | Scaffolding désactivé par défaut (déjà en cours côté autre session — `AzureAdAuthProvider.cs`) |
| Intégration ticketing | Accès à l'API du système CIT réel | Connecteur générique désactivé par défaut, champ texte libre en attendant |
| Passerelle réseau sécurisée | Décision et matériel réseau, hors périmètre applicatif | Documentation de déploiement recommandant un reverse proxy (déjà fait) |
| NFR-003 Performance sous charge | Un environnement de test de charge réel | Rien de mesurable sans lui — le code peut être instrumenté, pas testé en charge depuis ce poste |
| NFR-004 Scalabilité réelle | Plusieurs dizaines d'environnements réels à superviser simultanément | Idem — conception déjà multi-environnement, preuve sous charge impossible ici |

---

## Séquencement recommandé

1. Attendre que l'autre session committe son état actuel (Phase G/H).
2. Phase I (faible risque, fort impact) — peut démarrer immédiatement en
   parallèle si l'autre session ne touche pas les fichiers listés.
3. Phase II (moteur de diagnostic) — peut démarrer en parallèle, zéro
   recouvrement.
4. Phase III et IV — démarrer une fois Phase I terminée sur les fichiers
   partagés (`AlertService.cs`).
5. Phase V — **après** committement de l'autre session uniquement, pour éviter
   d'écraser son travail sur l'orchestration.
6. Phase VI — en continu, faible priorité.
7. Items hors-code — à discuter avec la DSI, pas une tâche de développement.
