# Plan de remédiation — atteindre 100 % du cahier des charges

**Origine** : audit indépendant du 15/08/2026 (voir la synthèse publiée), qui
a évalué 148 exigences numérotées à 75 Fait / 59 Partiel / 14 Absent.
Ce document convertit chaque écart en action concrète, organisée en phases,
pour fermer les 73 exigences Partiel/Absent une par une.

**État au 18/08/2026** : **Phase I est terminée — les 14 items sont faits.**
Les 4 qui restaient bloqués (3, 4, 6, 7) le sont désormais : l'autre session a
committé (`c10b66c`), ce qui a levé les deux verrous — le modèle EF n'a plus de
changement non migré, et `Login.razor` est stabilisé.

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
| 10 | ⛔ migration bloquée | FR-087 | Bouton « Signaler une réponse incorrecte » — nécessite une entité de correction proposée | `Knowledge.cs`, `KnowledgeService.cs`, `Documentation.razor` |
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

1. **FR-065 / §3.10.4** : étendre `DiagnosticSignature` d'un type de règle
   (`Motif` existant, nouveau `Correlation`) qui référence plusieurs sources/
   signaux avec un opérateur (ET/OU/seuil), pas seulement une regex. Ajouter
   version + statut de validation (Brouillon/Testé/Production) avec passage
   obligatoire sur un jeu de données de référence avant activation.
2. **FR-061** : relier `ClockSkewSeconds` (déjà calculé côté Vitalité) à
   `DiagnosticSession` — construire une chronologie unifiée qui réordonne les
   événements de plusieurs sources selon l'écart d'horloge détecté.
3. **FR-063** : ajouter `ContradictingEvidence` et un identifiant de règle
   versionné sur `DiagnosticHypothesis`.
4. **FR-066** : nouveau concept de « période de référence » — marquer une
   session/exécution comme saine, comparer les mêmes signaux à une session
   courante, écran de différence.
5. **FR-067** : empaqueter le rapport d'escalade en archive avec empreinte
   (hash) des fichiers inclus.
6. **FR-068** : déclencher `CollectFromServerAsync` automatiquement quand une
   alerte critique est levée (lier `AlertService` → `DiagnosticSessionService`).
7. **FR-069** : ajouter le 5e verdict (« plusieurs causes possibles »),
   déclenché quand deux hypothèses ont une confiance proche.
8. **FR-071** : heuristique de détection composant/serveur/version à partir du
   contenu importé (motifs déjà connus du catalogue).
9. **FR-072/073/077** : champs structurés (niveau, thread, classe,
   transaction), résumé de session (volumes par niveau, évolution), filtres
   composant/serveur/code/texte libre.
10. **FR-070/079/079B** : support d'archive .zip à l'import, politique de
    rétention configurable, filtre temporel appliqué à la collecte plutôt
    qu'après coup.
11. **Cycle incident (8 phases)** : ajouter `AlertId` sur `DiagnosticSession`
    pour relier détection → diagnostic, et des champs de suivi pour
    sécurisation/remise en service.

---

## Phase III — Supervision, dossiers partagés, EDI

**Risque de collision : faible à moyen.** `SupervisionService.cs` est proche
mais pas identique aux fichiers touchés par l'autre session — vérifier l'état
au moment de démarrer.

1. **FR-050** : dessiner les arêtes `ComponentDependency` sur
   `N4TopologyDiagram.razor` ; soit relier les nœuds actuellement codés en dur
   au référentiel réel, soit les retirer et n'afficher que les composants
   déclarés ; faire apparaître un composant non déclaré directement sur la
   carte (pas seulement dans un panneau à ouvrir manuellement).
2. **FR-051** : construire le moteur qui évalue réellement
   `ComponentHealthCheck` (port TCP, heartbeat, connectivité base) — la table
   existe en base sans consommateur.
3. **FR-057** : ajouter de vraies données réseau (latence, anomalies de
   connectivité) au panneau « Réseau et base », ou le renommer pour refléter
   ce qu'il montre réellement.
4. **FR-059B** : mesurer la latence des opérations dossier, détecter une
   tendance de croissance anormale (pas seulement un seuil d'ancienneté
   absolu).
5. **FR-059D** : ajouter un lien `SharedFolderSnapshot → SopExecution` pour
   tracer la confirmation ou l'infirmation d'une suspicion de corruption.
6. **FR-024** : `ExecutionService.ResumeAsync` doit interroger
   `SupervisionService` pour l'état réel du composant avant de rejouer une
   étape après réconciliation, plutôt que de la remettre à zéro aveuglément.

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
