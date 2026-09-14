# Analyse MERISE — N4 Sentinel
## 05. Modèle Conceptuel de Traitements (MCT)

**But** : Modéliser les grands processus métier, leurs décompositions en sous-traitements, les flux de données entrants/sortants, et les intégrations.
**Méthode** : Structuration par niveau (MCT Niveau 0 = contexte global → Niveau 1 = grands processus → Niveau 2 = détaillé).

---

## MCT NIVEAU 0 — Diagramme de Contexte Global

```
                         ┌──────────────────────────────────────────────────┐
                         │                  N4 SENTINEL                     │
                         │                                                  │
   ┌──────────────┐      │  ┌──────────────────────────────────────────┐    │
   │   Opérateur  │──────┼─►│  IHM Web (8 profils RBAC)                │    │
   │  N1/N2/N3    │◄─────┼──│  - Dashboard Global                      │    │
   │  / Auditeur  │      │  │  - Orchestration / Supervision           │    │
   └──────────────┘      │  │  - Diagnostic / Logs / Assistant        │    │
                         │  │  - SOP / Historique / Administration    │    │
   ┌──────────────┐      │  └────────────────────┬─────────────────────┘    │
   │  Validateur  │──────┼───────────────────────┤                          │
   │  / Manager   │◄─────┼───────────────────────┤                          │
   └──────────────┘      │                       ▼                          │
                         │  ┌──────────────────────────────────────────┐    │
   ┌──────────────┐      │  │  Moteur Applicatif (API + Orchestrateur)│    │
   │  Support     │──────┼─►│  - Workflow Engine Semi-Auto (Palier 1) │    │
   │  Navis       │◄─────┼─►│  - Diagnostic + Corrélation             │    │
   └──────────────┘      │  │  - RAG / Documentaire Q&R               │    │
                         │  │  - Audit + RBAC + Retention             │    │
   ┌──────────────┐      │  └──────┬─────────────┬─────────────┬───────┘    │
   │  Infra CIV   │──────┼─────────┤             │             │            │
   │  Team        │◄─────┼─────────┤             │             │            │
   └──────────────┘      │         ▼             ▼             ▼            │
                         │  ┌───────────┐ ┌──────────┐ ┌────────────────┐   │
   ┌──────────────┐      │  │Connecteurs│ │Corpus   │ │ Base de Données│   │
   │  ITSM CIT    │──────┼─►│ PowerShell│ │PDF/JSON │ │  Principale    │   │
   │  (Ticket CIT)│◄─────┼──│ + SQL     │ │(Navis   │ │ (données +    │   │
   └──────────────┘      │  │ + HTTP    │ │  Guides,│ │  audit + SOP) │   │
                         │  │ + Files   │ │  SOPs)  │ │                │   │
   ┌──────────────┐      │  │ + WMI/SVC │ │indexés  │ │                │   │
   │  Key Vault / │──────┼─►└───────────┘ └──────────┘ └────────────────┘   │
   │  DPAPI       │◄─────┼─┐     ▲             ▲              ▲             │
   └──────────────┘      │  │     │             │              │             │
                         │  │     │             │              │             │
   ┌──────────────┐      │  └─────┼─────────────┼──────────────┘             │
   │   MFA Email  │──────┼────────┤             │                            │
   │  / Azure AD  │◄─────┼────────┤             │   ┌────────────────────────┐│
   │   (V1/V2)    │      │        ▼             ▼   │  Superviseurs         ││
   └──────────────┘      │   ┌────────────────────┐ │  - Alerte temps réel  ││
                         │   │ ÉCOSYSTÈME NAVIS   │ │  - Signaux Faibles    ││
   ┌──────────────┐      │   │   N4 (hors sentinel)│ │  - APR recommandées  ││
   │   LDAP / AD  │──────┼─► │ - Cluster Nodes    │ └────────────────────────┘│
   │    Domain    │◄─────┼── │ - Center/Standby   │                          │
   └──────────────┘      │   │ - Bridge Daemon    │                          │
                         │   │ - XPS/XPS Services │                          │
                         │   │ - ECN4/ECN4 Web    │                          │
                         │   │ - DB SQL/Oracle    │                          │
                         │   │ - Shared Folder    │                          │
                         │   │ - ActiveMQ / EDIs  │                          │
                         │   └────────────────────┘                          │
                         └──────────────────────────────────────────────────┘
```

### Flux Externes (Niveau 0)

| Flux | Sens | Données | RG |
|---|---|---|---|
| `F1 - Actions opérateur` | UTILISATEURS → N4Sentinel | Séléction Workflow, confirmations, diagnostic, consultations | Tous RG-01 à RG-10 |
| `F2 - Retours écrans` | N4Sentinel → UTILISATEURS | Dashboard, étapes, preuves, alertes, hypothèses, logs masqués | SEC-003 masquage |
| `F3 - Approbations simples/doubles` | VALIDATEUR → N4Sentinel | Accords sur opérations critiques | RG-0304 |
| `F4 - État, commandes & logs N4` | N4Sentinel → ECOSYSTÈME NAVIS | Commandes PS, appels HTTP, requêtes SQL read-only, listage fichiers | CdC §3.16 SEC-006 (pas de console libre) |
| `F5 - Retours écosystème Navis` | ECOSYSTÈME NAVIS → N4Sentinel | Statuts services, stdout/stderr, logs bruts, réponses HTTP, résultats SQL, arborescence dossiers | RG-0201 preuve, RG-0601 secret mask |
| `F6 - Référence Ticket CIT (aller)` | N4Sentinel → ITSM CIT | Données opération : numéro, impact, motif, ticket lié | RG-0303 Prod |
| `F7 - Référence Ticket CIT (retour)` | ITSM CIT → N4Sentinel | Statut ticket, lien, notes | Fiche incident capitalisée |
| `F8 - Authentification` | N4Sentinel ↔ AD/AzureAD/MFA | Challenge, MFA, attributs groupes | RG-0801/03 V1 Email, V2 AAD |
| `F9 - Coffre Secrets` | N4Sentinel ↔ Vault/DPAPI | Stockage/récupération credentials CHIFFRÉS | SEC-003 |

---

## MCT NIVEAU 1 — Les 8 grands Processus Métier

```
┌──────────────────────────────────────────────────────────────────────────────┐
│  N4 SENTINEL : 8 PROCESSUS MÉTIER (Niveau 1)                                 │
│                                                                              │
│  P1  RÉFÉRENTIEL ET PARAMÉTRAGE (AdminSolution)                              │
│  P2  SUPERVISION EN TEMPS RÉEL + PRÉDICTION APR (Tous rôles)                 │
│  P3  ORCHESTRATION ARRÊT / DÉMARRAGE / OPÉRATIONS (Semi-Auto Palier 1)       │
│  P4  DIAGNOSTIC INCIDENT P1/P2 + ANALYSE DE LOGS (N2/N3/Admin N4)           │
│  P5  BASE DOCUMENTAIRE + ASSISTANT N4 IA (RAG) + Q&R SOURCÉE (Tous)         │
│  P6  SOP — CAPITALISATION & EXÉCUTION HUMAINE GUIDÉE (N1→N2)                │
│  P7  HISTORIQUE, AUDIT, RAPPORTS, MÉTRIQUES MTTR (Auditeurs, Manag.)        │
│  P8  SÉCURITÉ : RBAC, MFA, SECRETS, RÉTENTIONS (AdminSolution)              │
└──────────────────────────────────────────────────────────────────────────────┘
```

---

## MCT NIVEAU 2 — Détaillé par Processus

---

### P1 : RÉFÉRENTIEL ET PARAMÉTRAGE GÉNÉRAL

**Objectif** : AdminSolution alimente le référentiel des environnements, serveurs, composants, workflows, seuils, signatures connues, règles de corrélation.

```
┌────────────────────────────────────────────────────────────────────┐
│  P1 — Référentiel & Paramétrage                                     │
│                                                                    │
│  P1.1  Gérer les ENVIRONNEMENTS                                    │
│        ├─ CRUD Env + type + shared folder + timezone              │
│        ├─ Cycle de vie Brouillon→Actif                            │
│        └─ CONTRAINTE : Prod = criticité minimum MOYENNE (RG-0103) │
│                                                                    │
│  P1.2  Inventorier SERVEURS et COMPOSANTS N4                      │
│        ├─ CRUD serveurs (OS, IP, RAM, CPU)                        │
│        ├─ CRUD composants avec PROFIL DE PREUVE 1:1              │
│        ├─ CRUD PROFIL DOSSIER PARTAGE (1:1 DossierPartage)       │
│        ├─ CRUD SERVICES ASSOCIÉS (XPSGate, ECN4 XMLRDT...)       │
│        ├─ CRUD MATRICE DÉPENDANCES (CMP→CMP)                     │
│        └─ CONTROLES RG-0105 : 1 seul Center/Standby par ENV      │
│                                                                    │
│  P1.3  Construire les WORKFLOWS Modèles                            │
│        ├─ CRUD WKF : Arret/Start/Restart/Partial/Reconstitution  │
│        ├─ CRUD ÉTAPES WKF (ordre, action, timeout, retry...)     │
│        ├─ CRUD PRÉ-CHECKS (horloges, disque, antivirus, locks)   │
│        ├─ Versioning auto si déjà exécuté (RG-0702)              │
│        └─ Validation → Actif (cycle FR-006)                       │
│                                                                    │
│  P1.4  Gérer SIGNATURES ANOMALIES et RÈGLES CORRÉLATION           │
│        ├─ CRUD SIGDIAG (Editeur vs Site, valid. cycle)           │
│        ├─ CRUD REGLES CORR (SPLIT-BRAIN, queues...)              │
│        └─ CRUD CONDITIONS par règle (signaux + fenêtre)          │
│                                                                    │
│  P1.5  Paramétrer POLITIQUES APPROBATION et MATRICE CRITICITÉ      │
│        ├─ CRUD règles ApprovalMatrix (RG-0304 double Prod)       │
│        └─ Politiques Rétention par catégorie (SEC-009)           │
│                                                                    │
│  P1.6  CRÉDENTIELS TECHNIQUES dans COFFRE                         │
│        └─ Ajout/modif/suppr (JAMAIS en clair / SGBD)             │
│                                                                    │
│  ┌─DONNÉES LIÉES─────────────────────────────────────────────────┐│
│  │ ENV, SRV, CMP, RDY, DOSP, DEP, SVCASSOC, WKF, ETP, VRF,       ││
│  │ SIGDIAG, REGCORR, CONDCORR, CREDPROF, APROBMAT, RET, DIAGSETT ││
│  └────────────────────────────────────────────────────────────────┘│
└────────────────────────────────────────────────────────────────────┘
```

**Flux associés** :
- `P1_→ F_CATALOGUE` : catalogue Workflows disponibles (vers P3, P6)
- `P1_→ F_SIGNATURES` : règles de diagnostic (vers P4)
- `P1_→ F_PARAM_SUP` : seuils supervision (vers P2)

---

### P2 : SUPERVISION EN TEMPS RÉEL + APR (Actions Préventives Recommandées)

**Objectif** : Produire une vision de l'état réel (≠ service running), générer alertes, détecter signaux faibles → APR.

```
┌────────────────────────────────────────────────────────────────────────┐
│  P2 — Supervision + Signaux Faibles + APR                               │
│                                                                        │
│  P2.1  COLLECTE DES SIGNAUX PAR CONNECTEUR (répétitif 10-30s)          │
│        ├─ Services Windows via CIM/WMI → État service, Processus       │
│        ├─ Fichiers journaux → Tail + ReadyPattern matching (RDY)      │
│        ├─ SQL Read-only → ClusterServices view, TopSQL, Index frag,   │
│        │      DB connections, sessions, locks                          │
│        ├─ Network / TCP → Port, heartbeat, JMS QueueSize,             │
│        │      ConsumerCount, ProcessedEvents                           │
│        ├─ FileSystem → SharedFolder latence, disk usage pct           │
│        ├─ Horloges (NTP/w32tm) → Écart inter-serveurs sec             │
│        ├─ AMQ KahaDB → fichiers db.(n), verrou → split-brain détection│
│        └─ EDI folders → âge fichier, status (pending/consumed/error)  │
│                                                                        │
│  P2.2  CONSOLIDATION ÉTAT RÉEL PAR COMPOSANT (RG-0201 multi-signaux)  │
│        ├─ SI Running + ReadyPattern détecté + Heartbeat<2min          │
│        │     + Ports ouverts → STATUT = OPÉRATIONNEL                  │
│        ├─ SINON Écarts classés : Inconnu, Dégradé, Bloqué, Erreur     │
│        ├─ RÔLE ACTIF Center / Standby via ActiveRolePattern          │
│        └─ État SYNC N4 ↔ XPS via SyncPattern + retard < 15min         │
│                                                                        │
│  P2.3  DÉTECTION ANOMALES + ALERTES                                    │
│        ├─ Seuils dépassés → ALT gravité (Mineure→Critique)           │
│        ├─ Alertes temps réel : cluster INACTIVE, queue haute,         │
│        │      horloge >1s, split-brain détecté (règle COR-SPLIT-001)  │
│        └─ **SIGNAUX FAIBLES PRÉDICTS :**                               │
│             ├─ locallyScheduledJobCount tendance +15min → ALT préd.   │
│             ├─ fragmentation 28% → tendance vers 30% → ALT préd.      │
│             ├─ écart horloge 0.8s → tendance → ALT préd.              │
│             ├─ Log4j DEBUG activé → explosion disque → ALT préd.      │
│             └─ disk ProgramData > 85% + croissance → ALT préd.        │
│                                                                        │
│  P2.4  GÉNÉRATION PANEL ACTIONS PRÉVENTIVES RECOMMANDÉES (APR)        │
│        ├─ Top 10 APR calculé par :                                     │
│        │     APR_Score = RisqueP1 évité % / Durée estimation          │
│        ├─ Chaque APR : fenêtre suggérée (ex: 22h00), workflow lien,  │
│        │  justification détaillée, mesures actuelles                  │
│        └─ APR : Nouvelle → Programmée (opérateur) → Réalisée          │
│           (par workflow + flag EXE id) → ou Ignorée (motif oblig.)    │
│                                                                        │
│  P2.5  TABLEAU DE BORD UI GLOBAL                                       │
│        ├─ Vue par ENV (Prod ROUGE animé / UAT BLEU)                   │
│        ├─ Carte par composant : code couleur état, queues, heartbeats │
│        ├─ Synoptique Global N4 ↔ XPS Synchro OK / retard              │
│        ├─ Alertes en cours / à acquitter / APR à programmer           │
│        ├─ Historique signaux (courbes queues/frag/disk)               │
│        └─ Métriques business temps réel (MTTR Mois-1 en cours)        │
│                                                                        │
│  ┌─DONNÉES LIÉES─────────────────────────────────────────────────────┐ │
│  │ SIGNAL COMPOSANT, ALERTE_SUPERVISION, ACTION_PRÉVENTIVE_RECOMMANDÉE│ │
│  └───────────────────────────────────────────────────────────────────┘ │
└────────────────────────────────────────────────────────────────────────┘
```

**Améliorations clés vs CdC** : Signaux faibles → APR (RG-09), Thème UI différent Prod (RG-0804), Panneau APR permanent.

---

### P3 : ORCHESTRATION WORKFLOW (Semi-Automatique Palier 1)

**Objectif** : Exécuter des opérations d'arrêt/démarrage/partiel/reconstitution avec sécurité maximale, conformément RG-ARRÊT-01 / RG-DÉMARRAGE-01.

```
┌──────────────────────────────────────────────────────────────────────────┐
│  P3 — Orchestration Workflow Semi-Automatique                             │
│                                                                          │
│  P3.1  PRÉPARATION OPÉRATION (avant toute commande)                      │
│        ├─ Séléction Workflow compatible rôle/habilitations              │
│        ├─ Prod : Saisie OBLIGATOIRE motif/impact/ticket/fenêtre(RG-0303)│
│        ├─ MODE SIMULATION disponible (FR-005)                            │
│        └─ Choix Continuity Center → ResterActif / Basculer (RG-0306)    │
│                                                                          │
│  P3.2  EXÉCUTION PRÉ-CHECKS AUTOMATIQUES (FR-012)                        │
│        ├─ Checklist dans l'ORDRE RG-0307 TOP10 causes P1                │
│        │   1) Horloges <1s  2) Disques <85%  3) Services sains          │
│        │   4) Réseau ports  5) Shared Folder accessible                  │
│        │   6) DB connections OK  7) PAS snapshot VM en cours            │
│        │   8) Verrous concurrence  9) Tâches en cours N4               │
│        │   10) PAS split-brain en cours (double Active Center)          │
│        ├─ Chaque contrôle : SatisFait / Avertissement / BLOQUANT        │
│        ├─ Si BLOQUANT → STOP (contournement = approb Prod double)       │
│        └─ JSON sauvegardé dans EXE.RapportPreflight                     │
│                                                                          │
│  P3.3  APPROBATIONS (SIMPLE ou DOUBLE Prod)                              │
│        ├─ 1er approbateur si Criticité ≥ Moyenne                        │
│        ├─ 2e approbateur DISTINCT (RG-0801 séparation)                 │
│        ├─ Délai validité 120min (expiré = refaire)                      │
│        ├─ Fenêtre "30s calme obligatoire" confirmation opération CRIT.  │
│        └─ **VERROU MUTEX GLOBAL PAR ENV (RG-0305)** : 1 seule mutative  │
│                                                                          │
│  P3.4  EXÉCUTION SÉQUENCÉE WORKFLOW (RG-ARRÊT-01 / RG-DÉMARRAGE-01)     │
│        ├─ Stepper graphique UI                                          │
│        ├─ Étape : EN ATTENTE → EN COURS → VÉRIFICATION → RÉUSSI/ÉCHEC  │
│        ├─ EXÉCUTION COMMANDE (via Connecteur PS sécurisé)               │
│        │     • Cmd expurgée dans EXEETP                                 │
│        │     • Secrets MASQUÉS avant journalisation (SEC-003)           │
│        ├─ **PROOF TECHNIQUE obligatoire (RG-0402) après mutation**      │
│        │     → Présence ReadyPattern journal + Status service + port    │
│        ├─ Parallélisation SEULEMENT si validateur (RG-0403)             │
│        │     • ClusterNodes = OK parallèle                              │
│        │     • Center / Bridge / XPS = JAMAIS parallèles                │
│        ├─ Cas spéciaux :                                                │
│        │  • Service bloqué StopPending (RG-0407) :                      │
│        │    → Stop Force = INTERDIT AUTOMATIQUEMENT, décision HUMAINE   │
│        │  • GROSSE QUEUE BRIDGE (RG-0506) :                             │
│        │    → Bouton Restart Bridge GRISÉ + recommandation Clusters    │
│        ├─ RETRIES non-auto pour CRITIQUE/DESTRUCTRICE (RG-0408)        │
│        └─ **MINUTEUR DE CALME obligatoire (30s) avant action CRIT Prod**│
│                                                                          │
│  P3.5  CONTOURNEMENT / PAUSE / REPRISE / ANNULATION                     │
│        ├─ PAUSE → reprendre = RÉCONTROLE ÉTAT RÉEL (RG-0404)            │
│        │   (jamais répéter aveuglément)                                 │
│        ├─ ANNULATION → atteindre point sûr RG-0405 → Rapport PostAnnul. │
│        ├─ CONTOURNEMENT : habilité + motif + risque + double approb Prod│
│        │   (RG-0406 tout logué EXEETP.SkippedBy...)                    │
│        ├─ CRASH MOTEUR : reprise via DernierHeartbeat > 5min           │
│        │   → État réel reconstruit depuis signaux + reprise pas à pas   │
│        └─ Interv. manuelle guidée : instruction + preuve jointe OBLIG. │
│                                                                          │
│  P3.6  CLÔTURE + RAPPORT AUTO                                           │
│        ├─ Résultat final : Succès / SuccèsAvertiss / Échec / Annulé    │
│        ├─ Génération RAPPORT PDF/Word FR-028 :                          │
│        │   • Chrono, durées par étape,                                  │
│        │   • Commandes expurgées, preuves techniques collectées,        │
│        │   • Erreurs, confirmations, contournements,                    │
│        │   • États initial & final réel (vs déclaré)                    │
│        ├─ État initial vs état final comparatif                         │
│        └─ Si réussite + mode NON-simul : proposition GÉNÉRATION SOP     │
│           (FR-089B auto en Brouillon → validation humaine)              │
│                                                                          │
│  ┌─DONNÉES LIÉES───────────────────────────────────────────────────────┐│
│  │ EXECUTION_WKF, ÉTAPE_EXÉCUTION, VERROU_ENV, RAPPORT preflight+final ││
│  └─────────────────────────────────────────────────────────────────────┘│
└──────────────────────────────────────────────────────────────────────────┘
```

---

### P4 : DIAGNOSTIC INCIDENT + ANALYSE LOGS (8 Phases Non Linéaires)

**Objectif** : Guider diagnostic, éviter biais cognitifs, produire explications + preuves.

```
┌──────────────────────────────────────────────────────────────────────────┐
│  P4 — Diagnostic Incident P1-P2 + Analyse Journaux (8 Phases)            │
│                                                                          │
│  ═══════════════════════════════════════════════════════════════════      │
│  ║  CYCLE 8 PHASES NON LINÉAIRES (RG-0501 — retours arrières ok)  ║      │
│  ║  1. Détection → 2. Qualif/Collecte → 3. Sécurisation →         ║      │
│  ║  4. Diagnostic&Corrélation → 5. Plan → 6. Valid/Exécution →    ║      │
│  ║  7. Remise Service → 8. Clôture + Capitalisation                 ║      │
│  ═══════════════════════════════════════════════════════════════════      │
│                                                                          │
│  P4.1  OUVERTURE SESSION DIAGNOSTIC                                      │
│        ├─ Source : Alerte / Manuellement / Étape bloquée WKF             │
│        ├─ Définir Fenêtre analyse (défaut 4h) + ticket + symptôme       │
│        ├─ **Obligation : CHECKLIST RG-0307 TOP10 causes P1**             │
│        │   → Éliminer dans l'ORDRE avant creuser plus loin              │
│        └─ Passe en Phase #1 (DétectionEnregistrement)                   │
│                                                                          │
│  P4.2  COLLECTE DE JOURNAUX CIBLÉE ou IMPORTÉE                           │
│        ├─ Collecte ciblée : sélection composants + période →            │
│        │   connecteur lit dossiers logs ProgramData\Navis\...            │
│        ├─ IMPORT MANUEL : extension autorisée + scan antivirus (SEC-007)│
│        ├─ Auto-identification :                                        │
│        │   • Format (log4j, EventLog)                                   │
│        │   • Version N4 détectée                                        │
│        │   • Composant suggéré via chemin/contenu                       │
│        │   • Fuseau horaire détecté → écart horloge calculé             │
│        ├─ **MASQUAGE SECRETS (RG-0601)** avec compte des masquages →    │
│        ├─ Empreinte SHA256 contenu MASQUÉ (RG-0602 intégrité)           │
│        ├─ **RÉSUMÉAUTO début/fin + nb INFO/WARN/ERROR**                 │
│        └─ Échec collecte : TAXINOMIE RG-0605 AccèsRefusé / Timeout /    │
│           FormatNonReconnu / NonConfiguré / SourceAbsente               │
│                                                                          │
│  P4.3  PARSING + DÉTECTION ANOMALIES                                     │
│        ├─ Découper en événements :                                      │
│        │   Horodatage, Niveau, Thread, Classe Logger, Stack, CodeErr,   │
│        │   IdTransaction                                                 │
│        ├─ RegEx contre CATALOGUE SIGNATURES + POIDS CONFIANCE           │
│        ├─ CONSTATs = 1 anomalie détectée (412 occurrences groupées =   │
│        │   1 Constat avec nbOccurrences=412)                            │
│        ├─ Contexte ± lignes (masqué) autour l'erreur                    │
│        ├─ Constat sans SignatureId = ERREUR NON CATALOGUÉE → Alerte     │
│        └─ Référence copiée : Signification / Remediation / DocRef       │
│                                                                          │
│  P4.4  CORRÉLATION MULTI-SIGNAUX & HYPOTHÈSES                            │
│        ├─ Application RÈGLES CORR sur fenêtre temps (SPLIT-BRAIN...)    │
│        ├─ **Comparaison à une référence (RG-0505, affirmation HUMAIN)** │
│        │   • Période saine (autre session diag)                         │
│        │   • Exécution workflow réussie                                 │
│        │   • Valeurs habituelles composant (historique signaux)         │
│        │   • Nœud pair (Cluster 1 vs 2)                                 │
│        ├─ HYPOTHÈSES ordonnées par CONFIANCE (0-100)                    │
│        │   • ElementDePreuve sourcé (Signature, règle, signal)          │
│        │   • ElementContrePreuve (null = mentionné explicitement)       │
│        │   • Confiance > Seuil (déf 65%) = Hypothèse "établie"          │
│        └─ CAS INCONCLUANT : ESCALADE OBLIGATOIRE (RG-0504)              │
│           → Infra → Support Navis → Package escalade :                   │
│             exports diagnostics, logs, stats, hypothèses, preuves       │
│                                                                          │
│  P4.5  VERDICT FINAL 7 NIVEAUX (RG-0502)                                 │
│        ├─ Jamais OK/NOK binaire → 7 nuances précises                    │
│        ├─ Limites explicités dans ExplicationVerdict                    │
│        └─ Périmètre confirmé (ce qu'on a vu) vs non vérifié             │
│                                                                          │
│  P4.6  CLÔTURE ET CAPITALISATION                                         │
│        ├─ Validation Humaine, enregistrement Versions règles/sig        │
│        ├─ Génération FICHE INCIDENT CAPITALISÉE (AMÉLIORATION)          │
│        │   • MTTR calculé auto (Fin-Début)                               │
│        │   • Niveau compétence résolution (N1 / N2 / N3 / Expert)       │
│        │   • Cause Racine 5 Pourquoi                                    │
│        │   • SOPs utilisés liste + SOP généré auto (Brouillon)          │
│        │   • Leçons tirées → actions correctives                        │
│        ├─ Calcul MÉTRIQUE BUSINESS (METBUS) :                           │
│        │   MTTR, nb P1 résolus N1/N2 vs Expert                          │
│        └─ Indexation post-mortem dans corpus Base Documentaire          │
│                                                                          │
│  ┌─DONNÉES LIÉES───────────────────────────────────────────────────────┐│
│  │ SESSION_DIAG, TRANSITION_PHASE, SOURCE_JOURNAL, CONSTAT_ANOMALIE,   ││
│  │ HYPOTHÈSE, FICHE_INCIDENT_CAPITALISÉ, MÉTRIQUE_BUSINESS, PACK_ESCAL ││
│  └─────────────────────────────────────────────────────────────────────┘│
└──────────────────────────────────────────────────────────────────────────┘
```

---

### P5 : BASE DOCUMENTAIRE + ASSISTANT N4 IA (RAG)

**Objectif** : Réponses sourcées, JAMAIS action directe, structurées en SOP.

```
┌──────────────────────────────────────────────────────────────────────────┐
│  P5 — Base Documentaire + Assistant Q&R sourcé (séparation Conseil/Action)│
│                                                                          │
│  P5.1  INDEXATION CORPUS (RAG offline)                                    │
│        ├─ DOCUMENTS : 3 guides officiels Navis, procédures internes,    │
│        │   post-mortems, SOP validés                                     │
│        ├─ Parsing PDF : Structure §, page, figures ref                  │
│        ├─ Chunks → Embeddings → Index Vectoriel                         │
│        ├─ EstIndexePourRAG flag                                          │
│        └─ Ré-indexation automatique après modif doc / ajout             │
│                                                                          │
│  P5.2  INTERFACE RECHERCHE LIBRE                                         │
│        ├─ Mots-clés + filtres : Catégorie, Document, Version N4, Page   │
│        └─ Extrait pertinent avec §, n° page, doc.                       │
│                                                                          │
│  P5.3  ASSISTANT N4 Q&R LANGAGE NATUREL                                  │
│        ├─ Question utilisateur → recherche chunk pertinents             │
│        ├─ **RÉPONSE OBLIGATOIREMENT STRUCTURÉE** :                      │
│        │   1. Réponse courte directe                                     │
│        │   2. Preuves : [Doc §Section Page] × n citations               │
│        │   3. Explication pas à pas                                      │
│        │   4. Mise en garde/limites                                      │
│        │   5. Escalade recommandée si précision < seuil                  │
│        ├─ **SÉPARATION STRICTE CONSEIL vs ACTION :**                    │
│        │   → Assistant ne déclenche JAMAIS action technique             │
│        │   → Bouton : "Créer un Workflow / SOP depuis cette réponse"    │
│        │   (FR-088, BROUILLON → humain valide)                          │
│        └─ Format SOP proposé si procédure (Objectif/Périmètre/Prérequis │
│           / Risques / Étapes / Contrôles / Retour arrière / Escalade)    │
│                                                                          │
│  P5.4  RETOUR UTILISATEUR + SIGNALEMENT DOCUMENT                         │
│        ├─ Chaque réponse : Utile / Non utile                            │
│        ├─ Commentaire libre : précision manquante, info fausse, § manquant│
│        └─ Notifications AdminSolution pour corrections                  │
│                                                                          │
│  ┌─DONNÉES LIÉES───────────────────────────────────────────────────────┐│
│  │ DOCUMENT_REFERENCE, RETOUR_UTILISATEUR_DOC, Corpus chunks index (hors││
│  │ base SQL ou Vector DB dédié)                                         ││
│  └─────────────────────────────────────────────────────────────────────┘│
└──────────────────────────────────────────────────────────────────────────┘
```

---

### P6 : SOP — Capitalisation & Exécution Humaine Guidée

```
┌──────────────────────────────────────────────────────────────────────────┐
│  P6 — SOP : Capitalisation Incident → Procédures Réutilisables           │
│                                                                          │
│  P6.1  GÉNÉRATION AUTOMATIQUE SOP (Brouillon) FR-088 / FR-089            │
│        ├─ Source 1 : Workflow réussi (non simulé) → EXE.WKF → SOP       │
│        ├─ Source 2 : Incident clos → SESDIAG + FICHE INCIDENT → SOP     │
│        ├─ Source 3 : Réponse doc Assistant → bouton → SOP              │
│        └─ Statut = BROUILLON, RequiertValidationHumaine = OUI           │
│                                                                          │
│  P6.2  CIRCUIT DE VALIDATION SOP (AdminSolution → Valide → Actif)        │
│        ├─ Humain vérifie : objectif, prérequis, risques, retour arrière │
│        ├─ Version incrément (RG-0702)                                    │
│        ├─ Associations SOP ↔ CMP ↔ SIGDIAG ↔ SESDIAG (SOPASSOC)         │
│        │   → permet suggestions auto lors incidents similaires          │
│        └─ **Taux de Réussite Historique** par CodeSOP → affichage lors  │
│           du choix (Suggère jamais < 50%)                                │
│                                                                          │
│  P6.3  EXÉCUTION SOP HUMAINE 100% CONFIRMÉE                              │
│        ├─ SOP ≠ WKF : PAS d'automatisation (RG-0701)                    │
│        ├─ Stepper comme WKF mais chaque étape : INSTRUCTION → HUMAIN    │
│        │   lit + confirme + saisit preuve (texte ou capture écran jointe)│
│        ├─ Écarts vs attendu = NoteEcart + classification Problème       │
│        ├─ Rôle élévé si RequiertRoleSensible (RG-0704)                  │
│        └─ Fin : Résultat attesté HUMain SEUL (Problème Résolu / Non /   │
│           Indéterminé) → alimente taux de réussite historique           │
│                                                                          │
│  P6.4  RÉUTILISATION INTELLIGENTE SOP SIMILAIRE                          │
│        ├─ Lors ouverture Diagnostic → SOP suggéré via SIGDIAG ou rôles  │
│        ├─ Tri par TauxRéussiteHist + Même symptôme + Même Cause         │
│        └─ "Appliquer SOP X (taux 78% réussite) ?" → lien direct exécution│
│                                                                          │
│  ┌─DONNÉES LIÉES───────────────────────────────────────────────────────┐│
│  │ SOP_MODELE, EXECUTION_SOP, ÉTAPE_EXECUTION_SOP, ASSOCIATION_SOP     ││
│  └─────────────────────────────────────────────────────────────────────┘│
└──────────────────────────────────────────────────────────────────────────┘
```

---

### P7 : Historique, Rapports, Indicateurs MTTR, Audit

```
┌──────────────────────────────────────────────────────────────────────────┐
│  P7 — Capitalisation historique, rapports, métriques business            │
│                                                                          │
│  P7.1  HISTORIQUE OPÉRATIONS CONSULTABLE                                 │
│        ├─ Vue Environnement / Date / Type WF / Opérateur / Résultat     │
│        ├─ Détail EXE + étapes + preuves + rapports PDF                  │
│        ├─ Filtres multiples + export CSV                                 │
│        └─ Mémoire organisationnelle : "Erreur X → déjà vu il y a 12j,    │
│           résolue par action Y, durée 17min"                             │
│                                                                          │
│  P7.2  RAPPORTS D'INCIDENTS + POST-MORTEMS auto                          │
│        ├─ Génération rapport PDF depuis FICHE_INCIDENT_CAPITALISÉ       │
│        │   • MTTR, symptôme, hypothèses, cause racine 5 pourquoi,       │
│        │   • actions correctrices, impact métier, mesures compensatoires│
│        └─ Format conforme support Navis + DSI CIV                       │
│                                                                          │
│  P7.3  TABLEAU DE BORD MÉTRIQUES BUSINESS                                │
│        ├─ MTTR P1 mensuel / vs cible (÷2) vs baseline avant Sentinel   │
│        ├─ # P1 / Mois tendance vs cible -50%                            │
│        ├─ % P1 résolus par N1/N2 vs Expert N3 (objectif V1=30% / V2=70%)│
│        ├─ Taux de réussite Workflow par version (< 95% → alerte qualité)│
│        ├─ # APR réalisées vs # APR ignorées (tendance adoption prévention)│
│        └─ Taux SuggestionSOP appliquées → impact MTTR mesuré            │
│                                                                          │
│  P7.4  JOURNAL AUDIT CONSULTATION (Auditeurs / CISO)                     │
│        ├─ Recherche : User, Date, Action, Issue (Succès/Refus/Échec),  │
│        │   Environnement, CorrelationID                                 │
│        ├─ **Append-Only** : JAMAIS effacé, JAMAIS modifié (trigger SQL) │
│        ├─ Rétention 7 ans (SEC-009)                                     │
│        └─ Export PDF/Word pour conformité audit interne                 │
│                                                                          │
│  ┌─DONNÉES LIÉES───────────────────────────────────────────────────────┐│
│  │ EXE, EXEETP (recherche historique), FICHE_INCIDENT_CAPITALISÉ,      ││
│  │ MÉTRIQUE_BUSINESS_SUCCES, JOURNAL_AUDIT, RAPPORTS stockés Blob      ││
│  └─────────────────────────────────────────────────────────────────────┘│
└──────────────────────────────────────────────────────────────────────────┘
```

---

### P8 : Sécurité (RBAC + MFA + Secrets + Retentions)

```
┌──────────────────────────────────────────────────────────────────────────┐
│  P8 — Sécurité Transverse                                                 │
│                                                                          │
│  P8.1  AUTHENTIFICATION                                                  │
│        ├─ V1 : Identité Windows + MFA Email/TOTP (RG-0801)              │
│        ├─ V2 : Azure AD/SSO + groupes rôles automatiques                │
│        └─ Comptes NOMINATIFS OBLIGATOIRES (pas partagés)                │
│                                                                          │
│  P8.2  AUTORISATIONS RBAC (séparation devoirs)                           │
│        ├─ 8 Profils (Lecteur/Operateur/AdminN4/AdminInfra/Validateur/   │
│        │   AdminSolution/Auditeur/TOSManager)                           │
│        ├─ HABILITATION = U × R × ENV (droit Prod ≠ UAT)                 │
│        └─ Vérifications systématiques (API middleware) :                │
│           - PeutConsulter / PeutExecuterActions / ActionsSensibles /    │
│             PeutApprouver / Administrer / Audit / Habilitations         │
│                                                                          │
│  P8.3  SECRETS & COFFRE                                                  │
│        ├─ Credentials JAMAIS en clair / appsettings Prod                │
│        ├─ Stockage via DPAPI (V1) / Key Vault (V2)                      │
│        ├─ Credentials = chiffrement repos + utilisation dans mémoire    │
│        └─ Journalisation accès coffre dans AUDIT                        │
│                                                                          │
│  P8.4  POLITIQUES RÉTENTION APPLIQUÉES                                   │
│        ├─ Job quotidien applique POLITIQUE RET par catégorie :          │
│        │   • Logs techniques : 30j → suppression                        │
│        │   • Rapports : 1 an → archivage                                │
│        │   • Audits : 7 ans (RG-0806 SEC-009)                           │
│        │   • WKF / SOP validés : permanent                              │
│        └─ Traçabilité des suppressions dans AUDIT                       │
│                                                                          │
│  ┌─DONNÉES LIÉES───────────────────────────────────────────────────────┐│
│  │ UTILISATEUR, RÔLE, HABILITATION, DROIT_SPÉCIFIQUE, PROFIL_CREDENTIEL││
│  │ POLITIQUE_RETENTION, JOURNAL_AUDIT, CONFIGURATION_AZURE_AD          ││
│  └─────────────────────────────────────────────────────────────────────┘│
└──────────────────────────────────────────────────────────────────────────┘
```

---

## Matrice Processus × Données (vue transversale)

| Processus | ENV | SRV | CMP | WKF | EXE | SESDIAG | SOP | FICH_INC | AUDIT | ALT | APR |
|---|---|---|---|---|---|---|---|---|---|---|---|
| **P1 Référentiel** | CRUD | CRUD | CRUD | CRUD | | | | | R | R | R |
| **P2 Supervision** | R | R | R | | | | | | R | **CRUD** | **CRUD** |
| **P3 Orchestration** | R | R | R | R | **CRUD** | C/R | C/R | | C/A | R | U |
| **P4 Diagnostic** | R | R | R | | R | **CRUD** | R | C/R | R/A | R | |
| **P5 Documentaire** | | | | | | | | | R | | |
| **P6 SOP** | R | R | R | R | R | R | **CRUD** | R | R/A | R | U |
| **P7 Historique** | R | R | R | R | R | R | R | **R** | **R** | R | R |
| **P8 Sécurité** | R | R | R | | | | | | **A** | | |

**Légende** : C=Create, R=Read, U=Update, D=Delete, A=Append (Audit append only)

---

## Fin du Modèle Conceptuel de Traitements (MCT)
