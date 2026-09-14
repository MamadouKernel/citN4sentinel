# Analyse MERISE — N4 Sentinel
## 01. Règles de Gestion (RG)

**Document de référence** : [Cahier_de_charge_N4_Sentinel_v3 (1).pdf](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/doc/Cahier_de_charge_N4_Sentinel_v3%20(1).pdf)
**Guides techniques** : [N4 3.8.25 Diagnostics](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/doc/N4%203.8.25%20Setup,%20Maintenance,%20and%20System%20Diagnostics%20Guide%201%20(1).pdf) / [N4 4.x Day1 Install-Startup](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/doc/N4%20IT%20Admin%202024%204.x%20Day1%20Install-Startup%20(2)%20(1).pdf)
**Code de base actuel** : [N4Component.cs](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/N4Component.cs), [WorkflowExecution.cs](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/WorkflowExecution.cs), [Diagnostic.cs](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/Diagnostic.cs), [Sop.cs](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/Sop.cs)

---

## RG-01 — Référentiel & Environnements

| RG | Énoncé | Origine |
|---|---|---|
| RG-0101 | **Un composant N4 ne peut exister que rattaché à un seul ENVIRONNEMENT.** | CdC FR-002 |
| RG-0102 | **Il existe 5 types d'environnements : Production, UAT, Test, Développement, Formation.** Le type est immuable après création. | CdC FR-001 / [Enums.cs](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/Enums.cs#L18-L25) |
| RG-0103 | **Tout composant d'un ENVIRONNEMENT PRODUCTION est obligatoirement associé à une criticité minimum MOYENNE.** Centre et Standby = CRITIQUE. | CdC FR-029 |
| RG-0104 | **Un composant enregistré en statut BROUILLON ne peut être piloté ni supervisé.** Pour être pilotable : statut = VALIDE ou ACTIF ET ControlMode = PILOTABLE. | [N4Component.cs](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/N4Component.cs#L131-L133) |
| RG-0105 | **Un ENVIRONNEMENT ne peut comporter QU'UN SEUL composant de rôle CENTERNODE.** Deux composants Center en même temps = violation majeure (cause P1 #10). | Formation 4.x / Guide 3.8.25 2.1.4.7 |
| RG-0106 | **Un composant ne peut dépendre d'un autre composant d'un ENVIRONNEMENT DIFFÉRENT.** Le graphe de dépendances est intra-environnement. | CdC FR-002 |

---

## RG-02 — Supervision & État réel

| RG | Énoncé | Origine |
|---|---|---|
| RG-0201 | **L'état d'un composant n'est JAMAIS établi par le seul statut du service Windows "Running".** Il faut au MOINS : (a) service = Running, (b) journal écrit, (c) motif ReadyPattern détecté, (d) heartbeat frais (< 2min). | [ReadinessProfile](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/N4Component.cs#L147-L248) |
| RG-0202 | **Quand les signaux manquent ou se contredisent, l'état est INCONNU / À CONFIRMER.** Jamais OK par défaut. | CdC Principe 4 / [ComponentState](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/Enums.cs#L107-L117) |
| RG-0203 | **Pour un Center ou Standby, on distingue le statut de service du RÔLE ACTIF RÉEL.** On utilise les ActiveRolePatterns. Les 2 en ACTIF = CRITIQUE (split-brain). | Formation 4.x p.51 / 3.8.25 2.1.4.7 |
| RG-0204 | **Les horloges de TOUS les serveurs d'un ENVIRONNEMENT doivent être synchronisées à < 1 seconde.** Écart > 1s = ALERTE CRITIQUE avant toute opération. | P1 #5 / Guide 3.8.25 p.842 |
| RG-0205 | **Un Bridge en statut LOADING bloque le démarrage de XPS.** XPS ne doit être démarré qu'après confirmation Bridge = ACTIVE + QueueSize bridge.* = 0. | Formation 4.x Module 1.6 p.36 |
| RG-0206 | **Un composant en mode MAINTENANCE n'émet pas d'alerte de supervision.** La durée de maintenance est bornée (MaintenanceUntil), le motif obligatoire (MaintenanceReason). | [N4Component.cs](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/N4Component.cs#L86-L100) |

---

## RG-03 — Pré-checks & Approbations

| RG | Énoncé | Origine |
|---|---|---|
| RG-0301 | **Toute opération mutative nécessite un PRE-CHECK TERMINÉ sans contrôle BLOQUANT.** Les contrôles sont : connectivité serveurs, services, processus, DB, réseau, dossiers partagés, disque, horloges, files critiques, opérations concurrentes. | CdC FR-012 |
| RG-0302 | **Résultat pré-check : Satisfait / Avertissement / BLOQUANT / Non applicable / Impossible à vérifier.** Tout BLOQUANT → opération bloquée (sauf contournement approbation double en Prod). | [CheckOutcome](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/Enums.cs#L120-L127) |
| RG-0303 | **En PRODUCTION : motif, fenêtre intervention, périmètre, impact, référence incident OBLIGATOIRES avant lancement.** | CdC FR-011 |
| RG-0304 | **Approbation SIMPLE (criticité FAIBLE/MOYENNE) ou DOUBLE (ÉLEVÉE/CRITIQUE en Prod).** 2e approbateur = utilisateur DISTINCT du demandeur et du 1er approbateur. | CdC FR-013 / MatrixApprovalRule |
| RG-0305 | **Vérrou MUTEX global par ENVIRONNEMENT : 1 seule opération mutative active à la fois.** Le verrou porte une date d'expiration (ExpiresAt) prolongée par heartbeat moteur. | [EnvironmentLock](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/WorkflowExecution.cs#L384-L402) / CdC FR-015 |
| RG-0306 | **Si opération touche Center/Standby, choix CONTINUITY explicite OBLIGATOIRE : ResterActif ou Basculer.** | [CenterContinuityChoice](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/Enums.cs#L161-L168) |
| RG-0307 | **Les 10 causes TOP P1 sont vérifiées DANS L'ORDRE en début d'incident P1 :** (1) Gestion données insuffisante, (2) Réseau, (3) Ordre incorrect, (4) Corruption AMQ, (5) Horloges, (6) Snapshots, (7) Tiers incompatibles, (8) Index/fragmentation, (9) Erreur install, (10) Split-brain Center. | Formation 4.x TOP10 P1 causes |

---

## RG-04 — Orchestration Workflows

| RG | Énoncé | Origine |
|---|---|---|
| RG-ARRÊT-01 | **Ordre officiel d'arrêt : ECN4Web → ECN4Daemon → XPS → BridgeDaemon → ClusterNodes (un par un) → Standby → Center.** Jamais parallèle sauf nœuds Cluster indépendants. | Formation 4.x p.58 / SOP-N4-001 |
| RG-DÉMARRAGE-01 | **Ordre officiel de démarrage : 1er Cluster → autres Clusters → Center → Standby → Bridge (WAITING→LOADING→ACTIVE) → XPS → ECN4Daemon → ECN4Web.** Attendre "Web tier servlet 'action' initialized" avant étape suivante. | Formation 4.x p.36-39 |
| RG-0401 | **Étapes d'exécution : À VENIR / EN ATTENTE / EN COURS / VÉRIFICATION / RÉUSSI / AVERTISSEMENT / BLOQUÉ / ÉCHEC / IGNORÉ / ANNULÉ.** | [ExecutionStepState](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/WorkflowExecution.cs#L56-L71) |
| RG-0402 | **Étape VÉRIFICATION obligatoire après toute action mutative.** La preuve technique (Evidence) = ligne journal + statut service. | CdC Principe 3 |
| RG-0403 | **Parallélisation SEULEMENT si le validateur de séquence a jugé les étapes INDÉPENDANTES.** Centre, Bridge, XPS, ECN4 = JAMAIS parallèles entre eux. | CdC FR-023 / CanRunInParallel |
| RG-0404 | **REPRISE d'une exécution interrompue = RÉ-CONTROLE DE L'ÉTAT RÉEL avant toute action.** Jamais répétition aveugle d'une étape. | NFR-002 |
| RG-0405 | **ANNULATION sûre : atteindre point "sûr" (pas de coupure brutale).** Post-annulation = relevé état réel, + flag RequiresManualIntervention si état instable. | CdC FR-025 |
| RG-0406 | **CONTOURNEMENT d'une étape bloquante = rôle habilité + motif + risque + approbation (double en Prod).** Traçage SkippedBy, SkipReason, SkipCoApprovedBy/At. | CdC FR-027 |
| RG-0407 | **ARRÊT FORCÉ d'une étape StopPending = JAMAIS AUTOMATIQUE.** Décidé par opérateur, preuve explicitement absente. | CdC FR-029B / [StepErrorType 2](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/WorkflowExecution.cs#L9-L32) |
| RG-0408 | **Retries : JAMAIS automatiques pour actions CRITIQUES ou DESTRUCTRICES.** Retry manuel seulement, délai croissant. | CdC FR-004 |
| RG-0409 | **Mode SIMULATION : toutes étapes déroulées, AUCUNE commande émise, preuves simulées.** | CdC FR-005 / IsSimulation |

---

## RG-05 — Diagnostic d'incidents

| RG | Énoncé | Origine |
|---|---|---|
| RG-0501 | **Cycle 8 phases NON LINÉAIRE : Détection → Qualif/Collecte → Sécurisation → Diagnostic/Corrélation → Plan → Validation/Exécution → Remise service → Clôture/Capitalisation.** Retour en arrière autorisé, toutes transitions tracées. | CdC §3.10.1 / [DiagnosticPhase](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/Diagnostic.cs#L256-L266) |
| RG-0502 | **Verdict = 7 niveaux précis : CauseConfirmée / CauseCaractérisée / PlusieursCausesPossibles / PisteSérieuse / AnomaliesSansCause / RienDeConcluant / InformationsInsuffisantes.** Jamais binaire OK/NOK. | [DiagnosticVerdict](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/Diagnostic.cs#L11-L62) |
| RG-0503 | **Toute hypothèse citée = PREUVES explicites + CONTRE-PREUVES potentielles + niveau de confiance (0-100).** Hypothèse > seuil = "établie". | [DiagnosticHypothesis](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/Diagnostic.cs#L604-L644) |
| RG-0504 | **Diagnostic inconcluant = ESCALADE OBLIGATOIRE avant clôture.** (à Infra, Support Navis, etc.) | CdC §3.10.1 Phases 7-8 |
| RG-0505 | **Comparaison à une référence : 4 modes (période saine, exécution réussie, valeurs habituelles composant, nœud pair).** Une référence = jamais automatique = affirmation humaine. | [ReferenceKind](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/Diagnostic.cs#L274-L287) |
| RG-0506 | **GROSSE QUEUE BRIDGE (> seuil) : redémarrage Bridge = INTERDIT.** Bouton grisé + message : "Préférez redémarrer Cluster Nodes ou attendre la consommation réseau rétabli." | Guide 3.8.25 2.1.4.8 / RG-AMÉLIORATIONS Axe3 |
| RG-SPLIT-BRAIN | **2 Center/Standby ACTIFS : MODE DÉDIÉ IMPOSÉ.** Étapes forcées : (1) Vérif 2 db.(n) dans amq\, (2) Identifier vrai Center, (3) REDÉMARRER D'ABORD LE STANDBY, (4) Vérif Standby repasse Standby, (5) Drain queues, (6) Analyse cause auto. | RG-AMÉLIORATIONS Axe3 |

---

## RG-06 — Logs, Signatures & Secret

| RG | Énoncé | Origine |
|---|---|---|
| RG-0601 | **Tout contenu de journal = SECRETS MASQUÉS avant enregistrement.** Compte du nombre de masquages affiché à l'opérateur. | CdC SEC-003 / FR-077 |
| RG-0602 | **Empreinte SHA-256 du contenu MASQUÉ enregistrée (intégrité).** | [LogSource.ContentHash](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/Diagnostic.cs#L495) |
| RG-0603 | **Signatures cataloguées = cycle de vie Brouillon→Validé→Actif.** Poids confiance 1-100. Signatures éditeur Site = modifiables. | [DiagnosticSignature](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/Diagnostic.cs#L133-L200) |
| RG-0604 | **Toute erreur non cataloguée mais répétée = Constat créé automatiquement.** | LogFinding sans SignatureId |
| RG-0605 | **Échec de collecte = Taxonomie précise (pas texte libre seul) : AccèsRefusé / Connecteur / Timeout / SourceAbsente / FormatNonReconnu / NonConfiguré.** | [LogCollectionFailureReason](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/Diagnostic.cs#L407-L415) |

---

## RG-07 — SOP & Capitalisation

| RG | Énoncé | Origine |
|---|---|---|
| RG-0701 | **SOP ≠ WORKFLOW : SOP n'automatise JAMAIS rien.** Chaque étape = confirmée par humain + preuve saisie. | [Sop.cs](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/Sop.cs#L61-L69) |
| RG-0702 | **SOP modifié après exécution = NOUVELLE VERSION.** Version exécutée = immuable. | Sop.HasBeenExecuted + Version |
| RG-0703 | **Génération SOP depuis (a) workflow réussi, (b) diagnostic clos, (c) réponse doc = statut BROUILLON.** Validation humaine OBLIGATOIRE avant Actif. | CdC FR-088/089A/B |
| RG-0704 | **SOP avec RequiresElevatedRole = droit PeutExecuterActionsSensibles nécessaire.** | [Sop.RequiresElevatedRole](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/Sop.cs#L91-L98) |
| RG-0705 | **Résultat SOP (ProblèmeRésolu / NonRésolu / Indéterminé) = saisi par HUMAIN.** Ce résultat alimente le Taux de Réussite Historique des SOP similaires. | [SopOutcome](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/Sop.cs#L21-L28) |
| RG-0706 | **Suggestion SOP = taux de réussite historique affiché.** Ne suggère jamais un SOP < 50% de réussite. | Amélioration Axe4 |

---

## RG-08 — Sécurité, Audit, Rétention

| RG | Énoncé | Origine |
|---|---|---|
| RG-0801 | **Profils RBAC strict : Lecteur / Opérateur / AdminN4 / AdminInfra / Validateur / AdminSolution / Auditeur / TOSManager.** Séparation devoirs : qui approuve ≠ qui exécute ≠ qui audite. | CdC §2.5 / SEC-002 |
| RG-0802 | **Secrets = COFFRE (DPAPI ou équivalent). JAMAIS affichés, JAMAIS en clair dans logs ou appsettings Prod.** | SEC-003 |
| RG-0803 | **Authentification : V1 MFA Email/TOTP, V2 Azure AD/SSO.** Compte nominatif OBLIGATOIRE (pas partagé). | SEC-001 / Comptes nominatifs |
| RG-0804 | **PRODUCTION vs UAT = thème UI DIFFÉRENT (Prod = rouge).** Actions Prod = confirmation MFA supplémentaire. | SEC-004 / Amélioration Axe1 Biais |
| RG-0805 | **Journal d'audit = NON MODIFIABLE (append-only).** Toute action : Succès / Refus / Échec. Inclut tentatives non autorisées. | SEC-008 / AuditWriter + AuditingInterceptor |
| RG-0806 | **Rétentions distinctes :** Logs techniques (30j), Rapports (1an), Audits (7ans), SOP/Workflows validés (permanent). | SEC-009 / RetentionPolicy |
| RG-0807 | **Panne N4 Sentinel = procédures PowerShell manuelles restent utilisables.** Les scripts du dossier Corpus sont indépendants. | SEC-010 / README.md ligne 84 |

---

## RG-09 — Prévention (Signaux Faibles / APR) — AMÉLIORATIONS

| RG | Énoncé | Origine |
|---|---|---|
| RG-0901 | **Tendance LOCALLYSCHEDULEDJOBCOUNT sur 15min en hausse = Alerte Prédictive.** Recommandation : Redémarrer Cluster Node avant saturation. | Amélioration Axe2 |
| RG-0902 | **Fragmentation index > 28% (tendance vers 30%) = Recommandation préventive fenêtre creuse.** Durée estimée affichée. | Guide 3.8.25 2.2 / Amélioration Axe2 |
| RG-0903 | **Écart horloge > 0.8s (tendance) = alerte avant seuil 1s.** Diagnostic du serveur NTP. | Amélioration Axe2 |
| RG-0904 | **Panneau APR permanent : top 10 Actions Préventives Recommandées, classées par (risque P1 évité × durée opération).** | Amélioration Axe2 |
| RG-0905 | **Détection niveau DEBUG activé sur Log4j = alerte immédiate.** Explosion probable de logs → full disk. | Guide 3.8.25 2.1.9.4 |
| RG-0906 | **Disk ProgramData > 85% ET croissance = Détection auto cause (logs DEBUG ?) + correction.** | Amélioration Axe2 |

---

## RG-10 — Métriques Succès Business — AMÉLIORATIONS

| RG | Énoncé | Origine |
|---|---|---|
| RG-1001 | **MTTR : suivi par type d'incident. Objectif V1 = divisé par 2.** | Amélioration Axe4 |
| RG-1002 | **Nombre de P1/mois : suivi tendentiel. Objectif = -50% via APR.** | Amélioration Axe4 |
| RG-1003 | **% Incidents P1 résolus par N1/N2 (sans expert N4).** Objectif V1 = 30%, V2 = 70%. | Amélioration Axe4 |
| RG-1004 | **Taux de réussite Workflow par version : < 95% = alerte qualité workflow.** | Amélioration Axe4 |
