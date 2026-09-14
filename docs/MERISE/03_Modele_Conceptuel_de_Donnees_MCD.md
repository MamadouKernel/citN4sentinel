# Analyse MERISE — N4 Sentinel
## 03. Modèle Conceptuel de Données (MCD)

**Notation** : Chen / Merise avec cardinalités `(0,n)`, `(1,1)`, `(0,1)`, `(1,n)`
**Source de vérité alignée** :
- [N4Component.cs](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/N4Component.cs)
- [WorkflowExecution.cs](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/WorkflowExecution.cs)
- [Diagnostic.cs](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/Diagnostic.cs)
- [Sop.cs](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/Sop.cs)
- [Enums.cs](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/Enums.cs)
- + AMÉLIORATIONS : APR, Fiche Incident Capitalisé, Métriques Business

---

## Tableau des Entités du MCD (46 entités)

| # | Groupe Thématique | Code Entité | Nom Entité | Clé Primaire | Nature |
|---|---|---|---|---|---|
| G1 | **RÉFÉRENTIEL** | | | | |
| 1 | | `ENV` | ENVIRONNEMENT | `IdEnvironnement` | Indépendante |
| 2 | | `SRV` | SERVEUR | `IdServeur` | Indépendante |
| 3 | | `CMP` | COMPOSANT N4 | `IdComposant` | Indépendante |
| 4 | | `RDY` | PROFIL DE PREUVE COMPOSANT | `IdProfilPreuve` | Dépendante 1:1 de CMP |
| 5 | | `DOSP` | PROFIL DOSSIER PARTAGÉ | `IdDossierPartage` | Dépendante 1:1 de CMP |
| 6 | | `DEP` | DÉPENDANCE ENTRE COMPOSANTS | `IdDependance` | Association (CMP-CMP) |
| 7 | | `SVCASSOC` | SERVICE WINDOWS ASSOCIÉ | `IdServiceAssocie` | Faible (1,n CMP) |
| G2 | **SÉCURITÉ** | | | | |
| 8 | | `USR` | UTILISATEUR NOMINATIF | `IdUtilisateur` | Indépendante |
| 9 | | `ROL` | RÔLE APPLICATIF | `IdRole` | Indépendante |
| 10 | | `HAB` | HABILITATION (U×R×E) | `IdHabilitation` | Association USR-ROL-ENV |
| 11 | | `CREDPROF` | PROFIL CRÉDENTIEL TECHNIQUE | `IdCredential` | Indépendante |
| G3 | **WORKFLOW — MODÈLE** | | | | |
| 12 | | `WKF` | WORKFLOW MODÈLE | `IdWorkflow` | Indépendante |
| 13 | | `ETP` | ÉTAPE WORKFLOW MODÈLE | `IdEtapeModele` | Faible (1,n WKF) |
| 14 | | `VRF` | CONTRÔLE PRÉALABLE MODÈLE (PRÉ-CHECK) | `IdControleModele` | Faible (1,n WKF) |
| 15 | | `APROBMAT` | RÈGLE MATRICE APPROBATION | `IdRegleApprobation` | Indépendante |
| G4 | **WORKFLOW — EXÉCUTION** | | | | |
| 16 | | `EXE` | EXÉCUTION WORKFLOW | `IdExecution` | Indépendante |
| 17 | | `EXEETP` | ÉTAPE D'EXÉCUTION | `IdEtapeExecution` | Faible (1,n EXE) |
| 18 | | `VERR` | VERROU ENVIRONNEMENT MUTEX | `IdVerrou` | Association ENV-EXE |
| G5 | **SUPERVISION & SIGNaux FAIBLES** | | | | |
| 19 | | `SIG` | SIGNAL COMPOSANT HISTORIQUE | `IdSignal` | Faible (1,n CMP) |
| 20 | | `ALT` | ALERTE SUPERVISION / PRÉDICTIVE / APR | `IdAlerte` | Indépendante |
| 21 | | `APR` | ACTION PRÉVENTIVE RECOMMANDÉE | `IdActionPreventive` | Indépendante (AMÉLIORATION) |
| G6 | **DIAGNOSTIC INCIDENT** | | | | |
| 22 | | `SESDIAG` | SESSION DIAGNOSTIC | `IdSessionDiagnostic` | Indépendante |
| 23 | | `PHDIAG` | TRANSITION DE PHASE DIAGNOSTIC | `IdTransitionPhase` | Faible (1,n SESDIAG) |
| 24 | | `SIGDIAG` | SIGNATURE ANOMALIE CATALOGUÉE | `IdSignature` | Indépendante |
| 25 | | `REGCORR` | RÈGLE DE CORRÉLATION MULTI-SIGNAUX | `IdRegleCorrelation` | Indépendante |
| 26 | | `CONDCORR` | CONDITION RÈGLE CORRÉLATION | `IdConditionCorrelation` | Faible (1,n REGCORR) |
| 27 | | `LOGSRC` | SOURCE JOURNAL ANALYSÉ | `IdSourceJournal` | Faible (1,n SESDIAG) |
| 28 | | `LOGCONST` | CONSTAT D'ANOMALIE DANS LOG | `IdConstat` | Faible (1,n LOGSRC) |
| 29 | | `HYPDIAG` | HYPOTHÈSE DIAGNOSTIC | `IdHypothese` | Faible (1,n SESDIAG) |
| G7 | **BASE DOCUMENTAIRE** | | | | |
| 30 | | `DOC` | DOCUMENT RÉFÉRENCE | `IdDocument` | Indépendante |
| 31 | | `DOCRET` | RETOUR UTILISATEUR SUR REPONSE DOC | `IdRetourDoc` | Faible (1,n DOC) |
| G8 | **SOP** | | | | |
| 32 | | `SOP` | SOP MODÈLE | `IdSOP` | Indépendante |
| 33 | | `SOPEX` | EXÉCUTION SOP | `IdExecutionSOP` | Indépendante |
| 34 | | `SOPEXETP` | ÉTAPE EXÉCUTION SOP | `IdEtapeExecutionSOP` | Faible (1,n SOPEX) |
| 35 | | `SOPASSOC` | ASSOCIATION SOP → CMP / SIGDIAG / SESDIAG | `IdAssociationSOP` | Association SOP-vers-entités |
| G9 | **AUDIT & RÉTENTION** | | | | |
| 36 | | `AUD` | ENTRÉE JOURNAL AUDIT (IMMUABLE) | `IdEntreeAudit` | Indépendante append-only |
| 37 | | `RET` | POLITIQUE RÉTENTION CATÉGORIE | `IdPolitiqueRetention` | Indépendante |
| G10 | **CAPITALISATION & MÉTRIQUES BUSINESS (AMÉLIORATIONS)** | | | | |
| 38 | | `INC` | FICHE INCIDENT CAPITALISÉ | `IdIncidentCapitalise` | Indépendante |
| 39 | | `METBUS` | MÉTRIQUE BUSINESS SUCCÈS PÉRIODE | `IdMesureBusiness` | Indépendante |
| G11 | **DIVERS / EDI** | | | | |
| 40 | | `ACT_EXT` | ACTION EXTERNE DÉCLARÉE | `IdActionExterne` | Association multi-supports |
| 41 | | `EDIFIC` | FICHIER EDI SUIVI | `IdFichierEdi` | Indépendante |
| G12 | **PARAMÉTRAGES GÉNÉRAUX** | | | | |
| 42 | | `DIAGSETT` | PARAMÉTRAGES DIAGNOSTIC (seuils) | `IdParametrageDiagnostic` | Singleton ENV |
| 43 | | `POLRETENTION_APPLICATEE` | APPLICATION POLITIQUE RÉTENTION À TYPE DONNÉES | `IdApplicationRetention` | Association RET-type |
| 44 | | `GRANTSPECIFIQUE` | DROIT SPÉCIFIQUE ACCÈS ENVIRONNEMENT | `IdDroitSpecifique` | Faible ENV-USR |
| 45 | | `AZUREADCONF` | CONFIGURATION AZURE AD INTÉGRATION | `IdConfigAzureAd` | Singleton global |
| 46 | | `COMMISSIONSTATUS` | ÉTAT MISE EN SERVICE COMPOSANT | `IdCommissionStatut` | Faible CMP |

---

## Diagramme MCD — Bloc 1 : Référentiel & Sécurité

```
 ┌──────────────┐       ┌──────────────┐      ┌──────────────┐
 │   ENVIRON.   │1,1   │   HABILIT.   │0,n  │ UTILISATEUR  │
 │    ENV       │───────┤    HAB       ├──────┤    USR       │
 │ (Production, │       │ (U×R×E)      │      │ (nominatif)  │
 │  UAT, ...)   │       └──┬─────┬─────┘      └──────────────┘
 └──────┬───────┘          │0,n  │0,n
        │1,1               │     │           ┌──────────────┐
        │                  │     └───────────┤  RÔLE APP.   │
        │                  │                 │    ROL       │
        │1,1               ▼                 │(Lecteur,...) │
  ┌─────┴──────┐    ┌──────────────┐         └──────────────┘
  │  SERVEUR   │1,n │ COMPOSANT    │1,1
  │   SRV      ├────┤   CMP        ├───────┐
  │ (phys/VM)  │    │ (Cluster,    │       │1,1
  └────────────┘    │  Center,     │  ┌────┴─────────────┐   ┌──────────────────┐
                    │  Bridge, XPS,│  │ PROFIL PREUVE    │1,n│ SERV. ASSOCIES   │
                    │  ECN4, DB,   │  │  RDY             ├───┤  SVCASSOC        │
                    │  Dossier...) │  │ (ReadyPatterns,  │   │ (BridgeControl,  │
                    └──┬────┬──┬───┘  │  Timeouts, ...)  │   │  XPSGateService) │
                       │1,n │  │1,1   └──────────────────┘   └──────────────────┘
                       │    │  │
        ┌──────────────┘    │  └──────┐1,1    ┌──────────────────┐
        ▼                   ▼         ▼       │ PROFIL DOSSIER   │
  ┌────────────────┐  ┌──────────────┐       │  PARTAGÉ DOSP     │
  │ DÉPENDANCE CMP │  │ CRÉDENTIALS  │       │ (chemin, cat.,    │
  │     DEP        │  │  CREDPROF    │       │  seuils latence)  │
  │ (CMP → CMP)    │  │ (coffre)     │       └──────────────────┘
  └────────────────┘  └──────────────┘
```

### Cardinalités Référentiel (texte complet)

| Association | Entité A | Card. A | Entité B | Card. B | Commentaire |
|---|---|---|---|---|---|
| `ENV—SRV` | ENVIRONNEMENT | `(1,1)` | SERVEUR | `(0,n)` | Un serveur appartient à 1 env ; un env a 0..n serveurs |
| `ENV—CMP` | ENVIRONNEMENT | `(1,1)` | COMPOSANT | `(0,n)` | 1 composant = 1 env (RG-0101) ; 1 env = 0..n composants |
| `SRV—CMP` | SERVEUR | `(1,1)` | COMPOSANT | `(0,n)` | 1 composant hébergé sur 0..1 serveur (système ext = null) |
| `CMP—RDY` | COMPOSANT | `(1,1)` | PROFIL PREUVE | `(1,1)` | 1:1 — chaque composant configurable a son profil preuve |
| `CMP—DOSP` | COMPOSANT | `(1,1)` | PROFIL DOSSIER PARTAGÉ | `(0,1)` | 0..1 si composant rôle = DossierPartage ; sinon 0 |
| `CMP—DEP-soure` | COMPOSANT | `(1,1)` | DÉPENDANCE | `(0,n)` | CMP est source de 0..n dépendances |
| `DEP—CMP-cible` | DÉPENDANCE | `(1,1)` | COMPOSANT | `(1,1)` | Chaque dépendance pointe vers 1 composant cible |
| `CMP—SVCASSOC` | COMPOSANT | `(1,1)` | SERVICE ASSOCIÉ | `(0,n)` | 0+ services Windows associés par composant (Bridge, XPS, ECN4) |
| `USR—HAB` | UTILISATEUR | `(1,1)` | HABILITATION | `(0,n)` | 1 user a 0..n habilitations (rôles × env) |
| `ROL—HAB` | RÔLE | `(1,1)` | HABILITATION | `(0,n)` | 1 rôle utilisé par 0..n habilitations |
| `ENV—HAB` | ENVIRONNEMENT | `(1,1)` | HABILITATION | `(0,n)` | habilitation attachée à 1 env |
| `SRV—CREDPROF` | SERVEUR | `(0,1)` | CRÉDENTIAL | `(0,n)` | credentials rattachés à env/serveur |
| `ENV—CREDPROF` | ENVIRONNEMENT | `(1,1)` | CRÉDENTIAL | `(0,n)` | credentials globaux d'env |

---

## Diagramme MCD — Bloc 2 : Workflow Modèle + Exécution

```
 ┌──────────────┐      ┌────────────────┐      ┌───────────────────────┐
 │  ENVIRONNEMENT│1,1  │ EXÉCUTION WKF  │0,n   │ ÉTAPE D'EXÉCUTION     │
 │    ENV       ├──────┤    EXE         ├─────────►  EXEETP             │
 └──────┬───────┘      └──┬─────────┬───┘0,n     └───────────────────────┘
        │1,1               │1,1      │1,1
        │                  ▼         ▼              ┌──────────────┐
 ┌──────┴────────┐  ┌─────────┐  ┌────────────┐1,1 │ VERROU MUTEX │1,1
 │ WORKFLOW MOD. │  │  USR    │  │  WORKFLOW  ├─────┤   VERR       ├───┐
 │    WKF        │  │(demande,│  │  (figé au  │     └──────────────┘   │
 │ (1→n ETAPES)  │  │ approb) │  │  lancement)│     ┌──────────────┐1,1 │
 └──────┬────────┘  └─────────┘  └────────────┘     │ COMPOSANT    ├────┘
        │1,1                                     ┌───┤  (cible étape)│
        ▼0,n                                     │   └──────┬───────┘
 ┌──────────────┐     ┌──────────────┐          │          │1,1
 │  ÉTAPE MOD.  │1,n  │ CONTRÔLE PRÉ │0,n       │   ┌──────▼───────┐
 │  ETP         ├─────┤  VRF         │          │   │ SESSION DIAG.│
 │(ordre, action│     │(PRÉ-CHECK)   │          │   │SESDIAG ratt. │
 │ timeout, ...)│     └──────────────┘          │   └──────────────┘
 └──────────────┘                               │
                                                │
 ┌──────────────────┐     ┌──────────────┐      │
 │ MATRICE APPROB.  │1,1  │ RÈGLE MATRICE│0,n   │
 │  APPROB GLOBALE  ├─────┤  APROBMAT    │◄─────┘ (appliquées à EXE)
 └──────────────────┘     └──────────────┘
```

### Cardinalités Workflow

| Association | Entité A | Card. A | Entité B | Card. B | Commentaire |
|---|---|---|---|---|---|
| `ENV—WKF` | ENV | `(1,1)` | WKF | `(0,n)` | Un workflow est défini pour 1 environnement |
| `WKF—ETP` | WORKFLOW | `(1,1)` | ÉTAPE MODÈLE | `(1,n)` | Workflow a 1..n étapes au moins |
| `WKF—VRF` | WKF | `(1,1)` | CONTRÔLE PRÉALABLE | `(0,n)` | 0..n contrôles pre-check associés |
| `EXE—WKF` | EXÉCUTION | `(1,1)` | WKF | `(1,1)` | Une exécution = 1 workflow modèle (version figée recopiée dans EXE) |
| `EXE—ENV` | ENV | `(1,1)` | EXÉCUTION | `(0,n)` | 1 env a 0..n exécutions historiques |
| `EXE—EXEETP` | EXÉCUTION | `(1,1)` | ÉTAPE EXÉCUTION | `(1,n)` | 1+ étapes par exécution |
| `EXEETP—CMP` | COMPOSANT | `(0,1)` | ÉTAPE EXÉCUTION | `(0,n)` | Étape vise 0..1 composant (étapes sans cible autorisées : attente, intervention, etc.) |
| `EXEETP—SESDIAG` | SESSION DIAG | `(0,1)` | ÉTAPE EXÉCUTION | `(0,n)` | Étape bloquée peut ouvrir 1 session diag (0..1) |
| `VERR—ENV` | ENV | `(1,1)` | VERROU | `(0,n)` | Un environnement a 0..n verrous dans l'historique |
| `VERR—EXE` | EXÉCUTION | `(1,1)` | VERROU | `(0,1)` | Un verrou appartient à 1 exécution |
| `APROBMAT—ENV` | ENV | `(0,1)` | RÈGLE MATRICE | `(0,n)` | Règle peut cibler 1 env NULL = global |

---

## Diagramme MCD — Bloc 3 : Supervision, Diagnostic, Base Documentaire

```
 ┌─────────────┐1,n  ┌──────────────────────┐     ┌───────────────────┐
 │ COMPOSANT   ├─────┤ SIGNAL COMPOSANT SIG │     │ ACTION PREVENTIVE │
 │    CMP      │     │ (historisation)      │     │    APR            │
 └──────┬──────┘     └──────────────────────┘     └──────┬────────────┘
        │1,n                                            │0,1
        ▼                                         ┌──────▼────┐
 ┌────────────────────────┐                      │  ALERTE   │
 │ ALERTE Supervision/     │                      │   ALT     │
 │ Predictive/APR ALT      │0,1 ┌──────────┐1,1  └─────┬─────┘
 └──────┬─────────────────┘◄────┤ SESSION  │◄─────────┤1,n
        │0,n                    │  DIAG    │          │
        ▼                ┌──────┤ SESDIAG  ├─────┐    │0,1
 ┌──────────────────┐    │0,n   └──┬─┬──────┘0,n │    ▼
 │ SOURCE JOURNAL   │    │        │ │           │ ┌──────────────┐
 │   LOGSRC         │◄───┘        │ │           └─┤  DOCUMENT    │
 │(1→n LOGCONST)    │0,n          │ │             │    DOC       │
 └──────┬───────────┘             │ │             └──────┬───────┘
        │1,1                      │ │                    │0,n
        ▼                         │ │             ┌──────▼──────┐
 ┌──────────────────────┐    ┌────▼─┴──────┐      │ RETOUR DOC  │
 │ CONSTAT ANOMALIE     │    │  HYPOTHÈSES │      │  DOCRET     │
 │   LOGCONST           │    │  HYPDIAG    │      └─────────────┘
 │(reconn. SIGDIAG ?)   │    └─────┬───────┘
 └──────────┬───────────┘          │1,1 (source Sig/Règle)
            │0,1                   ▼
   ┌────────▼───────────┐  ┌──────────────────────┐
   │SIGNATURE CATALOGUÉE│  │  RÈGLE CORRÉLATION   │
   │    SIGDIAG         │  │     REGCORR          │
   │(Editeur/Site, poids│  │(1→n CONDCORR conds.) │
   │ valid. cycle vie)  │  └───────┬──────────────┘
   └────────────────────┘          │1,n
                           ┌───────▼──────────────┐
                           │CONDITION CORRÉLATION │
                           │      CONDCORR        │
                           └──────────────────────┘
```

### Cardinalités Supervision & Diagnostic

| Association | Entité A | Card. A | Entité B | Card. B | Commentaire |
|---|---|---|---|---|---|
| `CMP—SIG` | CMP | `(1,1)` | SIGNAL | `(0,n)` | Historisation supervision |
| `CMP—ALT` | CMP | `(0,1)` | ALERTE | `(0,n)` | Alerte concerne 0..1 composant (globales = 0) |
| `ENV—ALT` | ENV | `(1,1)` | ALERTE | `(0,n)` | Alerte toujours sur 1 env |
| `ALT—SESDIAG` | ALERTE | `(0,1)` | SESSION DIAG | `(0,n)` | Session diag ouverte auto depuis 1 alerte |
| `ALT—APR` | ALERTE | `(0,1)` | APR | `(0,n)` | APR peut être issue d'une alerte prédictive |
| `APR—WKF` | WKF | `(0,1)` | APR | `(0,n)` | APR suggère workflow applicable |
| `SESDIAG—ENV` | ENV | `(1,1)` | SESSION DIAG | `(0,n)` | 1 session sur 1 env |
| `SESDIAG—LOGSRC` | SESDIAG | `(1,1)` | SOURCE JOURNAL | `(0,n)` | 0+ sources de journaux analysés |
| `SESDIAG—PHDIAG` | SESDIAG | `(1,1)` | TRANSITION PHASE | `(1,n)` | 1+ transitions (cycle 8 phases non linéaire) |
| `SESDIAG—HYPDIAG` | SESDIAG | `(1,1)` | HYPOTHÈSE | `(0,n)` | 0+ hypothèses formulées |
| `SESDIAG—SESDIAG (ref)` | SESDIAG base | `(0,1)` | SESDIAG référence | `(0,n)` | Référence période saine = autre session |
| `SESDIAG—EXE (ref)` | EXÉCUTION | `(0,1)` | SESSION DIAG | `(0,n)` | Comparaison à exécution réussie antérieure |
| `SESDIAG—CMP (ref)` | CMP | `(0,1)` | SESSION DIAG | `(0,n)` | Référence valeurs habituelles / nœud pair |
| `LOGSRC—LOGCONST` | SOURCE JOURNAL | `(1,1)` | CONSTAT | `(0,n)` | 0+ anomalies trouvées dans une source |
| `LOGCONST—SIGDIAG` | SIGNATURE | `(0,1)` | CONSTAT | `(0,n)` | Constat reconnu par 0..1 signature cataloguée (sinon = erreur non cataloguée) |
| `LOGSRC—CMP` | COMPOSANT | `(0,1)` | SOURCE JOURNAL | `(0,n)` | journal rattaché à 0..1 composant (désigné par opérateur) |
| `HYPDIAG—SIGDIAG` | SIGNATURE | `(0,n)` | HYPOTHÈSE | `(0,n)` | Hypothèse s'appuie sur 0..n signatures |
| `HYPDIAG—REGCORR` | RÈGLE CORRÉLATION | `(0,n)` | HYPOTHÈSE | `(0,n)` | Hypothèse issue de 0..n règles |
| `REGCORR—CONDCORR` | RÈGLE CORR | `(1,1)` | CONDITION CORR | `(1,n)` | 1+ conditions par règle de corrélation |
| `DOC—DOCRET` | DOCUMENT | `(1,1)` | RETOUR DOC | `(0,n)` | 0+ retours utilisateurs par doc |

---

## Diagramme MCD — Bloc 4 : SOP, Capitalisation Incidents, Audit

```
 ┌──────────────┐       ┌───────────────┐       ┌───────────────────┐
 │  ENVIRONNEM. │1,1    │   SOP MODÈLE  │0,n    │ASSOCIATION SOP    │
 │    ENV       ├───────┤     SOP       ├───────► SOPASSOC          │
 └──────┬───────┘       └──┬─────────┬───┘       │(→ CMP / SIGDIAG / │
        │1,1               │1,1      │1,n        │ → SESDIAG)       │
        ▼                  ▼         ▼            └──┬──┬───────────┘
 ┌──────────────┐  ┌───────────┐  ┌──────────────┐  │0,n│0,n  │0,n
 │ DOCUMENT DOC │  │ EXEC. WKF │  │ EXÉCUTION    │  ▼   ▼     ▼
 └──────┬───────┘  │    EXE    │  │ SOP SOPEX    │ ┌───┐ ┌──┐ ┌──────┐
        │0,1       └─────┬─────┘  └──┬─────────┬─┘CMP │ │SI│ │SESDIAG│
        ▼               │0,1         │1,n      │      └───┘ │GD│ └──────┘
 ┌──────────────┐       ▼            ▼         │            └──┘
 │ SOP : source │  ┌──────────────────────┐    │1,n      ┌───────────────┐
 │ depuis doc/  │  │ ÉTAPE EXÉCUTION SOP  │    │         │ FICHE INCIDENT│
 │ wf/diag      │  │     SOPEXETP         │    │         │     INC       │
 └──────────────┘  └──────────────────────┘    │         │ Capitalisation│
                                                 │         └───────┬───────┘
 ┌──────────────────┐     ┌─────────────────┐   │1,1             │0,n
 │ ACTION EXTERNE   │1,1  │  AUDIT (append  │◄──┤                ▼
 │ DÉCLARÉE ACT_EXT ├─────┤  only) AUD      │   │         ┌───────────────┐
 │(WF/Diag/SOP, éta│)│     └─────────────────┘   │         │ MÉTRIQUE BUS. │
 └──────────────────┘                           └─────────┤    METBUS     │
                                                          │ MTTR, #P1, %  │
 ┌──────────────┐     ┌─────────────────────────┐        │ N2 résolution │
 │ POLITIQUE DE │     │ PARAM. DIAGNOSTIC,      │        └───────────────┘
 │ RÉTENTION RET├─────┤ SeuilsHypothèses, ...   │
 └──────┬───────┘     │  DIAGSETT (ENV scope)   │
        │0,n          └─────────────────────────┘
        ▼
 ┌──────────────────┐     ┌─────────────────────────┐
 │ APPLICATION      │     │  CONFIG AZURE AD /      │
 │ POLITIQUE RET.   │     │  AZUREADCONF            │
 └──────────────────┘     └─────────────────────────┘
```

### Cardinalités SOP & Capitalisation & Audit

| Association | Entité A | Card. A | Entité B | Card. B | Commentaire |
|---|---|---|---|---|---|
| `ENV—SOP` | ENV | `(1,1)` | SOP | `(0,n)` | Un SOP cible 1 environnement (ou global si ENV = catalogue générique via ENVs) |
| `SOP—SOPEX` | SOP | `(1,1)` | EXÉCUTION SOP | `(0,n)` | SOP exécuté 0..n fois |
| `SOP—SOPASSOC` | SOP | `(1,1)` | ASSOCIATION SOP | `(0,n)` | 0+ associations SOP → entités |
| `SOPASSOC—CMP` | COMPOSANT | `(0,1)` | ASSOC SOP | `(0,n)` | Association via composant |
| `SOPASSOC—SIGDIAG` | SIGDIAG | `(0,1)` | ASSOC SOP | `(0,n)` | Association via signature anomalie → suggestion SOP |
| `SOPASSOC—SESDIAG` | SESDIAG | `(0,1)` | ASSOC SOP | `(0,n)` | SOP suggéré pour diagnostic |
| `DOC—SOP` | DOCUMENT | `(0,1)` | SOP | `(0,n)` | SOP généré depuis un doc (Q&R) |
| `EXE—SOP` | EXÉCUTION WKF | `(0,1)` | SOP | `(0,n)` | SOP généré depuis workflow réussi |
| `SESDIAG—SOP` | SESSION DIAG | `(0,1)` | SOP | `(0,n)` | SOP généré depuis incident clos |
| `SOPEX—SOPEXETP` | SOPEX | `(1,1)` | ÉTAPE SOPEX | `(1,n)` | 1+ étapes |
| `SOPEXETP—CMP` | CMP | `(0,1)` | ÉTAPE SOPEX | `(0,n)` | Étape SOP vise 0..1 composant |
| `SOPEX—ENV` | ENV | `(1,1)` | SOPEX | `(0,n)` | Exécution rattachée à 1 env |
| `SOPEX—ALT` | ALERTE | `(0,1)` | SOPEX | `(0,n)` | Exécution SOP déclenchée par 1 alerte |
| `SOPEX—SESDIAG` | SESDIAG | `(0,1)` | SOPEX | `(0,n)` | SOP exécuté dans le cadre d'un diagnostic |
| `INC—SESDIAG` | SESDIAG | `(0,1)` | FICHE INCIDENT | `(0,1)` | 1 diagnostic → 0..1 incident capitalisé |
| `INC—EXE` | EXÉCUTION WKF | `(0,1)` | FICHE INCIDENT | `(0,n)` | Incident utilise 0..n exécutions de workflow |
| `INC—ENV` | ENV | `(1,1)` | FICHE INCIDENT | `(0,n)` | Incident sur 1 env |
| `INC—SOP` | SOP | `(0,n)` | FICHE INCIDENT | `(0,n)` | Incident a utilisé 0..n SOPs |
| `INC—SOP (genere)` | FICHE INCIDENT | `(0,1)` | SOP | `(0,n)` | Incident a généré 0..1 nouveau SOP |
| `METBUS—ENV` | ENV | `(0,1)` | MÉTRIQUE BUS | `(0,n)` | Métrique peut cibler 1 env NULL = global |
| `AUD—GLOBAL` | — | — | AUDIT | `(0,n)` | Toute entité peut générer 0..n entrées d'audit (champs ID entité libre) |
| `DIAGSETT—ENV` | ENV | `(1,1)` | PARAM DIAGNOSTIC | `(0,1)` | Singleton par environnement |
| `RET—DIAGSETT/GLOBAL` | POL RETENTION | `(1,1)` | APPLICATION POL | `(1,n)` | Politiques appliquées à chaque type données |

---

## MCD : Associations avec attributs (Association Class)

Certaines associations portent des attributs propres (ce sont des classes d'association) :

| Association | Classe d'association | Attributs |
|---|---|---|
| `CMP × CMP` (dépendance) | DEPENDANCE DEP | `NatureDependance`, `Notes` |
| `USR × ROL × ENV` | HABILITATION HAB | `DateDebutValidite`, `DateFinValidite` |
| `SOP × [CMP/SIGDIAG/SESDIAG]` | SOPASSOC | `NatureAssociation(enum)` |
| `ENV × USR` (droit additionnel) | GRANTSPECIFIQUE | `DroitSpecial`, `Jusqua`, `Motif` |
| `REGCORR × SIG/Mesure` | CONDCORR | `EstNegation`, `DescriptionCondition` |

---

## Règles d'unicité & contraintes métier supplémentaires (non cardinalités)

1. **ENV CodeEnvironnement = UNIQUE global.**
2. **CMP : 1 seul CenterNode par ENV** — Contrainte : `UNIQUE(EnvironmentId) WHERE Role = 'CenterNode'` (RG-0105).
3. **CMP : 1 seul StandbyCenterNode par ENV** — Contrainte filtre de même type.
4. **VERROU ENV : 1 seul verrou actif (non expiré) à la fois.** Contrainte = gérée par code (mutex applicatif) + index conditionnel `UNIQUE(EnvironmentId) WHERE ExpiresAt > GETUTCDATE()`.
5. **SOP Code + Version = UNIQUE.**
6. **WKF Nom + Version + EnvironmentId = UNIQUE.**
7. **HAB USR × ROL × ENV = UNIQUE.** Même utilisateur ne peut pas avoir 2 fois même rôle dans même environnement.
8. **SIGDIAG Code = UNIQUE global** (identifiant stable pour référencement).
9. **AUD = Append-Only strict.** Aucun UPDATE, aucun DELETE sur table AUDIT. Seul INSERT autorisé.
10. **METBUS Période × CodeMesure × EnvironmentId = UNIQUE** (ou NULL env) — pas deux mesures pour même période/code/env.
11. **APR : 1 seule APR active par AlerteId** (évite doublons).
12. **CREDPROF : 1 seul credential principal par SRV/ENV pair.**
