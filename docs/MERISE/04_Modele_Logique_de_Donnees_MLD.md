# Analyse MERISE — N4 Sentinel
## 04. Modèle Logique de Données (MLD)

**Format** : Schéma relationnel — Tables (PK soulignée, FK en italique, types alignés DD)
**Conventions** :
- PK = Primary Key (clé primaire)
- FK = Foreign Key (clé étrangère — `→ Table`)
- UK = Unique Key (unicité)
- CK = Check Constraint (contrainte domaine)
- `*not null` = obligatoire
- Types logiques : `CHAR(n)`, `VARCHAR(n)`, `NVARCHAR(n)`, `INT`, `BIGINT`, `NUMERIC(p,s)`, `BOOLEAN`, `DATE`, `DATETIME2(3)`, `GUID` (ou `UNIQUEIDENTIFIER`), `VARBINARY(max)`

---

## Groupe 1 — Référentiel

### TABLE ENVIRONNEMENT
```
ENVIRONNEMENT (
  IdEnvironnement      GUID [PK],
  CodeEnvironnement    VARCHAR(20) *not null [UK1],
  NomEnvironnement     VARCHAR(100) *not null,
  TypeEnvironnement    VARCHAR(20) *not null
                       CK: IN ('Production','UAT','Test','Developpement','Formation'),
  Description          VARCHAR(500),
  AdminResponsable     VARCHAR(100),
  UrlBaseN4            VARCHAR(255),
  CheminDossierPartage VARCHAR(255),
  FuseauHoraire        VARCHAR(50),
  StatutCycleVie       VARCHAR(20) *not null
                       CK: IN ('Brouillon','AValider','Valide','Actif','Desactive'),
  CreeLe               DATETIME2(3) *not null,
  ModifieLe            DATETIME2(3)
)
```

### TABLE SERVEUR
```
SERVEUR (
  IdServeur            GUID [PK],
  *IdEnvironnement     GUID [FK → ENVIRONNEMENT.IdEnvironnement] *not null,
  NomHote              VARCHAR(100) *not null,
  AdresseIp            VARCHAR(50),
  SystemeExploitation  VARCHAR(100),
  RoleApplicatif       VARCHAR(100),
  CoeursCPUAlloues     INT,
  RAMGo                INT,
  DisqueGo             BIGINT,
  EstActif             BOOLEAN *not null DEFAULT TRUE
)
UK2 : (IdEnvironnement, NomHote)
```

### TABLE COMPOSANT_N4
```
COMPOSANT_N4 (
  IdComposant          GUID [PK],
  *IdEnvironnement     GUID [FK → ENVIRONNEMENT.IdEnvironnement] *not null,
  *IdServeur           GUID [FK → SERVEUR.IdServeur],
  NomLogique           VARCHAR(100) *not null,
  RoleApplicatif       VARCHAR(30) *not null
                       CK: IN ('ClusterNode','CenterNode','StandbyCenterNode','BridgeDaemon',
                               'Xps','Ecn4','Ecn4Web','BaseDeDonnees','ActiveMq',
                               'DossierPartage','InterfaceEdi','JobConditionnel','Autre'),
  NomServiceWindows    VARCHAR(200),
  NomProcessus         VARCHAR(100),
  EndpointSante        VARCHAR(255),
  PortApplicatif       INT,
  Criticite            VARCHAR(15) *not null
                       CK: IN ('Faible','Moyenne','Elevee','Critique'),
  ModeControle         VARCHAR(20) *not null
                       CK: IN ('Pilotable','SuperviseSeulement','NonSupervise'),
  StatutCycleVie       VARCHAR(20) *not null,
  ModeMaintenance      BOOLEAN *not null DEFAULT FALSE,
  NoteMaintenance      VARCHAR(500),
  MaintenanceJusqua    DATETIME2(3),
  MotifMaintenance     VARCHAR(500),
  MaintenancePar       VARCHAR(100),
  OrdreDemarrage       INT,
  Description          VARCHAR(500),
  ResponsableTechnique VARCHAR(100)
)
UK_CMP_ENV_ROLE_CENTER : UNIQUE(IdEnvironnement) WHERE RoleApplicatif IN ('CenterNode','StandbyCenterNode')
    -- Constraint logique : 1 seul de chaque par env (gérée à la fois par contrainte et code)
```

### TABLE PROFIL_PREUVE (1:1 avec COMPOSANT_N4)
```
PROFIL_PREUVE (
  *IdComposant         GUID [PK, FK → COMPOSANT_N4.IdComposant],
  CheminJournal        VARCHAR(300),
  MotifsPretsJSON      NVARCHAR(4000),
  MotifsErreurJSON     NVARCHAR(4000),
  MotifsIgnoreJSON     NVARCHAR(4000),
  MotifsRoleActifJSON  NVARCHAR(4000),
  MotifsSyncJSON       NVARCHAR(4000),
  SeuilRetardSyncMin   INT DEFAULT 15,
  TimeoutServiceSec    INT DEFAULT 300,
  TimeoutLogPretSec    INT DEFAULT 1800,
  TimeoutStopSec       INT DEFAULT 600,
  IntervalleSondageSec INT DEFAULT 10,
  ProgressionChaqueSec INT DEFAULT 60,
  ObservationApresPretSec INT DEFAULT 0
)
```

### TABLE PROFIL_DOSSIER_PARTAGE (1:0..1 avec COMPOSANT_N4 — rôle DossierPartage)
```
PROFIL_DOSSIER_PARTAGE (
  *IdComposant         GUID [PK, FK → COMPOSANT_N4.IdComposant],
  CheminRacine         VARCHAR(300) *not null,
  Categorie            VARCHAR(30) *not null
                       CK: IN ('ConfigurationN4','ActiveMqKahaDb','EchangeEdi',
                               'Archives','DossiersErreur','Autre'),
  SousDossierAttente   VARCHAR(200),
  SousDossierConsomme  VARCHAR(200),
  SousDossierBloque    VARCHAR(200),
  SousDossierErreur    VARCHAR(200),
  AncienneteMaxAttenteHeure INT DEFAULT 4,
  LatenceEcritureMaxMs INT DEFAULT 500,
  CroissanceMaxGoParHeure NUMERIC(12,2),
  MotifNommageFichierEdi VARCHAR(500),
  DureeMaxSansIntegrationHeure INT DEFAULT 2,
  DerniereSauvegardeLe DATETIME2(3),
  DerniereSauvegardePar VARCHAR(100)
)
```

### TABLE DEPENDANCE_COMPOSANT (association CMP→CMP)
```
DEPENDANCE_COMPOSANT (
  IdDependance         GUID [PK],
  *IdComposantSource   GUID [FK → COMPOSANT_N4.IdComposant] *not null,
  *IdComposantDependu  GUID [FK → COMPOSANT_N4.IdComposant] *not null,
  NatureDependance     VARCHAR(30) *not null
                       CK: IN ('RequisAuDemarrage','RequisEnFonctionnement','Informative'),
  Notes                VARCHAR(500)
)
UK_DEP : UNIQUE(IdComposantSource, IdComposantDependu, NatureDependance)
CK : IdComposantSource <> IdComposantDependu  -- pas de dépendance sur soi-même
```

### TABLE SERVICE_ASSOCIE (1:n COMPOSANT_N4)
```
SERVICE_ASSOCIE (
  IdServiceAssocie     GUID [PK],
  *IdComposant         GUID [FK → COMPOSANT_N4.IdComposant] *not null,
  NomServiceWindows    VARCHAR(200) *not null,
  EstRequisPourOperationnel BOOLEAN *not null DEFAULT TRUE
)
```

---

## Groupe 2 — Sécurité

### TABLE UTILISATEUR
```
UTILISATEUR (
  IdUtilisateur        GUID [PK],
  LoginWindows         VARCHAR(150) *not null [UK],
  Nom                  VARCHAR(100),
  Prenom               VARCHAR(100),
  AdresseEmail         VARCHAR(200),
  MFAEstActif          BOOLEAN *not null DEFAULT FALSE,
  EstActif             BOOLEAN *not null DEFAULT TRUE,
  DerniereConnexionLe  DATETIME2(3)
)
```

### TABLE ROLE_APPLICATIF
```
ROLE_APPLICATIF (
  IdRole               GUID [PK],
  CodeRole             VARCHAR(50) *not null [UK]
                       CK: IN ('Lecteur','OperateurN4','AdminN4','AdminInfra',
                               'Validateur','AdminSolution','Auditeur','TOSManager'),
  NomRole              VARCHAR(100) *not null,
  Description          VARCHAR(500)
)
```

### TABLE HABILITATION (USR × ROL × ENV)
```
HABILITATION (
  IdHabilitation       GUID [PK],
  *IdUtilisateur       GUID [FK → UTILISATEUR.IdUtilisateur] *not null,
  *IdRole              GUID [FK → ROLE_APPLICATIF.IdRole] *not null,
  *IdEnvironnement     GUID [FK → ENVIRONNEMENT.IdEnvironnement], -- NULL = droit global
  ValideDepuisLe       DATETIME2(3) *not null DEFAULT SYSUTCDATETIME(),
  ValideJusquaLe       DATETIME2(3)
)
UK_HAB : UNIQUE(IdUtilisateur, IdRole, IdEnvironnement)
CK : IdEnvironnement IS NOT NULL OR CodeRole IN ('AdminSolution','Auditeur')  -- globalité limitée
```

### TABLE PROFIL_CREDENTIEL
```
PROFIL_CREDENTIEL (
  IdProfilCredential   GUID [PK],
  *IdEnvironnement     GUID [FK → ENVIRONNEMENT.IdEnvironnement] *not null,
  *IdServeur           GUID [FK → SERVEUR.IdServeur],
  NomUsage             VARCHAR(200) *not null,
  TypeAuthentification VARCHAR(30) *not null
                       CK: IN ('CompteDomaine','CompteLocal','SqlLogin','Certificat','SSHKey'),
  IdentifiantStockeProtege VARCHAR(500),
  MotDePasseChiffre    VARBINARY(max),
  EmpreinteCertificat  VARCHAR(200),
  ValideDepuisLe       DATETIME2(3),
  ExpireLe             DATETIME2(3),
  UtilisationCheminDescription VARCHAR(500),
  EstActif             BOOLEAN *not null DEFAULT TRUE
)
```

### TABLE DROIT_SPECIFIQUE (ENV × USR) — exemptions temporaires
```
DROIT_SPECIFIQUE (
  IdDroitSpecifique    GUID [PK],
  *IdEnvironnement     GUID [FK → ENVIRONNEMENT.IdEnvironnement] *not null,
  *IdUtilisateur       GUID [FK → UTILISATEUR.IdUtilisateur] *not null,
  DroitSpecial         VARCHAR(50) *not null,
  DateDebut            DATETIME2(3) *not null,
  DateFin              DATETIME2(3) *not null,
  Motif                VARCHAR(1000),
  AccordPar            VARCHAR(150) *not null
)
```

---

## Groupe 3 — Workflow Modèle

### TABLE WORKFLOW_MODELE
```
WORKFLOW_MODELE (
  IdWorkflow           GUID [PK],
  *IdEnvironnement     GUID [FK → ENVIRONNEMENT.IdEnvironnement] *not null,
  Nom                  VARCHAR(200) *not null,
  TypeWorkflow         VARCHAR(30) *not null
                       CK: IN ('ArretComplet','DemarrageComplet','RedemarrageCluster',
                               'BasculeCenter','ArretPartiel','DemarragePartiel',
                               'MaintenanceComposant','ReconstitutionDossierPartage'),
  Version              INT *not null DEFAULT 1,
  StatutCycleVie       VARCHAR(20) *not null,
  NiveauAutomatisation VARCHAR(20) *not null DEFAULT 'SemiAutomatique'
                       CK: IN ('SemiAutomatique','Automatique'),
  RequiertDoubleApprobation BOOLEAN *not null DEFAULT FALSE,
  Description          NVARCHAR(1000)
)
UK_WKF : UNIQUE(IdEnvironnement, Nom, Version)
```

### TABLE ETAPE_WKF_MODELE (1:n WORKFLOW_MODELE)
```
ETAPE_WKF_MODELE (
  IdEtapeModele        GUID [PK],
  *IdWorkflow          GUID [FK → WORKFLOW_MODELE.IdWorkflow] *not null,
  Ordre                INT *not null,
  NomEtape             VARCHAR(200) *not null,
  Action               VARCHAR(30) *not null
                       CK: IN ('Demarrer','Arreter','Redemarrer','Attente','VerrouPreRequis',
                               'ControlePreuve','InterventionManuelle','ArretForce'),
  *IdComposantCible    GUID [FK → COMPOSANT_N4.IdComposant],
  TimeoutSecondes      INT DEFAULT 1800,
  DureeAttendueSecondes INT DEFAULT 120,
  SeuilAvertissementSecondes INT DEFAULT 240,
  NombreMaxEssais      INT DEFAULT 0,
  RetryAutomatique     BOOLEAN *not null DEFAULT FALSE,
  DelaiRetrySecondes   INT DEFAULT 30,
  Contournable         BOOLEAN *not null DEFAULT TRUE,
  ConfirmationOperateur BOOLEAN *not null DEFAULT TRUE,
  Parallelisable       BOOLEAN *not null DEFAULT FALSE,
  PolitiqueEchec       VARCHAR(15) *not null DEFAULT 'Bloquer'
                       CK: IN ('Bloquer','ContinuerAlerte','ContinuerSilence'),
  InstructionManuelle  NVARCHAR(2000)
)
UK_ETP : UNIQUE(IdWorkflow, Ordre)
```

### TABLE CONTROLE_PREALABLE_MODELE (PRÉ-CHECK)
```
CONTROLE_PREALABLE_MODELE (
  IdControleModele     GUID [PK],
  *IdWorkflow          GUID [FK → WORKFLOW_MODELE.IdWorkflow], -- NULL = contrôle global environnement
  CodeControle         VARCHAR(50) *not null [UK],
  Libelle              VARCHAR(200) *not null,
  SeveriteSiEchec      VARCHAR(15) *not null
                       CK: IN ('Bloquant','Avertissement','NonApplicable'),
  ScriptVerificationDefinition NVARCHAR(4000),
  *IdComposantCible    GUID [FK → COMPOSANT_N4.IdComposant]
)
```

### TABLE REGLE_MATRICE_APPROBATION
```
REGLE_MATRICE_APPROBATION (
  IdRegleApprobation   GUID [PK],
  *IdEnvironnement     GUID [FK → ENVIRONNEMENT.IdEnvironnement],  -- NULL = défaut global
  AppliqueARoleComposant VARCHAR(30),   -- NULL = tous
  AppliqueACriticite   VARCHAR(15),      -- NULL = toutes
  AppliqueATypeActionWorkflow VARCHAR(30), -- NULL = toutes
  NombreApprobationsRequises INT *not null DEFAULT 1
                                     CK: IN (1,2),
  RoleApplicatifRequisPourApprouver VARCHAR(50) *not null,
  OperateurDoitEtreDifferent BOOLEAN *not null DEFAULT TRUE,
  DelaiValiditeApprobationMinutes INT DEFAULT 120,
  EstActif             BOOLEAN *not null DEFAULT TRUE
)
```

---

## Groupe 4 — Workflow Exécution

### TABLE EXECUTION_WKF
```
EXECUTION_WKF (
  IdExecution          GUID [PK],
  *IdWorkflow          GUID [FK → WORKFLOW_MODELE.IdWorkflow] *not null,
  VersionWorkflowLancee INT *not null,
  NomWorkflowFige      VARCHAR(200) *not null,
  *IdEnvironnement     GUID [FK → ENVIRONNEMENT.IdEnvironnement] *not null,
  CodeEnvironnementFige VARCHAR(20) *not null,
  Statut               VARCHAR(30) *not null
                       CK: IN ('EnPreparation','EnAttenteApprobation','EnCours','EnPause',
                               'AnnulationDemandee','ReconciliationRequise','TermineSucces',
                               'TermineAvecAvertissements','Echec','Annule'),
  EstSimulation        BOOLEAN *not null DEFAULT FALSE,
  NiveauAutomatisation VARCHAR(20) *not null,
  FallbackSemiAutoForce BOOLEAN *not null DEFAULT FALSE,
  DemandePar           VARCHAR(150),
  IdentiteOperateurLogin VARCHAR(150),
  EtiquetteIdentiteOperateur VARCHAR(255),
  MotifOperation       NVARCHAR(1000),
  ReferenceTicket      VARCHAR(50),
  ImpactPrevu          NVARCHAR(1000),
  FenetreDebut         DATETIME2(3),
  FenetreFin           DATETIME2(3),
  DureeEstimeeTotale   INT,
  Approbateur1Par      VARCHAR(150),
  Approbateur1Le       DATETIME2(3),
  Approbateur2Par      VARCHAR(150),
  Approbateur2Le       DATETIME2(3),
  ChoixContinuiteRequis BOOLEAN *not null DEFAULT FALSE,
  ChoixContinuiteValeur VARCHAR(15)
                         CK: IN (NULL,'ResterActif','Basculer'),
  ChoixContinuiteFaitPar VARCHAR(150),
  ChoixContinuiteFaitLe DATETIME2(3),
  DebutEffectifLe      DATETIME2(3),
  FinEffectifLe        DATETIME2(3),
  DernierHeartbeatMoteurLe DATETIME2(3),
  PauseDemandeePar     VARCHAR(150),
  AnnulationDemandeePar VARCHAR(150),
  ResumeResultat       NVARCHAR(2000),
  RapportPostAnnulation NVARCHAR(4000),
  RequiertInterventionManuelle BOOLEAN *not null DEFAULT FALSE,
  IdCorrelation        VARCHAR(20),
  RapportPreflightJSON NVARCHAR(max),
  PreflightEffectueLe  DATETIME2(3),
  PreflightContientBloquant BOOLEAN
)
INDEX (IdEnvironnement, Statut)
INDEX (DebutEffectifLe)
```

### TABLE ETAPE_EXECUTION (1:n EXECUTION_WKF)
```
ETAPE_EXECUTION (
  IdEtapeExecution     GUID [PK],
  *IdExecution         GUID [FK → EXECUTION_WKF.IdExecution] *not null,
  OrdreEtape           INT *not null,
  NomEtape             VARCHAR(200) *not null,
  Action               VARCHAR(30) *not null,
  *IdComposantCible    GUID [FK → COMPOSANT_N4.IdComposant],
  NomComposantFige     VARCHAR(200),
  NomHoteFige          VARCHAR(150),
  Statut               VARCHAR(20) *not null
                       CK: IN ('AVenir','EnAttente','EnCours','Verification','Reussi',
                               'Avertissement','Bloque','Echec','Ignore','Annule'),
  DebutEffectifLe      DATETIME2(3),
  FinEffectifLe        DATETIME2(3),
  MessageAvancement    NVARCHAR(500),
  PreuveTechnique      NVARCHAR(4000),
  CommandeExecuteeExpurgee NVARCHAR(4000),
  TexteErreur          NVARCHAR(4000),
  TypeErreurTaxinomie  VARCHAR(30)
                       CK: IN (NULL,'CommandeRefusee','TimeoutAttente','ComportementConnuStopPending',
                               'ComposantNonConfigure','PrerequisNonSatisfait','Inconnu'),
  NombreEssaisEffectues INT *not null DEFAULT 0,
  MaxEssaisFiges       INT,
  RetryAutoFige        BOOLEAN,
  DelaiRetrySecFiges   INT,
  RetryAutoriseApresLe DATETIME2(3),
  ContournementPar     VARCHAR(150),
  ContournementMotif   NVARCHAR(1000),
  ContournementCoApprouvePar VARCHAR(150),
  ContournementCoApprouveLe DATETIME2(3),
  ArretForcePar        VARCHAR(150),
  ArretForceMotif      NVARCHAR(1000),
  ConfirmationOperateurPar VARCHAR(150),
  ConfirmationOperateurLe DATETIME2(3),
  NoteOperateur        NVARCHAR(2000),
  PreuveJointeObligatoire BOOLEAN *not null DEFAULT FALSE,
  NomFichierPreuveJoint VARCHAR(255),
  TypeMimeFichierPreuve VARCHAR(100),
  ContenuBinaireFichierPreuve VARBINARY(max),
  *IdSessionDiagnosticRattachee GUID [FK → SESSION_DIAGNOSTIC.IdSessionDiagnostic],
  TimeoutSecondesFige  INT,
  DureeAttendueSecondesFige INT,
  SeuilAvertissementFige INT,
  EstContournableFige  BOOLEAN,
  ConfirmationRequiseFige BOOLEAN,
  ParallelisableFige   BOOLEAN,
  PolitiqueEchecFige   VARCHAR(15),
  InstructionManuelleFige NVARCHAR(2000),
  EstActionDestructrice BOOLEAN *not null DEFAULT FALSE
)
UK_ETPEXE : UNIQUE(IdExecution, OrdreEtape)
```

### TABLE VERROU_ENVIRONNEMENT (MUTEX)
```
VERROU_ENVIRONNEMENT (
  IdVerrou             GUID [PK],
  *IdEnvironnement     GUID [FK → ENVIRONNEMENT.IdEnvironnement] *not null,
  *IdExecution         GUID [FK → EXECUTION_WKF.IdExecution] *not null,
  DetenuPar            VARCHAR(150) *not null,
  MotifAcquisition     NVARCHAR(500) *not null,
  AcquisLe             DATETIME2(3) *not null,
  ExpireLe             DATETIME2(3) *not null
)
UK_VERROU_ACTIF : UNIQUE(IdEnvironnement) WHERE ExpireLe > SYSUTCDATETIME()
```

---

## Groupe 5 — Supervision & Alertes Préventives

### TABLE SIGNAL_COMPOSANT (historique mesures)
```
SIGNAL_COMPOSANT (
  IdSignal             GUID [PK],
  *IdComposant         GUID [FK → COMPOSANT_N4.IdComposant] *not null,
  MesureLe             DATETIME2(3) *not null,
  NomMesure            VARCHAR(100) *not null,
  ValeurNumerique      NUMERIC(18,4),
  ValeurTexte          VARCHAR(500),
  SeuilAlerteAtteint   BOOLEAN *not null DEFAULT FALSE,
  CollecteParConnecteur VARCHAR(200),
  ErreurCollecte       NVARCHAR(2000)
)
CLUSTERED INDEX (IdComposant, MesureLe DESC)
```

### TABLE ALERTE_SUPERVISION
```
ALERTE_SUPERVISION (
  IdAlerte             GUID [PK],
  *IdEnvironnement     GUID [FK → ENVIRONNEMENT.IdEnvironnement] *not null,
  *IdComposant         GUID [FK → COMPOSANT_N4.IdComposant],
  CodeAlerte           VARCHAR(50) *not null,
  Gravite              VARCHAR(15) *not null
                       CK: IN ('Info','Avertissement','Mineure','Majeure','Critique'),
  Titre                VARCHAR(200) *not null,
  DescriptionDetaillee NVARCHAR(4000),
  Nature               VARCHAR(30) *not null
                       CK: IN ('SupervisionTempsReel','PredictiveTendance','PreventiveAPR'),
  NiveauConfiancePredit INT CK: NiveauConfiancePredit BETWEEN 0 AND 100,
  ActionSuggeree       NVARCHAR(2000),
  RisqueP1EviteSiFaitePourcent INT CK: RisqueP1EviteSiFaitePourcent BETWEEN 0 AND 100,
  DureeActionSuggereeMin INT,
  CreeLe               DATETIME2(3) *not null,
  AcquitteePar         VARCHAR(150),
  AcquitteeLe          DATETIME2(3),
  TraiteePar           VARCHAR(150),
  TraiteeLe            DATETIME2(3),
  Statut               VARCHAR(20) *not null DEFAULT 'Nouvelle'
                       CK: IN ('Nouvelle','Acquittee','Traitee','FausseAlerte','Ignoree'),
  *IdSessionDiagnosticOuverte GUID [FK → SESSION_DIAGNOSTIC.IdSessionDiagnostic]
)
INDEX (IdEnvironnement, Gravite, Statut)
```

### TABLE ACTION_PREVENTIVE_RECOMMANDÉE (APR — AMÉLIORATION)
```
ACTION_PREVENTIVE_RECOMMANDÉE (
  IdActionPreventive   GUID [PK],
  *IdEnvironnement     GUID [FK → ENVIRONNEMENT.IdEnvironnement] *not null,
  CodeReference        VARCHAR(50) *not null,
  Libelle              VARCHAR(300) *not null,
  DetailJustificatif   NVARCHAR(4000),
  ValeurSeuilDeclenchementMesure VARCHAR(50),
  ValeurActuelleMesuree VARCHAR(50),
  RisqueP1EviteSiFaitePourcent INT CK: RisqueP1EviteSiFaitePourcent BETWEEN 0 AND 100,
  DureeOperationEstimeeMin INT,
  ImpactMetierSiFaite  NVARCHAR(500),
  FenetreSuggereeDebut DATETIME2(3),
  FenetreSuggereeFin   DATETIME2(3),
  *IdWorkflowSuggereSiApplicable GUID [FK → WORKFLOW_MODELE.IdWorkflow],
  *IdAlerteSourceSiPredictive   GUID [FK → ALERTE_SUPERVISION.IdAlerte],
  GenereLe             DATETIME2(3) *not null,
  Statut               VARCHAR(20) *not null DEFAULT 'Nouvelle'
                       CK: IN ('Nouvelle','Programme','Realisee','Ignoree','Expiree'),
  ProgrammeePar        VARCHAR(150),
  ProgrammeeLe         DATETIME2(3),
  RealiseePar          VARCHAR(150),
  RealiseeLe           DATETIME2(3),
  *IdExecutionWorkflowRealisation GUID [FK → EXECUTION_WKF.IdExecution],
  IgnoreePar           VARCHAR(150),
  MotifIgnorance       NVARCHAR(2000)
)
```

---

## Groupe 6 — Diagnostic Incident

### TABLE SESSION_DIAGNOSTIC
```
SESSION_DIAGNOSTIC (
  IdSessionDiagnostic  GUID [PK],
  *IdEnvironnement     GUID [FK → ENVIRONNEMENT.IdEnvironnement] *not null,
  CodeEnvironnementFige VARCHAR(20) *not null,
  Titre                VARCHAR(200) *not null,
  MotifObjectif        NVARCHAR(2000),
  ReferenceTicket      VARCHAR(50),
  DemandePar           VARCHAR(150),
  FenetreAnalyseDebut  DATETIME2(3),
  FenetreAnalyseFin    DATETIME2(3),
  VerdictFinal         VARCHAR(30)
                       CK: IN (NULL,'CauseConfirmee','CauseCaracterisee','PlusieursCausesPossibles',
                               'PisteSerieuse','AnomaliesSansCause','RienDeConcluant',
                               'InformationsInsuffisantes'),
  ExplicationVerdict   NVARCHAR(4000),
  AnalyseEffectueeLe   DATETIME2(3),
  *IdAlerteSource      GUID [FK → ALERTE_SUPERVISION.IdAlerte],
  EstReferencePeriodeSaine BOOLEAN *not null DEFAULT FALSE,
  *IdSessionReferencePeriodeSaine GUID [FK → SESSION_DIAGNOSTIC.IdSessionDiagnostic],
  ModeComparaisonActif VARCHAR(30)
                       CK: IN (NULL,'PeriodeSaine','ExecutionReussie','ValeursHabituellesComposant','NoeudPair'),
  *IdExecutionReferenceReussie GUID [FK → EXECUTION_WKF.IdExecution],
  *IdComposantReference GUID [FK → COMPOSANT_N4.IdComposant],
  PhaseCourante8Phases VARCHAR(30) *not null
                       CK: IN ('DetectionEtEnregistrement','QualificationEtCollecte','Securisation',
                               'DiagnosticEtCorrelation','ChoixDuPlanDAction','ValidationEtExecution',
                               'RemiseEnServiceEtVerification','ClotureEtCapitalisation'),
  EscaladeVersQui      VARCHAR(150),
  EscaladeFaiteLe      DATETIME2(3),
  EscaladeDemandeePar  VARCHAR(150)
)
INDEX (IdEnvironnement, PhaseCourante8Phases)
INDEX (ReferenceTicket)
```

### TABLE TRANSITION_PHASE_DIAG (NON LINÉAIRE — RG-0501)
```
TRANSITION_PHASE_DIAG (
  IdTransitionPhase    GUID [PK],
  *IdSessionDiagnostic GUID [FK → SESSION_DIAGNOSTIC.IdSessionDiagnostic] *not null,
  Phase                VARCHAR(30) *not null,
  EntreePar            VARCHAR(150) *not null,
  EntreeLe             DATETIME2(3) *not null,
  Justification        NVARCHAR(2000)
)
```

### TABLE SIGNATURE_ANOMALIE (cataloguée éditeur ou site)
```
SIGNATURE_ANOMALIE (
  IdSignature          GUID [PK],
  CodeSignature        VARCHAR(50) *not null [UK],
  Nom                  VARCHAR(200) *not null,
  MotifRegEx           VARCHAR(1000) *not null,
  DomaineTechnique     VARCHAR(30) *not null,
  Severite             VARCHAR(15) *not null
                       CK: IN ('Info','Avertissement','Erreur','Critique'),
  Origine              VARCHAR(10) *not null
                       CK: IN ('Editeur','Site'),
  SignificationMetier  NVARCHAR(2000),
  ActionCorrectriceConseillee NVARCHAR(2000),
  ReferenceDocumentaire VARCHAR(500),
  ContrePreuvePossible NVARCHAR(2000),
  Version              INT *not null DEFAULT 1,
  PoidsConfiance0a100  INT *not null DEFAULT 50 CK: PoidsConfiance0a100 BETWEEN 1 AND 100,
  AppliqueARoleComposant VARCHAR(30),  -- NULL = tous
  EstActivee           BOOLEAN *not null DEFAULT TRUE,
  StatutValidation     VARCHAR(20) *not null DEFAULT 'Valide'
)
```

### TABLE REGLE_CORRELATION
```
REGLE_CORRELATION (
  IdRegleCorrelation   GUID [PK],
  CodeRegle            VARCHAR(50) *not null [UK],
  Nom                  VARCHAR(200) *not null,
  DomaineTechnique     VARCHAR(30) *not null,
  Hypothese            NVARCHAR(2000) *not null,
  NiveauConfiance0a100 INT *not null CK: NiveauConfiance0a100 BETWEEN 0 AND 100,
  Recommandation       NVARCHAR(2000),
  FenetreTempsSecondes INT DEFAULT 120
)
```

### TABLE CONDITION_CORRELATION (1:n REGLE_CORRELATION)
```
CONDITION_CORRELATION (
  IdConditionCorrelation GUID [PK],
  *IdRegleCorrelation  GUID [FK → REGLE_CORRELATION.IdRegleCorrelation] *not null,
  IdSourceSignal       VARCHAR(100) *not null,   -- CodeSignature ou CodeMesure
  DescriptionCondition NVARCHAR(500),
  EstNegation          BOOLEAN *not null DEFAULT FALSE
)
```

### TABLE SOURCE_JOURNAL (log analysé dans session)
```
SOURCE_JOURNAL (
  IdSourceJournal      GUID [PK],
  *IdSessionDiagnostic GUID [FK → SESSION_DIAGNOSTIC.IdSessionDiagnostic] *not null,
  *IdComposantDesigne  GUID [FK → COMPOSANT_N4.IdComposant],
  NomComposantDesigneFige VARCHAR(200),
  RoleComposantDesigneFige VARCHAR(30),
  NomHoteFige          VARCHAR(150),
  ComposantAutoIdentifie BOOLEAN *not null DEFAULT FALSE,
  VersionDetecteeContenu VARCHAR(50),
  FormatDetecte        VARCHAR(50),
  FuseauHoraireDetecte VARCHAR(50),
  *IdComposantSuggereContenu GUID [FK → COMPOSANT_N4.IdComposant],
  NomComposantSuggere  VARCHAR(200),
  IndiceIdentification NVARCHAR(2000),
  OrigineMultiCandidats BOOLEAN *not null DEFAULT FALSE,
  IdCorrelationAssociation VARCHAR(20),
  OrigineCollecte      VARCHAR(20) *not null
                         CK: IN ('CollecteCiblee','ImportManuel'),
  CheminEffectifLecture VARCHAR(300),
  NomFichier           VARCHAR(200),
  TailleOctets         BIGINT,
  NombreLignes         BIGINT,
  HachageSHA256ContenuMasque VARCHAR(64),
  Horodatage1ereLigne  DATETIME2(3),
  HorodatageDerniereLigne DATETIME2(3),
  CompteNiveauINFO     BIGINT,
  CompteNiveauWARN     BIGINT,
  CompteNiveauERROR    BIGINT,
  EcartHorlogeSourceSecondes NUMERIC(12,3),
  NombreSecretsMasques INT,
  EstTronqueFinFichierSeulement BOOLEAN *not null DEFAULT FALSE,
  TexteErreurCollecte  NVARCHAR(2000),
  RaisonEchecTaxinomie VARCHAR(30)
                         CK: IN (NULL,'AccesRefuse','ConnecteurIndisponible','Timeout',
                                  'SourceAbsente','FormatNonReconnu','ControleNonConfigure'),
  CollecteSucces       BOOLEAN *not null
)
```

### TABLE CONSTAT_ANOMALIE (issue d'une source journal, reconnue par signature ?)
```
CONSTAT_ANOMALIE (
  IdConstat            GUID [PK],
  *IdSessionDiagnostic GUID [FK → SESSION_DIAGNOSTIC.IdSessionDiagnostic] *not null,
  *IdSourceJournal     GUID [FK → SOURCE_JOURNAL.IdSourceJournal] *not null,
  *IdSignatureReconnue GUID [FK → SIGNATURE_ANOMALIE.IdSignature],
  CodeSignatureFige    VARCHAR(50),
  DomaineTechnique     VARCHAR(30) *not null,
  Severite             VARCHAR(15) *not null,
  IntituleConstat      VARCHAR(200) *not null,
  LigneRepresentativeMasquee NVARCHAR(500),
  LignesContexteMasquees NVARCHAR(4000),
  NombreOccurrencesGroupees BIGINT *not null DEFAULT 1,
  PremiereObservationLe DATETIME2(3),
  DerniereObservationLe DATETIME2(3),
  PremierNumeroLigne   BIGINT,
  SignificationCopiee  NVARCHAR(2000),
  RemediationCopiee    NVARCHAR(2000),
  ReferenceDocCopiee   VARCHAR(500),
  ThreadExtrait        VARCHAR(200),
  ClasseLogger         VARCHAR(200),
  IdTransaction        VARCHAR(100),
  NiveauLog            VARCHAR(10),
  CodeErreurExtrait    VARCHAR(50)
)
INDEX (IdSessionDiagnostic, Severite, PremiereObservationLe)
```

### TABLE HYPOTHESE_DIAGNOSTIC
```
HYPOTHESE_DIAGNOSTIC (
  IdHypothese          GUID [PK],
  *IdSessionDiagnostic GUID [FK → SESSION_DIAGNOSTIC.IdSessionDiagnostic] *not null,
  DomaineTechnique     VARCHAR(30) *not null,
  EnonceHypothese      NVARCHAR(2000) *not null,
  NiveauConfiance0a100 INT *not null CK: NiveauConfiance0a100 BETWEEN 0 AND 100,
  ElementDePreuve      NVARCHAR(4000),
  ElementContrePreuve  NVARCHAR(2000),
  DerniereObservationPreuveLe DATETIME2(3),
  VersionReglePlusDeterminante VARCHAR(50),
  ActionConseillee     NVARCHAR(2000),
  RangAffichage        INT *not null
)
```

### TABLE PARAMETRAGE_DIAGNOSTIC (singleton ENV)
```
PARAMETRAGE_DIAGNOSTIC (
  IdParametrageDiagnostic GUID [PK],
  *IdEnvironnement     GUID [FK → ENVIRONNEMENT.IdEnvironnement] *not null [UK],
  SeuilConfianceHypothesePct INT DEFAULT 65,
  NombreMaxHypotheses  INT DEFAULT 5,
  FenetreAnalyseDefautHeures INT DEFAULT 4,
  AlarmeClusterDisconnectedSecondes INT DEFAULT 120,
  DelaiMaxIntegrationEdiHeures INT DEFAULT 2,
  SeuilAlerteEcartHorlogeSec NUMERIC(6,3) DEFAULT 1.000
)
```

---

## Groupe 7 — Base Documentaire

### TABLE DOCUMENT_REFERENCE
```
DOCUMENT_REFERENCE (
  IdDocument           GUID [PK],
  Titre                VARCHAR(300) *not null,
  Categorie            VARCHAR(50) *not null
                         CK: IN ('GuideEditeur','ProcedureInterne','PostMortem',
                                  'NoteDeVersion','SOP'),
  Version              VARCHAR(50),
  EditeurOuAuteur      VARCHAR(200),
  DatePublication      DATE,
  CheminFichierOriginal VARCHAR(400),
  StatutCycleVie       VARCHAR(20) *not null,
  EstIndexePourRAG     BOOLEAN *not null DEFAULT FALSE,
  TailleMo             NUMERIC(8,2),
  NombrePages          INT
)
```

### TABLE RETOUR_UTILISATEUR_DOC
```
RETOUR_UTILISATEUR_DOC (
  IdRetour             GUID [PK],
  *IdDocument          GUID [FK → DOCUMENT_REFERENCE.IdDocument] *not null,
  ReferenceSectionPage VARCHAR(200) *not null,
  QuestionPosee        NVARCHAR(500) *not null,
  ReponseRenvoyee      NVARCHAR(max) *not null,
  EstUtile             BOOLEAN,
  RetourPar            VARCHAR(150),
  RetourLe             DATETIME2(3),
  CommentaireLibre     NVARCHAR(2000)
)
```

---

## Groupe 8 — SOP

### TABLE SOP_MODELE
```
SOP_MODELE (
  IdSOP                GUID [PK],
  *IdEnvironnement     GUID [FK → ENVIRONNEMENT.IdEnvironnement],  -- NULL = générique multi-envs
  CodeStable           VARCHAR(30) *not null,
  Titre                VARCHAR(300) *not null,
  Version              INT *not null DEFAULT 1,
  StatutCycleVie       VARCHAR(20) *not null,
  DejaExecuteAuMoins1Fois BOOLEAN *not null DEFAULT FALSE,
  RequiertRoleSensible BOOLEAN *not null DEFAULT FALSE,
  Objectif             NVARCHAR(2000),
  Perimetre            NVARCHAR(2000),
  Prerequis            NVARCHAR(4000),
  RisquesEncourus      NVARCHAR(4000),
  ControlesPostOperation NVARCHAR(4000),
  ResultatAttendu      NVARCHAR(2000),
  PlanRetourArriere    NVARCHAR(4000),
  CheminEscalade       NVARCHAR(2000),
  AppliqueVersionN4    VARCHAR(50),
  *IdDocumentSourceSiGenereDepuisDoc GUID [FK → DOCUMENT_REFERENCE.IdDocument],
  *IdExecutionSourceSiGenereDepuisWKF GUID [FK → EXECUTION_WKF.IdExecution],
  *IdSessionDiagSourceSiGenereDepuisIncident GUID [FK → SESSION_DIAGNOSTIC.IdSessionDiagnostic]
)
UK_SOP : UNIQUE(CodeStable, Version)
```

### TABLE EXECUTION_SOP
```
EXECUTION_SOP (
  IdExecutionSOP       GUID [PK],
  *IdSOP               GUID [FK → SOP_MODELE.IdSOP] *not null,
  VersionSOPFigee      INT *not null,
  CodeSOPFigee         VARCHAR(30) *not null,
  TitreSOPFigee        VARCHAR(300) *not null,
  *IdEnvironnement     GUID [FK → ENVIRONNEMENT.IdEnvironnement] *not null,
  CodeEnvironnementFigé VARCHAR(20) *not null,
  Statut               VARCHAR(15) *not null
                         CK: IN ('EnCours','Termine','Abandonne'),
  ExecutePar           VARCHAR(150) *not null,
  MotifExecution       NVARCHAR(1000),
  ReferenceTicket      VARCHAR(50),
  *IdAlerteSourceDeclenchement GUID [FK → ALERTE_SUPERVISION.IdAlerte],
  *IdSessionDiagnosticSourceSiDeclenche GUID [FK → SESSION_DIAGNOSTIC.IdSessionDiagnostic],
  DebutLe              DATETIME2(3) *not null,
  FinLe                DATETIME2(3),
  MotifAbandon         NVARCHAR(2000),
  ResultatFinalAtteste VARCHAR(20)
                         CK: IN (NULL,'ProblemeResolu','ProblemeNonResolu','Indetermine'),
  NoteResultat         NVARCHAR(2000),
  ResultatAttestePar   VARCHAR(150),
  ResultatAttesteLe    DATETIME2(3),
  IdCorrelation        VARCHAR(20),
  TauxReussiteHistoriqueSOPAuLancementPct NUMERIC(7,2)
)
```

### TABLE ETAPE_EXECUTION_SOP (1:n EXECUTION_SOP)
```
ETAPE_EXECUTION_SOP (
  IdEtapeExecutionSOP  GUID [PK],
  *IdExecutionSOP      GUID [FK → EXECUTION_SOP.IdExecutionSOP] *not null,
  Ordre                INT *not null,
  NomEtape             VARCHAR(200) *not null,
  InstructionFigee     NVARCHAR(2000),
  ResultatAttenduFigé  NVARCHAR(1000),
  *IdComposantCible    GUID [FK → COMPOSANT_N4.IdComposant],
  NomComposantFige     VARCHAR(200),
  Statut               VARCHAR(20) *not null
                         CK: IN ('AVenir','EnCours','Confirmee','Ecart','Ignoree','Annulee'),
  DebutLe              DATETIME2(3),
  FinLe                DATETIME2(3),
  EstContournableFige  BOOLEAN *not null DEFAULT TRUE,
  ConfirmationPar      VARCHAR(150),
  ConfirmationLe       DATETIME2(3),
  PreuveConstateeOperateur NVARCHAR(4000),
  NoteEcartParRapportAttendu NVARCHAR(2000),
  ContournementPar     VARCHAR(150),
  ContournementMotif   NVARCHAR(1000),
  HistoriqueRouvertures NVARCHAR(max)
)
UK_SOPEXEETP : UNIQUE(IdExecutionSOP, Ordre)
```

### TABLE ASSOCIATION_SOP (association multi-voies)
```
ASSOCIATION_SOP (
  IdAssociationSOP     GUID [PK],
  *IdSOP               GUID [FK → SOP_MODELE.IdSOP] *not null,
  NatureAssociation    VARCHAR(30) *not null
                         CK: IN ('Composant','Signature','SessionDiagnostic'),
  *IdComposant         GUID [FK → COMPOSANT_N4.IdComposant],
  RoleComposantSuggere VARCHAR(30),
  *IdSignature         GUID [FK → SIGNATURE_ANOMALIE.IdSignature],
  CodeSignature        VARCHAR(50),
  *IdSessionDiagnostic GUID [FK → SESSION_DIAGNOSTIC.IdSessionDiagnostic]
)
CK_ASSOC_SOP : 
  (NatureAssociation = 'Composant' AND IdComposant IS NOT NULL)
  OR
  (NatureAssociation = 'Signature' AND IdSignature IS NOT NULL)
  OR
  (NatureAssociation = 'SessionDiagnostic' AND IdSessionDiagnostic IS NOT NULL)
```

---

## Groupe 9 — Audit, Rétention, EDI

### TABLE JOURNAL_AUDIT (APPEND ONLY — SEC-008)
```
JOURNAL_AUDIT (
  IdEntreeAudit        GUID [PK],
  HorodatageEvenement  DATETIME2(3) *not null,
  Action               VARCHAR(30) *not null,
  Issue                VARCHAR(10) *not null
                         CK: IN ('Succes','Refuse','Echec'),
  CategorieEntite      VARCHAR(100),
  IdEntiteConcernee    VARCHAR(100),
  EvenementParIdentite VARCHAR(255) *not null,
  AdresseIpClient      VARCHAR(50),
  MethodeHttpOuOperation VARCHAR(20),
  CheminRessourceOuOperation VARCHAR(500),
  ValeursAvantChangementsJSON NVARCHAR(max),
  ValeursApresChangementsJSON NVARCHAR(max),
  DescriptionEvenement NVARCHAR(1000),
  IdCorrelation        VARCHAR(20),
  *IdEnvironnementConcerne GUID [FK → ENVIRONNEMENT.IdEnvironnement],
  CodeEnvironnementFige VARCHAR(20)
)
CLUSTERED INDEX (HorodatageEvenement DESC)
TRIGGER : Pas de UPDATE ni DELETE sur cette table
```

### TABLE POLITIQUE_RETENTION
```
POLITIQUE_RETENTION (
  IdPolitiqueRetention GUID [PK],
  CodeCategorieDonnees VARCHAR(50) *not null [UK],
  NomLibelle           VARCHAR(200) *not null,
  DureeConservationJours INT *not null,
  ActionFinDeVie       VARCHAR(30) *not null
                         CK: IN ('Supprimer','Archiver','Anonymiser'),
  CheminArchivageSiNecessaire VARCHAR(400),
  EstActivee           BOOLEAN *not null DEFAULT TRUE
)
```

### TABLE FICHIER_EDI_SUIVI
```
FICHIER_EDI_SUIVI (
  IdFichierEdi         GUID [PK],
  *IdEnvironnement     GUID [FK → ENVIRONNEMENT.IdEnvironnement] *not null,
  *IdDossierPartageSource GUID [FK → COMPOSANT_N4.IdComposant],
  NomFichier           VARCHAR(255) *not null,
  PartenaireExtraitOuSaisi VARCHAR(200),
  TypeMessageEdi       VARCHAR(100),
  RecuSurDossierLe     DATETIME2(3) *not null,
  IntegreDansBddLe     DATETIME2(3),
  StatutTraitement     VARCHAR(20) *not null
                         CK: IN ('Recu','EnAttente','Integre','Rejete','Bloque','EnErreurTraitement'),
  NombreTentativesIntegration INT *not null DEFAULT 0,
  DernierMessageErreur NVARCHAR(4000),
  *IdSignatureRapprocheeSiErreur GUID [FK → SIGNATURE_ANOMALIE.IdSignature],
  *IdSessionDiagnosticOuverteSiPblm GUID [FK → SESSION_DIAGNOSTIC.IdSessionDiagnostic],
  CheminFinalApresTraitement VARCHAR(300)
)
INDEX (IdEnvironnement, StatutTraitement, RecuSurDossierLe)
```

---

## Groupe 10 — Capitalisation Incidents & Métriques Business (AMÉLIORATIONS)

### TABLE FICHE_INCIDENT_CAPITALISE
```
FICHE_INCIDENT_CAPITALISE (
  IdIncidentCapitalise GUID [PK],
  ReferenceIncident    VARCHAR(50) *not null [UK],
  *IdEnvironnement     GUID [FK → ENVIRONNEMENT.IdEnvironnement] *not null,
  GraviteDeclenchement VARCHAR(10) *not null
                         CK: IN ('P1','P2','P3','P4'),
  SymptomeInitialDecrit NVARCHAR(2000),
  DebutIncidentConstateLe DATETIME2(3) *not null,
  FinIncidentConstateLe DATETIME2(3),
  DureeTotaleIncidentMinutes BIGINT,
  DetectionParSentinelOuOpsLe DATETIME2(3),
  CausePrincipaleId    VARCHAR(50),
  CausePrincipaleLibelle NVARCHAR(500),
  CauseRacine5Pourquoi NVARCHAR(max),
  ActionsCorrectricesMenees NVARCHAR(max),
  *IdSessionDiagnosticRattachee GUID [FK → SESSION_DIAGNOSTIC.IdSessionDiagnostic],
  *IdExecutionWorkflowRattachee GUID [FK → EXECUTION_WKF.IdExecution],
  SOPsUtilisesListeString NVARCHAR(1000),
  *IdSOPSuiteIncidentGenere GUID [FK → SOP_MODELE.IdSOP],
  ImpactMetierQuantifie NVARCHAR(2000),
  MesuresCompensatoiresMisesEnOeuvre NVARCHAR(4000),
  NombreOperateursImpliques INT,
  NiveauCompetenceResolution VARCHAR(20)
                         CK: IN (NULL,'N1Support','N2Operateur','N3ExpertN4','ExpertExterneNavis'),
  IncidentClosPar      VARCHAR(150),
  IncidentClosLe       DATETIME2(3),
  LienPostMortemDocument VARCHAR(500),
  LeconsTireesEtActionsCorrectives NVARCHAR(max)
)
INDEX (IdEnvironnement, GraviteDeclenchement, DebutIncidentConstateLe)
```

### TABLE METRIQUE_BUSINESS_SUCCES
```
METRIQUE_BUSINESS_SUCCES (
  IdMesureBusiness     GUID [PK],
  PeriodeReference     VARCHAR(10) *not null,   -- Mois-2026-09 / Sem-37
  *IdEnvironnement     GUID [FK → ENVIRONNEMENT.IdEnvironnement],  -- NULL global
  CodeMesure           VARCHAR(50) *not null,
  LibelleMesure        VARCHAR(200) *not null,
  ValeurNumeriqueMoyenneOuSomme NUMERIC(18,4),
  ValeurCibleFixee     NUMERIC(18,4),
  ValeurBaselineAvantSentinel NUMERIC(18,4),
  TendancePeriodePrecedentePct NUMERIC(8,2),
  TypeAgregation       VARCHAR(20) *not null
                         CK: IN ('Moyenne','Somme','Mediane','Pourcentage'),
  CalculeeLe           DATETIME2(3) *not null,
  TailleEchantillonN   BIGINT
)
UK_METBUS : UNIQUE(PeriodeReference, CodeMesure, IdEnvironnement)
```

---

## Groupe 11 — Actions externes déclarées & Config. Azure AD

### TABLE ACTION_EXTERNE_DECLAREE
```
ACTION_EXTERNE_DECLAREE (
  IdActionExterne      GUID [PK],
  TypeSupport          VARCHAR(30) *not null
                         CK: IN ('ExecutionWorkflow','SessionDiagnostic',
                                  'SessionDiagEtapeBloquee','SopExecution'),
  IdEntiteSupport1     GUID *not null,   -- ExecutionWF / SessDiag / SopEx / Etape
  IdEntiteSupport2     GUID,             -- 2ndaire (étape p. ex.)
  DescriptionActionMeneeHorsSentinel NVARCHAR(2000) *not null,
  RealiseePar          VARCHAR(150) *not null,
  RealiseeLe           DATETIME2(3) *not null,
  PreuveConstatee      NVARCHAR(4000),
  ImpactSurEtatCourant NVARCHAR(2000),
  IdCorrelation        VARCHAR(20)
)
INDEX (IdEntiteSupport1, TypeSupport)
```

### TABLE CONFIGURATION_AZURE_AD
```
CONFIGURATION_AZURE_AD (
  IdConfigAzureAd      GUID [PK],
  TenantId             VARCHAR(100),
  ClientId             VARCHAR(100),
  Authority            VARCHAR(300),
  RedirectUri          VARCHAR(300),
  Scope                VARCHAR(500),
  MFAObligatoire       BOOLEAN *not null DEFAULT TRUE,
  GroupesAutorisesJSON NVARCHAR(2000),
  EstActivee           BOOLEAN *not null DEFAULT FALSE
)
```

---

## Fin du Modèle Logique de Données — 46 tables
