# Analyse MERISE — N4 Sentinel
## 07. Modèle Physique de Données (MPD) — SQL Server DDL

**Cible** : SQL Server 2019+ / Azure SQL — Connexion String existante : `Database=n4sentinel` (voir [appsettings.json](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Web/appsettings.json#L3-L9))
**Contexte EF Core** : [N4SentinelDbContext.cs](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Infrastructure/Persistence/N4SentinelDbContext.cs)
**Tables EF Core existantes** : Environments, Servers, Components, ComponentDependencies, AuditEntries, Credentials, Alerts, Workflows, WorkflowSteps, Executions, ExecutionSteps, EnvironmentLocks, Signatures, Sessions, Sources, Findings, Hypotheses, PhaseTransitions, EnvironmentGrants, Documents, DocumentSections, KnowledgeFeedback, CorrelationRules, CorrelationConditions, Sops, SopSteps, SopAssociations, SopExecutions, SopExecutionSteps, SharedFolderSnapshots, EdiFiles, RetentionPolicies, DiagnosticSettings, AzureAdSettings, ComponentSignals, ExternalActionDeclarations, ApprovalMatrixRules, PasswordHistoryRecords, + tables Identity ASP.NET
**Ajouts MERISE (par rapport aux migrations existantes)** : ActionPreventiveRecommandee, FicheIncidentCapitalise, MetriqueBusinessSucces, ServiceAssocie (1:n Components), Contrôle Préalable Modèle (PreChecks Workflow)

---

```sql
/* =========================================================================
   N4 SENTINEL — MPD Modèle Physique SQL Server
   Schéma : dbo (par défaut, cohérent avec migrations EF existantes)
   Contraintes : PK, FK, UK, CK, Index, Trigger append-only Audit
   Nouveautés vs code existant : APR, FicheIncident, MétriquesBusiness,
     ServicesAssocies, Contrôles Préalables Workflow modèle
   ========================================================================= */

/* -------------------------------------------------------------------------
   00. SÉCURITÉ BASE — Schéma, Trigger EMPÊCHANT suppression/modification Audit
   ------------------------------------------------------------------------- */

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'dbo') EXEC ('CREATE SCHEMA dbo AUTHORIZATION dbo');
GO

/* -------------------------------------------------------------------------
   01. CATÉGORIES RÉFÉRENTIEL — Environnements, Serveurs, Composants
   ------------------------------------------------------------------------- */

/* -------- ENVIRONNEMENTS (enrichi vs migrations exist : +SharedFolderUNC) */
IF OBJECT_ID('dbo.Environments', 'U') IS NULL
CREATE TABLE dbo.Environments (
    Id                          UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    Code                        NVARCHAR(20)      NOT NULL,
    Name                        NVARCHAR(100)     NOT NULL,
    Type                        INT               NOT NULL,   -- enum: Dev/Test/UAT/Prod/Formation
    Description                 NVARCHAR(500),
    OwnerAdministrator         NVARCHAR(100),
    BaseUrl                     NVARCHAR(255),
    SharedFolderUNC            NVARCHAR(255),      /* CHEMIN DOSSIER PARTAGE N4 */
    TimeZoneId                 NVARCHAR(50) DEFAULT 'Africa/Abidjan',
    LifecycleStatus            INT               NOT NULL DEFAULT 0,  /* Brouillon→Actif */
    CreatedAt                  DATETIME2(3)      NOT NULL DEFAULT SYSUTCDATETIME(),
    ModifiedAt                 DATETIME2(3),
    CONSTRAINT PK_Environments PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT UK_Environments_Code UNIQUE NONCLUSTERED (Code),
    CONSTRAINT CK_Env_Type CHECK (Type BETWEEN 0 AND 4)
);

/* -------- SERVEURS (enrichi + RAM, CPU, DISK) */
IF OBJECT_ID('dbo.Servers', 'U') IS NULL
CREATE TABLE dbo.Servers (
    Id                          UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    EnvironmentId               UNIQUEIDENTIFIER NOT NULL,
    HostName                    NVARCHAR(100)     NOT NULL,
    IpAddress                   NVARCHAR(50),
    OperatingSystem            NVARCHAR(100),
    ApplicationRole            NVARCHAR(100),
    CpuCores                   INT,
    MemoryGB                   INT,
    TotalDiskGB                BIGINT,
    IsActive                   BIT               NOT NULL DEFAULT 1,
    CONSTRAINT PK_Servers PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT FK_Servers_Environments FOREIGN KEY (EnvironmentId)
        REFERENCES dbo.Environments(Id) ON DELETE CASCADE,
    CONSTRAINT UK_Servers_EnvHost UNIQUE NONCLUSTERED (EnvironmentId, HostName)
);

/* -------- COMPOSANTS N4 (enrichis vs migration existante) */
IF OBJECT_ID('dbo.Components', 'U') IS NULL
CREATE TABLE dbo.Components (
    Id                          UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    EnvironmentId               UNIQUEIDENTIFIER NOT NULL,
    ServerId                    UNIQUEIDENTIFIER NULL,
    LogicalName                NVARCHAR(100)     NOT NULL,
    Role                       INT               NOT NULL, /* Cluster/Center/Standby/Bridge/Xps/Ecn4/DB/Amq/SharedFolder/Edi/Job/Autre */
    WindowsServiceName         NVARCHAR(200),
    ProcessName                NVARCHAR(100),
    HealthEndpoint             NVARCHAR(255),
    Port                       INT,
    Criticity                  INT               NOT NULL DEFAULT 1,  /* 0=F..3=Critique */
    ControlMode                INT               NOT NULL DEFAULT 0,  /* 0=Pilotable/1=SuperviseSeulement/2=NonSupervise */
    LifecycleStatus            INT               NOT NULL DEFAULT 3, /* Actif */
    InMaintenance              BIT               NOT NULL DEFAULT 0,
    MaintenanceNote            NVARCHAR(500),
    MaintenanceUntil           DATETIME2(3),
    MaintenanceReason          NVARCHAR(500),
    MaintenanceBy              NVARCHAR(100),
    StartupOrder               INT,
    Description                NVARCHAR(500),
    TechnicalOwner             NVARCHAR(100),
    /* ReadinessProfile (stocké en JSON + scalaires principaux — converters EF Core) */
    LogPath                    NVARCHAR(300),
    ReadyPatterns              NVARCHAR(MAX)      NOT NULL DEFAULT N'[]',
    ErrorPatterns              NVARCHAR(MAX)      NOT NULL DEFAULT N'[]',
    IgnorePatterns             NVARCHAR(MAX)      NOT NULL DEFAULT N'[]',
    ActiveRolePatterns         NVARCHAR(MAX)      NOT NULL DEFAULT N'[]',
    SyncPatterns               NVARCHAR(MAX)      NOT NULL DEFAULT N'[]',
    SyncDelayMin               INT               NOT NULL DEFAULT 15,
    ReadyTimeoutSec            INT               NOT NULL DEFAULT 300,
    LogReadyTimeoutSec         INT               NOT NULL DEFAULT 1800,
    StopTimeoutSec             INT               NOT NULL DEFAULT 600,
    PollIntervalSec            INT               NOT NULL DEFAULT 10,
    ProgressEverySec           INT               NOT NULL DEFAULT 60,
    PostReadyObserveSec        INT               NOT NULL DEFAULT 0,
    /* Shared Folder Profile — NULL si composant n'est pas un dossier partagé */
    SFRootPath                 NVARCHAR(300),
    SFCategory                 INT NULL,   /* ConfigN4/ActiveMqKahaDb/EchangeEdi/Archives/DossiersErreur/Autre */
    SFPendingFolder            NVARCHAR(200),
    SFConsumedFolder           NVARCHAR(200),
    SFBlockedFolder            NVARCHAR(200),
    SFErrorFolder              NVARCHAR(200),
    SFMaxAgePendingHours       INT DEFAULT 4,
    SFMaxWriteLatencyMs        INT DEFAULT 500,
    SFMaxGrowthGBPerHour       DECIMAL(12,2),
    SFEdiFileNamingRegex       NVARCHAR(500),
    SFMaxNoIntegrationHours    INT DEFAULT 2,
    SFLastBackupAt             DATETIME2(3),
    SFLastBackupBy             NVARCHAR(100),
    CONSTRAINT PK_Components PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT FK_Components_Environments FOREIGN KEY (EnvironmentId)
        REFERENCES dbo.Environments(Id) ON DELETE CASCADE,
    CONSTRAINT FK_Components_Servers FOREIGN KEY (ServerId)
        REFERENCES dbo.Servers(Id) ON DELETE SET NULL,
    /* RG-0105 : 1 seul CenterNode + 1 seul Standby par Env */
    CONSTRAINT CK_Components_Role CHECK (Role BETWEEN 0 AND 11),
    CONSTRAINT CK_Components_Criticity CHECK (Criticity BETWEEN 0 AND 3)
);
CREATE NONCLUSTERED INDEX IX_Components_EnvRole ON dbo.Components(EnvironmentId, Role);
CREATE UNIQUE INDEX UX_Components_OneCenterPerEnv ON dbo.Components(EnvironmentId) WHERE Role = 1;   /* CenterNode */
CREATE UNIQUE INDEX UX_Components_OneStandbyPerEnv ON dbo.Components(EnvironmentId) WHERE Role = 2; /* Standby */

/* -------- SERVICES WINDOWS ASSOCIÉS À UN COMPOSANT (XPSGateServices, etc.) */
IF OBJECT_ID('dbo.ComponentAssociatedServices', 'U') IS NULL
CREATE TABLE dbo.ComponentAssociatedServices (
    Id                          UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    ComponentId                 UNIQUEIDENTIFIER NOT NULL,
    WindowsServiceName         NVARCHAR(200)     NOT NULL,
    IsRequiredForOperational   BIT               NOT NULL DEFAULT 1,
    CONSTRAINT PK_ComponentAssociatedServices PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT FK_CAS_Components FOREIGN KEY (ComponentId)
        REFERENCES dbo.Components(Id) ON DELETE CASCADE
);

/* -------- DÉPENDANCES COMPOSANT → COMPOSANT (déjà existe) */
IF OBJECT_ID('dbo.ComponentDependencies', 'U') IS NULL
CREATE TABLE dbo.ComponentDependencies (
    Id                          UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    SourceComponentId           UNIQUEIDENTIFIER NOT NULL,
    DependsOnComponentId        UNIQUEIDENTIFIER NOT NULL,
    DependencyKind             INT               NOT NULL DEFAULT 0, /* 0=Start/1=Runtime/2=Informative */
    Notes                      NVARCHAR(500),
    CONSTRAINT PK_ComponentDependencies PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT FK_CD_Source FOREIGN KEY (SourceComponentId)
        REFERENCES dbo.Components(Id),
    CONSTRAINT FK_CD_DependsOn FOREIGN KEY (DependsOnComponentId)
        REFERENCES dbo.Components(Id),
    CONSTRAINT UK_CD_Unique UNIQUE NONCLUSTERED (SourceComponentId, DependsOnComponentId, DependencyKind),
    CONSTRAINT CK_CD_NoSelfDep CHECK (SourceComponentId <> DependsOnComponentId)
);

/* -------------------------------------------------------------------------
   02. IDENTITÉ, RÔLES, HABILITATIONS (ASP.NET Identity existant + habilitations)
   ------------------------------------------------------------------------- */

/* Rôles = table AspNetRoles (ASP.NET Identity) avec 8 rôles standards (CdC §2.5) :
   Lecteur, OperateurN4, AdminN4, AdminInfra, Validateur, AdminSolution, Auditeur, TOSManager */

/* HABILITATIONS ENVIRONNEMENT × RÔLE × UTILISATEUR — déjà sous EnvironmentGrants */
IF OBJECT_ID('dbo.EnvironmentGrants', 'U') IS NULL
CREATE TABLE dbo.EnvironmentGrants (
    Id                          UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    UserId                      UNIQUEIDENTIFIER NOT NULL,
    EnvironmentId               UNIQUEIDENTIFIER NULL,   /* NULL = droit global */
    RoleName                    NVARCHAR(256)     NOT NULL,
    ValidFrom                  DATETIME2(3)      NOT NULL DEFAULT SYSUTCDATETIME(),
    ValidUntil                 DATETIME2(3) NULL,
    Motif                      NVARCHAR(1000),
    AccordPar                  NVARCHAR(150),
    CONSTRAINT PK_EnvironmentGrants PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT UK_Grants_Unique UNIQUE NONCLUSTERED (UserId, EnvironmentId, RoleName)
);

/* HISTORIQUE MOT DE PASSE — déjà existe */
IF OBJECT_ID('dbo.PasswordHistoryRecords', 'U') IS NULL
CREATE TABLE dbo.PasswordHistoryRecords (
    Id                          UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    UserId                      UNIQUEIDENTIFIER NOT NULL,
    PasswordHash               NVARCHAR(1000)    NOT NULL,
    CreatedAt                  DATETIME2(3)      NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_PasswordHistoryRecords PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT FK_PwdHist_AspNetUsers FOREIGN KEY (UserId)
        REFERENCES dbo.AspNetUsers(Id) ON DELETE CASCADE
);
CREATE NONCLUSTERED INDEX IX_PwdHist_User ON dbo.PasswordHistoryRecords(UserId);

/* COMPTES NOMINATIFS — ApplicationUser (AspNetUsers) possède Email / UserName / MFA / TotpKey */

/* CRÉDENTIELS TECHNIQUES (coffre) — déjà existe */
IF OBJECT_ID('dbo.Credentials', 'U') IS NULL
CREATE TABLE dbo.Credentials (
    Id                          UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    EnvironmentId               UNIQUEIDENTIFIER NOT NULL,
    ServerId                    UNIQUEIDENTIFIER NULL,
    DisplayName                NVARCHAR(200)     NOT NULL,
    AuthType                   INT               NOT NULL DEFAULT 0, /* Domaine/Local/Sql/Certificat/SSH */
    ProtectedLogin             NVARCHAR(500),
    EncryptedPassword          VARBINARY(MAX),
    CertificateThumbprint     NVARCHAR(200),
    ValidFrom                  DATETIME2(3),
    ExpiresAt                  DATETIME2(3),
    UsageDescription          NVARCHAR(500),
    IsActive                   BIT               NOT NULL DEFAULT 1,
    CONSTRAINT PK_Credentials PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT FK_Credentials_Environments FOREIGN KEY (EnvironmentId)
        REFERENCES dbo.Environments(Id) ON DELETE CASCADE,
    CONSTRAINT FK_Credentials_Servers FOREIGN KEY (ServerId)
        REFERENCES dbo.Servers(Id) ON DELETE SET NULL
);
CREATE NONCLUSTERED INDEX IX_Cred_Exp ON dbo.Credentials(ExpiresAt) WHERE IsActive = 1;

/* CONFIGURATION AZURE AD V2 */
IF OBJECT_ID('dbo.AzureAdSettings', 'U') IS NULL
CREATE TABLE dbo.AzureAdSettings (
    Id                          UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    TenantId                   NVARCHAR(100),
    ClientId                   NVARCHAR(100),
    Authority                  NVARCHAR(300),
    RedirectUri                NVARCHAR(300),
    Scope                      NVARCHAR(500),
    RequireMfa                 BIT               NOT NULL DEFAULT 1,
    AllowedGroupsJson         NVARCHAR(2000),
    IsEnabled                  BIT               NOT NULL DEFAULT 0,
    CONSTRAINT PK_AzureAdSettings PRIMARY KEY CLUSTERED (Id)
);

/* -------------------------------------------------------------------------
   03. WORKFLOW MODÈLE + ÉTAPES (S + enrichissement Contrôles Préalables)
   ------------------------------------------------------------------------- */

IF OBJECT_ID('dbo.Workflows', 'U') IS NULL
CREATE TABLE dbo.Workflows (
    Id                          UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    EnvironmentId               UNIQUEIDENTIFIER NOT NULL,
    Name                       NVARCHAR(200)     NOT NULL,
    Type                       INT               NOT NULL, /* ArretComplet/DemarrageComplet/RedemarrageCluster/BasculeCenter/ArretPartiel/DemarragePartiel/MaintenanceComposant/ReconstitutionDossierPartage */
    Version                    INT               NOT NULL DEFAULT 1,
    LifecycleStatus            INT               NOT NULL DEFAULT 0, /* Brouillon→Actif */
    AutomationLevel            INT               NOT NULL DEFAULT 0, /* 0=SemiAuto/1=Auto */
    RequiresDoubleApproval     BIT               NOT NULL DEFAULT 0,
    Description                NVARCHAR(1000),
    CONSTRAINT PK_Workflows PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT FK_Workflows_Environments FOREIGN KEY (EnvironmentId)
        REFERENCES dbo.Environments(Id) ON DELETE CASCADE,
    CONSTRAINT UK_Workflow_Unique UNIQUE NONCLUSTERED (EnvironmentId, Name, Version)
);

IF OBJECT_ID('dbo.WorkflowSteps', 'U') IS NULL
CREATE TABLE dbo.WorkflowSteps (
    Id                          UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    WorkflowId                 UNIQUEIDENTIFIER NOT NULL,
    Order                      INT               NOT NULL,
    Name                       NVARCHAR(200)     NOT NULL,
    Action                     INT               NOT NULL, /* Demarrer/Arreter/Redemarrer/Attente/VerrouPreRequis/ControlePreuve/InterventionManuelle/ArretForce */
    TargetComponentId          UNIQUEIDENTIFIER NULL,
    TimeoutSeconds             INT               NOT NULL DEFAULT 1800,
    ExpectedDurationSec        INT               NOT NULL DEFAULT 120,
    WarningThresholdSec        INT               NOT NULL DEFAULT 240,
    MaxAttempts                INT               NOT NULL DEFAULT 0,
    AutomaticRetry             BIT               NOT NULL DEFAULT 0,  /* RG-0408: faux critique */
    RetryDelaySeconds          INT               NOT NULL DEFAULT 30,
    IsSkippable                BIT               NOT NULL DEFAULT 1,
    OperatorConfirmation       BIT               NOT NULL DEFAULT 1,
    IsParallelizable           BIT               NOT NULL DEFAULT 0,
    FailurePolicy              INT               NOT NULL DEFAULT 0, /* Bloquer/ContinuerAlerte/ContinuerSilence */
    ManualInstruction          NVARCHAR(2000),
    CONSTRAINT PK_WorkflowSteps PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT FK_WFSteps_Workflows FOREIGN KEY (WorkflowId)
        REFERENCES dbo.Workflows(Id) ON DELETE CASCADE,
    CONSTRAINT FK_WFSteps_Components FOREIGN KEY (TargetComponentId)
        REFERENCES dbo.Components(Id) ON DELETE SET NULL,
    CONSTRAINT UK_WFSteps_UniqueOrder UNIQUE NONCLUSTERED (WorkflowId, [Order])
);

/* CONTRÔLES PRÉALABLES MODÈLE (PRÉ-CHECK associé à workflow ou global env) */
IF OBJECT_ID('dbo.WorkflowPreChecks', 'U') IS NULL
CREATE TABLE dbo.WorkflowPreChecks (
    Id                          UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    WorkflowId                 UNIQUEIDENTIFIER NULL,   /* NULL = global à l'env */
    EnvironmentId              UNIQUEIDENTIFIER NULL,
    Code                       NVARCHAR(50)      NOT NULL,
    Label                      NVARCHAR(200)     NOT NULL,
    SeverityIfFailed           INT               NOT NULL DEFAULT 0,  /* Bloquant/Avertissement/NonApplicable */
    ScriptDefinitionJson       NVARCHAR(MAX),
    TargetComponentId          UNIQUEIDENTIFIER NULL,
    CONSTRAINT PK_WorkflowPreChecks PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT UK_PreChecks_Code UNIQUE NONCLUSTERED (Code),
    CONSTRAINT FK_PC_Workflows FOREIGN KEY (WorkflowId)
        REFERENCES dbo.Workflows(Id) ON DELETE CASCADE,
    CONSTRAINT FK_PC_Components FOREIGN KEY (TargetComponentId)
        REFERENCES dbo.Components(Id) ON DELETE SET NULL
);

/* MATRICE APPROBATION */
IF OBJECT_ID('dbo.ApprovalMatrixRules', 'U') IS NULL
CREATE TABLE dbo.ApprovalMatrixRules (
    Id                          UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    EnvironmentId               UNIQUEIDENTIFIER NULL,   /* NULL = global */
    AppliesToRole              INT NULL,                 /* NULL = tous */
    AppliesToCriticity         INT NULL,                 /* NULL = toutes */
    AppliesToWorkflowType      INT NULL,                 /* NULL = tous */
    RequiredApprovalsCount     TINYINT           NOT NULL DEFAULT 1,  /* 1 simple / 2 double */
    RequiredApprovalRole      NVARCHAR(256)     NOT NULL,
    OperatorMustBeDifferent   BIT               NOT NULL DEFAULT 1,
    ValidityMinutes            INT               NOT NULL DEFAULT 120,
    IsActive                   BIT               NOT NULL DEFAULT 1,
    CONSTRAINT PK_ApprovalMatrixRules PRIMARY KEY CLUSTERED (Id)
);

/* -------------------------------------------------------------------------
   04. EXÉCUTION WORKFLOW + ÉTAPES + VERROU MUTEX ENV
   ------------------------------------------------------------------------- */

IF OBJECT_ID('dbo.Executions', 'U') IS NULL
CREATE TABLE dbo.Executions (
    Id                          UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    WorkflowId                 UNIQUEIDENTIFIER NOT NULL,
    LaunchedWorkflowVersion   INT               NOT NULL,
    FrozenWorkflowName       NVARCHAR(200)     NOT NULL,
    EnvironmentId              UNIQUEIDENTIFIER NOT NULL,
    FrozenEnvironmentCode    NVARCHAR(20)      NOT NULL,
    Status                     INT               NOT NULL DEFAULT 0,  /* EnPreparation..Annule */
    IsSimulation               BIT               NOT NULL DEFAULT 0,
    AutomationLevel            INT               NOT NULL DEFAULT 0,
    ForceFallbackSemiAuto     BIT               NOT NULL DEFAULT 0,
    RequestedBy               NVARCHAR(150),
    OperatorIdentityLogin    NVARCHAR(150),
    OperatorIdentityLabel    NVARCHAR(255),
    OperationReason          NVARCHAR(1000),
    ReferenceTicket          NVARCHAR(50),
    ExpectedImpact           NVARCHAR(1000),
    WindowStart               DATETIME2(3),
    WindowEnd                 DATETIME2(3),
    EstimatedTotalDurationSec INT,
    Approv1By                NVARCHAR(150),
    Approv1At                DATETIME2(3),
    Approv2By                NVARCHAR(150),
    Approv2At                DATETIME2(3),
    RequiresContinuityChoice  BIT               NOT NULL DEFAULT 0,
    ContinuityChoice         INT NULL,                   /* ResterActif / Basculer */
    ContinuityChoiceBy       NVARCHAR(150),
    ContinuityChoiceAt       DATETIME2(3),
    ActualStartedAt          DATETIME2(3),
    ActualFinishedAt         DATETIME2(3),
    LastEngineHeartbeatAt    DATETIME2(3),
    PauseRequestedBy         NVARCHAR(150),
    CancellationRequestedBy  NVARCHAR(150),
    FinalSummary             NVARCHAR(2000),
    PostCancellationReport   NVARCHAR(4000),
    RequiresManualIntervention BIT           NOT NULL DEFAULT 0,
    CorrelationId            NVARCHAR(20),
    PreflightReportJson      NVARCHAR(MAX),
    PreflightPerformedAt     DATETIME2(3),
    PreflightHasBlocking     BIT,
    CONSTRAINT PK_Executions PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT FK_Exec_Workflows FOREIGN KEY (WorkflowId)
        REFERENCES dbo.Workflows(Id),
    CONSTRAINT FK_Exec_Environments FOREIGN KEY (EnvironmentId)
        REFERENCES dbo.Environments(Id),
    CONSTRAINT CK_Exec_Continuity CHECK (ContinuityChoice IS NULL OR ContinuityChoice IN (0,1))
);
CREATE NONCLUSTERED INDEX IX_Exec_EnvStatus ON dbo.Executions(EnvironmentId, Status);
CREATE NONCLUSTERED INDEX IX_Exec_Start ON dbo.Executions(ActualStartedAt);
CREATE NONCLUSTERED INDEX IX_Exec_Corr ON dbo.Executions(CorrelationId);

IF OBJECT_ID('dbo.ExecutionSteps', 'U') IS NULL
CREATE TABLE dbo.ExecutionSteps (
    Id                          UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    ExecutionId                UNIQUEIDENTIFIER NOT NULL,
    Order                      INT               NOT NULL,
    Name                       NVARCHAR(200)     NOT NULL,
    Action                     INT               NOT NULL,
    TargetComponentId          UNIQUEIDENTIFIER NULL,
    FrozenComponentName       NVARCHAR(200),
    FrozenHostName            NVARCHAR(150),
    State                      INT               NOT NULL DEFAULT 0,  /* AVenir..Annule */
    ActualStartedAt           DATETIME2(3),
    ActualFinishedAt          DATETIME2(3),
    ProgressMessage          NVARCHAR(500),
    TechnicalEvidence        NVARCHAR(4000),
    ExpurgatedCommand        NVARCHAR(4000),
    ErrorText                 NVARCHAR(4000),
    ErrorTaxonomy             INT NULL,  /* CommandeRefusée/Timeout/StopPending/NonConfig/Prerequis/EtatInattendu/Inconnu */
    AttemptsCount             INT               NOT NULL DEFAULT 0,
    FrozenMaxAttempts        INT,
    FrozenAutoRetry          BIT,
    FrozenRetryDelaySec      INT,
    RetryAllowedAfter        DATETIME2(3),
    SkippedBy                NVARCHAR(150),
    SkipReason               NVARCHAR(1000),
    SkipCoApprovedBy         NVARCHAR(150),
    SkipCoApprovedAt         DATETIME2(3),
    ForcedStopBy             NVARCHAR(150),   /* RG-0407: Jamais auto */
    ForcedStopReason         NVARCHAR(1000),
    OperatorConfirmedBy      NVARCHAR(150),
    OperatorConfirmedAt      DATETIME2(3),
    OperatorNote             NVARCHAR(2000),
    RequireAttachmentProof   BIT               NOT NULL DEFAULT 0,
    ProofAttachmentFileName  NVARCHAR(255),
    ProofAttachmentMime     NVARCHAR(100),
    ProofAttachmentBlob     VARBINARY(MAX),
    LinkedDiagnosticSessionId UNIQUEIDENTIFIER NULL,
    FrozenTimeoutSec        INT,
    FrozenExpectedDurationSec INT,
    FrozenWarningThresholdSec INT,
    FrozenIsSkippable       BIT,
    FrozenNeedConfirmation  BIT,
    FrozenIsParallelizable  BIT,
    FrozenFailurePolicy     INT,
    FrozenManualInstruction NVARCHAR(2000),
    IsDestructiveAction     BIT               NOT NULL DEFAULT 0,
    CONSTRAINT PK_ExecutionSteps PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT FK_ExStep_Exec FOREIGN KEY (ExecutionId)
        REFERENCES dbo.Executions(Id) ON DELETE CASCADE,
    CONSTRAINT FK_ExStep_Components FOREIGN KEY (TargetComponentId)
        REFERENCES dbo.Components(Id) ON DELETE SET NULL,
    CONSTRAINT UK_ExStep_UniqueOrder UNIQUE NONCLUSTERED (ExecutionId, [Order])
);
CREATE NONCLUSTERED INDEX IX_ExStep_State ON dbo.ExecutionSteps(ExecutionId, State);

/* VERROU MUTEX PAR ENVIRONNEMENT — 1 seul verrou actif à la fois */
IF OBJECT_ID('dbo.EnvironmentLocks', 'U') IS NULL
CREATE TABLE dbo.EnvironmentLocks (
    Id                          UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    EnvironmentId               UNIQUEIDENTIFIER NOT NULL,
    ExecutionId                UNIQUEIDENTIFIER NOT NULL,
    HeldBy                    NVARCHAR(150)     NOT NULL,
    AcquisitionReason         NVARCHAR(500)     NOT NULL,
    AcquiredAt                DATETIME2(3)      NOT NULL DEFAULT SYSUTCDATETIME(),
    ExpiresAt                 DATETIME2(3)      NOT NULL,
    CONSTRAINT PK_EnvironmentLocks PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT FK_EnvLock_Environments FOREIGN KEY (EnvironmentId)
        REFERENCES dbo.Environments(Id),
    CONSTRAINT FK_EnvLock_Exec FOREIGN KEY (ExecutionId)
        REFERENCES dbo.Executions(Id)
);
/* UN SEUL VERROU ACTIF PAR ENVIRONNEMENT (index conditionnel SQL Server) */
CREATE UNIQUE INDEX UX_EnvLock_ActiveOne
    ON dbo.EnvironmentLocks(EnvironmentId)
    WHERE ExpiresAt > SYSUTCDATETIME();

/* -------------------------------------------------------------------------
   05. SUPERVISION — SIGNAUX HISTORIQUE + ALERTES + APR (NOUVEAU)
   ------------------------------------------------------------------------- */

IF OBJECT_ID('dbo.ComponentSignals', 'U') IS NULL
CREATE TABLE dbo.ComponentSignals (
    Id                          UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    ComponentId                 UNIQUEIDENTIFIER NOT NULL,
    MeasuredAt                 DATETIME2(3)      NOT NULL DEFAULT SYSUTCDATETIME(),
    MetricName                 NVARCHAR(100)     NOT NULL,
    NumericValue               DECIMAL(18,4),
    StringValue                NVARCHAR(500),
    AlertThresholdHit          BIT               NOT NULL DEFAULT 0,
    CollectedByConnector       NVARCHAR(200),
    CollectionError            NVARCHAR(2000),
    CONSTRAINT PK_ComponentSignals PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT FK_Signals_Components FOREIGN KEY (ComponentId)
        REFERENCES dbo.Components(Id) ON DELETE CASCADE
);
CREATE CLUSTERED COLUMNSTORE INDEX CCI_ComponentSignals /* optionnel : pour tables volumineuses >1M lignes */
    ON dbo.ComponentSignals; /* A activer après stabilisation ; on garde aussi le CLUSTERED PK classique ci-dessus */
CREATE NONCLUSTERED INDEX IX_Signals_CmpTime
    ON dbo.ComponentSignals(ComponentId, MeasuredAt DESC) INCLUDE (MetricName, NumericValue, AlertThresholdHit);

IF OBJECT_ID('dbo.Alerts', 'U') IS NULL
CREATE TABLE dbo.Alerts (
    Id                          UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    EnvironmentId               UNIQUEIDENTIFIER NOT NULL,
    ComponentId                 UNIQUEIDENTIFIER NULL,
    Code                       NVARCHAR(50)      NOT NULL,
    Severity                   INT               NOT NULL, /* 0-4 : Info → Critique */
    Title                      NVARCHAR(200)     NOT NULL,
    DetailedDescription        NVARCHAR(4000),
    AlertKind                  INT               NOT NULL DEFAULT 0, /* 0=TempsRéel/1=Prédictive/2=PréventiveAPR */
    PredictionConfidencePct    INT CHECK (PredictionConfidencePct BETWEEN 0 AND 100),
    SuggestedAction           NVARCHAR(2000),
    P1AvoidedRiskPctIfDone    INT CHECK (P1AvoidedRiskPctIfDone BETWEEN 0 AND 100),
    SuggestedDurationMin       INT,
    CreatedAt                  DATETIME2(3)      NOT NULL DEFAULT SYSUTCDATETIME(),
    AcknowledgedBy            NVARCHAR(150),
    AcknowledgedAt            DATETIME2(3),
    ResolvedBy                NVARCHAR(150),
    ResolvedAt                DATETIME2(3),
    Status                     INT               NOT NULL DEFAULT 0, /* Nouvelle/Aquittée/Traitée/FausseAlerte/Ignorée */
    OpenedDiagnosticSessionId UNIQUEIDENTIFIER NULL,
    CONSTRAINT PK_Alerts PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT FK_Alerts_Environments FOREIGN KEY (EnvironmentId)
        REFERENCES dbo.Environments(Id),
    CONSTRAINT FK_Alerts_Components FOREIGN KEY (ComponentId)
        REFERENCES dbo.Components(Id) ON DELETE SET NULL
);
CREATE NONCLUSTERED INDEX IX_Alerts_EnvGravStat
    ON dbo.Alerts(EnvironmentId, Severity, Status) INCLUDE (Title, CreatedAt);

/* -------------------------------------------------------------------------
   05b. — AMÉLIORATIONS : ACTIONS PRÉVENTIVES RECOMMANDÉES (APR) — NOUVELLE TABLE
   ------------------------------------------------------------------------- */
IF OBJECT_ID('dbo.PreventiveRecommendedActions', 'U') IS NULL
CREATE TABLE dbo.PreventiveRecommendedActions (
    Id                          UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    EnvironmentId               UNIQUEIDENTIFIER NOT NULL,
    ReferenceCode              NVARCHAR(50)      NOT NULL,
    Label                      NVARCHAR(300)     NOT NULL,
    JustificationDetails       NVARCHAR(4000),
    TriggeringThreshold       NVARCHAR(50),      /* ex: FragmentationPct > 30 */
    CurrentObservedValue      NVARCHAR(50),      /* ex: 34.7% mesuré */
    P1RiskAvoidedPct          INT               NOT NULL DEFAULT 0 CHECK (P1RiskAvoidedPct BETWEEN 0 AND 100),
    EstimatedDurationMinutes   INT,
    BusinessImpactIfDone     NVARCHAR(500),
    SuggestedWindowStart     DATETIME2(3),
    SuggestedWindowEnd       DATETIME2(3),
    SuggestedWorkflowId      UNIQUEIDENTIFIER NULL,
    SourcePredictiveAlertId  UNIQUEIDENTIFIER NULL,
    GeneratedAt               DATETIME2(3)      NOT NULL DEFAULT SYSUTCDATETIME(),
    APRStatus                 INT               NOT NULL DEFAULT 0,   /* Nouvelle/Programmée/Réalisée/Ignorée/Expirée */
    ScheduledBy              NVARCHAR(150),
    ScheduledAt              DATETIME2(3),
    RealisedBy               NVARCHAR(150),
    RealisedAt               DATETIME2(3),
    RealisedViaExecutionId   UNIQUEIDENTIFIER NULL,
    IgnoredBy                NVARCHAR(150),
    IgnoreReason             NVARCHAR(2000),
    CONSTRAINT PK_PreventiveActions PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT FK_APR_Environments FOREIGN KEY (EnvironmentId)
        REFERENCES dbo.Environments(Id) ON DELETE CASCADE,
    CONSTRAINT FK_APR_Workflows FOREIGN KEY (SuggestedWorkflowId)
        REFERENCES dbo.Workflows(Id) ON DELETE SET NULL,
    CONSTRAINT FK_APR_Alerts FOREIGN KEY (SourcePredictiveAlertId)
        REFERENCES dbo.Alerts(Id) ON DELETE SET NULL,
    CONSTRAINT FK_APR_Executions FOREIGN KEY (RealisedViaExecutionId)
        REFERENCES dbo.Executions(Id) ON DELETE SET NULL
);
CREATE NONCLUSTERED INDEX IX_APR_EnvStatus ON dbo.PreventiveRecommendedActions(EnvironmentId, APRStatus, GeneratedAt);

/* -------------------------------------------------------------------------
   06. DIAGNOSTIC — 8 Phases, Journaux, Signatures, Corrélations, Hypothèses
   ------------------------------------------------------------------------- */

/* SIGNATURES ANOMALIES CATALOGUÉES */
IF OBJECT_ID('dbo.Signatures', 'U') IS NULL
CREATE TABLE dbo.Signatures (
    Id                          UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    Code                       NVARCHAR(50)      NOT NULL,
    Name                       NVARCHAR(200)     NOT NULL,
    RegExPattern               NVARCHAR(1000)    NOT NULL,
    Domain                     INT               NOT NULL,  /* 0=BD,1=Network,2=Mem,3=Config..14=Indéterminé */
    Severity                   INT               NOT NULL,  /* 0-3 Info→Critique */
    Origin                     INT               NOT NULL DEFAULT 1,  /* 0=Editeur,1=Site */
    BusinessMeaning            NVARCHAR(2000),
    RecommendedCorrection     NVARCHAR(2000),
    ReferenceDocumentation   NVARCHAR(500),
    CounterEvidenceHint       NVARCHAR(2000),
    Version                    INT               NOT NULL DEFAULT 1,
    ConfidenceWeight0To100     INT               NOT NULL DEFAULT 50 CHECK (ConfidenceWeight0To100 BETWEEN 1 AND 100),
    RestrictedToRole           INT NULL,
    IsEnabled                  BIT               NOT NULL DEFAULT 1,
    ValidationStatus           INT               NOT NULL DEFAULT 2,  /* 0=Brouillon/1=AValider/2=Validé/3=Actif */
    CONSTRAINT PK_Signatures PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT UK_Signatures_Code UNIQUE NONCLUSTERED (Code)
);

/* RÈGLES CORRÉLATION MULTI-SIGNAUX */
IF OBJECT_ID('dbo.CorrelationRules', 'U') IS NULL
CREATE TABLE dbo.CorrelationRules (
    Id                          UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    Code                       NVARCHAR(50)      NOT NULL,
    Name                       NVARCHAR(200)     NOT NULL,
    Domain                     INT               NOT NULL,
    Hypothesis                 NVARCHAR(2000)    NOT NULL,
    Confidence0To100           INT               NOT NULL CHECK (Confidence0To100 BETWEEN 0 AND 100),
    Recommendation             NVARCHAR(2000),
    TimeWindowSeconds          INT               NOT NULL DEFAULT 120,
    CONSTRAINT PK_CorrelationRules PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT UK_CorrelationRules_Code UNIQUE NONCLUSTERED (Code)
);

IF OBJECT_ID('dbo.CorrelationConditions', 'U') IS NULL
CREATE TABLE dbo.CorrelationConditions (
    Id                          UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    CorrelationRuleId          UNIQUEIDENTIFIER NOT NULL,
    SignalSourceId             NVARCHAR(100)     NOT NULL,  /* CodeSignature OU CodeMesure */
    Description                NVARCHAR(500),
    IsNegation                 BIT               NOT NULL DEFAULT 0,
    CONSTRAINT PK_CorrelationConditions PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT FK_Conditions_Rules FOREIGN KEY (CorrelationRuleId)
        REFERENCES dbo.CorrelationRules(Id) ON DELETE CASCADE
);

/* SESSIONS DIAGNOSTIC */
IF OBJECT_ID('dbo.Sessions', 'U') IS NULL
CREATE TABLE dbo.Sessions (
    Id                          UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    EnvironmentId               UNIQUEIDENTIFIER NOT NULL,
    FrozenEnvironmentCode    NVARCHAR(20)      NOT NULL,
    Title                      NVARCHAR(200)     NOT NULL,
    MotiveGoal                NVARCHAR(2000),
    ReferenceTicket           NVARCHAR(50),
    RequestedBy               NVARCHAR(150),
    AnalysisWindowStart      DATETIME2(3),
    AnalysisWindowEnd        DATETIME2(3),
    FinalVerdict              INT NULL,        /* 7 niveaux précis : Confirmee..Insuffisantes */
    VerdictExplanation        NVARCHAR(4000),
    AnalysisPerformedAt       DATETIME2(3),
    SourceAlertId             UNIQUEIDENTIFIER NULL,
    IsReferenceHealthyPeriod  BIT               NOT NULL DEFAULT 0,
    HealthyReferenceSessionId UNIQUEIDENTIFIER NULL,
    ReferenceMode             INT NULL,        /* 0=Healthy/1=ExecOK/2=Habituelles/3=NoeudPair */
    ReferenceOkExecutionId    UNIQUEIDENTIFIER NULL,
    ReferenceComponentId      UNIQUEIDENTIFIER NULL,
    Current8Phase             INT               NOT NULL DEFAULT 0,   /* 1-8 phases */
    EscalatedTo               NVARCHAR(150),
    EscalationPerformedAt     DATETIME2(3),
    EscalationRequestedBy     NVARCHAR(150),
    CONSTRAINT PK_Sessions PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT FK_Diag_Environments FOREIGN KEY (EnvironmentId)
        REFERENCES dbo.Environments(Id),
    CONSTRAINT FK_Diag_Alerts FOREIGN KEY (SourceAlertId)
        REFERENCES dbo.Alerts(Id) ON DELETE SET NULL,
    CONSTRAINT FK_Diag_SelfHealthy FOREIGN KEY (HealthyReferenceSessionId)
        REFERENCES dbo.Sessions(Id),
    CONSTRAINT FK_Diag_ExecOK FOREIGN KEY (ReferenceOkExecutionId)
        REFERENCES dbo.Executions(Id) ON DELETE SET NULL,
    CONSTRAINT FK_Diag_Component FOREIGN KEY (ReferenceComponentId)
        REFERENCES dbo.Components(Id) ON DELETE SET NULL
);
CREATE NONCLUSTERED INDEX IX_Sessions_EnvPhase ON dbo.Sessions(EnvironmentId, Current8Phase);
CREATE NONCLUSTERED INDEX IX_Sessions_Ticket ON dbo.Sessions(ReferenceTicket);

/* TRANSITIONS DE PHASES 8-PHASES (cycle non linéaire — tous passages loggés) */
IF OBJECT_ID('dbo.PhaseTransitions', 'U') IS NULL
CREATE TABLE dbo.PhaseTransitions (
    Id                          UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    DiagnosticSessionId        UNIQUEIDENTIFIER NOT NULL,
    Phase                      INT               NOT NULL,
    EnteredBy                 NVARCHAR(150)     NOT NULL,
    EnteredAt                 DATETIME2(3)      NOT NULL DEFAULT SYSUTCDATETIME(),
    Justification              NVARCHAR(2000),
    CONSTRAINT PK_PhaseTransitions PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT FK_PhT_Sessions FOREIGN KEY (DiagnosticSessionId)
        REFERENCES dbo.Sessions(Id) ON DELETE CASCADE
);

/* SOURCES JOURNAUX ANALYSÉS (collecte ciblée ou import manuel) */
IF OBJECT_ID('dbo.Sources', 'U') IS NULL
CREATE TABLE dbo.Sources (
    Id                          UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    DiagnosticSessionId        UNIQUEIDENTIFIER NOT NULL,
    IntendedComponentId        UNIQUEIDENTIFIER NULL,
    FrozenIntendedComponentName NVARCHAR(200),
    FrozenIntendedComponentRole INT NULL,
    FrozenHostName            NVARCHAR(150),
    AutoIdentifiedComponent    BIT               NOT NULL DEFAULT 0,
    DetectedVersionN4         NVARCHAR(50),
    DetectedFormat            NVARCHAR(50),
    DetectedTimeZone          NVARCHAR(50),
    SuggestedComponentId      UNIQUEIDENTIFIER NULL,
    SuggestedComponentName    NVARCHAR(200),
    IdentificationEvidence   NVARCHAR(2000),
    MultipleCandidates       BIT               NOT NULL DEFAULT 0,
    OperationCorrelationId   NVARCHAR(20),
    CollectionOrigin          INT               NOT NULL DEFAULT 0, /* 0=Collecte/1=ImportManuel */
    ActualReadPath           NVARCHAR(300),
    FileName                  NVARCHAR(200),
    SizeBytes                 BIGINT,
    TotalLines                BIGINT,
    Sha256OfMaskedContent    NVARCHAR(64),       /* intégrité */
    FirstLineTimestamp       DATETIME2(3),
    LastLineTimestamp        DATETIME2(3),
    LinesInfo                 BIGINT,
    LinesWarn                 BIGINT,
    LinesError                BIGINT,
    SourceClockOffsetSec     DECIMAL(12,3),
    SecretsMaskedCount        INT,
    IsTailOnlyTruncated       BIT               NOT NULL DEFAULT 0,
    CollectionErrorMessage   NVARCHAR(2000),
    FailureTaxonomy           INT NULL,  /* 0=AccesRefuse..5=NonConfiguré */
    Success                   BIT               NOT NULL,
    CONSTRAINT PK_Sources PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT FK_Src_Sessions FOREIGN KEY (DiagnosticSessionId)
        REFERENCES dbo.Sessions(Id) ON DELETE CASCADE,
    CONSTRAINT FK_Src_IntendedComponent FOREIGN KEY (IntendedComponentId)
        REFERENCES dbo.Components(Id),
    CONSTRAINT FK_Src_SuggComponent FOREIGN KEY (SuggestedComponentId)
        REFERENCES dbo.Components(Id)
);

/* CONSTATS ANOMALIES DANS LES JOURNAUX (reconnus par signature ?) */
IF OBJECT_ID('dbo.Findings', 'U') IS NULL
CREATE TABLE dbo.Findings (
    Id                          UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    DiagnosticSessionId        UNIQUEIDENTIFIER NOT NULL,
    LogSourceId               UNIQUEIDENTIFIER NOT NULL,
    RecognizedSignatureId     UNIQUEIDENTIFIER NULL,
    FrozenSignatureCode      NVARCHAR(50),
    Domain                     INT               NOT NULL,
    Severity                   INT               NOT NULL,
    Title                      NVARCHAR(200)     NOT NULL,
    MaskedRepresentativeLine  NVARCHAR(500),
    MaskedContextLines        NVARCHAR(4000),
    OccurrencesCount          BIGINT            NOT NULL DEFAULT 1,
    FirstObservedAt           DATETIME2(3),
    LastObservedAt            DATETIME2(3),
    FirstLineNumber           BIGINT,
    CopiedMeaning            NVARCHAR(2000),
    CopiedRemediation        NVARCHAR(2000),
    CopiedDocReference       NVARCHAR(500),
    ExtractedThreadId        NVARCHAR(200),
    ExtractedLoggerClass     NVARCHAR(200),
    TransactionId            NVARCHAR(100),
    LogLevel                 NVARCHAR(10),
    ExtractedErrorCode       NVARCHAR(50),
    CONSTRAINT PK_Findings PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT FK_Find_Sessions FOREIGN KEY (DiagnosticSessionId)
        REFERENCES dbo.Sessions(Id) ON DELETE CASCADE,
    CONSTRAINT FK_Find_Sources FOREIGN KEY (LogSourceId)
        REFERENCES dbo.Sources(Id),
    CONSTRAINT FK_Find_Signatures FOREIGN KEY (RecognizedSignatureId)
        REFERENCES dbo.Signatures(Id) ON DELETE SET NULL
);
CREATE NONCLUSTERED INDEX IX_Find_Session ON dbo.Findings(DiagnosticSessionId, Severity, FirstObservedAt);

/* HYPOTHÈSES DIAGNOSTIC */
IF OBJECT_ID('dbo.Hypotheses', 'U') IS NULL
CREATE TABLE dbo.Hypotheses (
    Id                          UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    DiagnosticSessionId        UNIQUEIDENTIFIER NOT NULL,
    Domain                     INT               NOT NULL,
    Statement                 NVARCHAR(2000)    NOT NULL,
    Confidence0To100           INT               NOT NULL CHECK (Confidence0To100 BETWEEN 0 AND 100),
    EvidenceElements          NVARCHAR(4000),
    CounterEvidenceElements   NVARCHAR(2000),
    LastObservedEvidenceAt    DATETIME2(3),
    StrongestRuleVersionRef  NVARCHAR(50),
    RecommendedAction        NVARCHAR(2000),
    DisplayRank                INT               NOT NULL,
    CONSTRAINT PK_Hypotheses PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT FK_Hyp_Sessions FOREIGN KEY (DiagnosticSessionId)
        REFERENCES dbo.Sessions(Id) ON DELETE CASCADE
);

/* PARAMÉTRAGES DIAGNOSTIC SEUILS */
IF OBJECT_ID('dbo.DiagnosticSettings', 'U') IS NULL
CREATE TABLE dbo.DiagnosticSettings (
    Id                          UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    EnvironmentId               UNIQUEIDENTIFIER NOT NULL,
    HypothesisAcceptancePct    INT               NOT NULL DEFAULT 65,
    MaxHypotheses             INT               NOT NULL DEFAULT 5,
    DefaultAnalysisWindowHours INT               NOT NULL DEFAULT 4,
    ClusterDisconnectedAlertSec INT             NOT NULL DEFAULT 120,
    EdiMaxDelayHours            INT               NOT NULL DEFAULT 2,
    ClockSkewAlertSec           DECIMAL(6,3)     NOT NULL DEFAULT 1.000,
    CONSTRAINT PK_DiagnosticSettings PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT UK_DiagSettings_Env UNIQUE NONCLUSTERED (EnvironmentId),
    CONSTRAINT FK_DiagSettings_Environments FOREIGN KEY (EnvironmentId)
        REFERENCES dbo.Environments(Id) ON DELETE CASCADE
);

/* -------------------------------------------------------------------------
   07. BASE DOCUMENTAIRE (PDF indexés + Feedbacks Q&R)
   ------------------------------------------------------------------------- */

IF OBJECT_ID('dbo.Documents', 'U') IS NULL
CREATE TABLE dbo.Documents (
    Id                          UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    Title                      NVARCHAR(300)     NOT NULL,
    Category                   INT               NOT NULL, /* GuideEditeur/Procedure/PostMortem/NoteVersion/SOP */
    VersionLabel              NVARCHAR(50),
    Publisher                 NVARCHAR(200),
    PublicationDate           DATE,
    OriginalFilePath          NVARCHAR(400),
    LifecycleStatus           INT               NOT NULL DEFAULT 3, /* Actif */
    IsIndexedForRag           BIT               NOT NULL DEFAULT 0,
    SizeMB                    DECIMAL(8,2),
    PageCount                  INT,
    CONSTRAINT PK_Documents PRIMARY KEY CLUSTERED (Id)
);
CREATE FULLTEXT CATALOG N4SentinelFullText AS DEFAULT;  /* recommandé pour recherche doc */

IF OBJECT_ID('dbo.DocumentSections', 'U') IS NULL
CREATE TABLE dbo.DocumentSections (
    Id                          UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    DocumentId                 UNIQUEIDENTIFIER NOT NULL,
    SectionReference          NVARCHAR(200)     NOT NULL,    /* §2.1.4.8 p.847 */
    Heading                   NVARCHAR(500),
    PageStart                  INT,
    PageEnd                    INT,
    ContentPlainText          NVARCHAR(MAX),     /* extraction texte */
    EmbeddingVector           VARBINARY(MAX),   /* vecteur RAG */
    CONSTRAINT PK_DocumentSections PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT FK_DocSections_Documents FOREIGN KEY (DocumentId)
        REFERENCES dbo.Documents(Id) ON DELETE CASCADE
);
CREATE FULLTEXT INDEX ON dbo.DocumentSections(ContentPlainText) KEY INDEX PK_DocumentSections;

IF OBJECT_ID('dbo.KnowledgeFeedback', 'U') IS NULL
CREATE TABLE dbo.KnowledgeFeedback (
    Id                          UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    DocumentId                 UNIQUEIDENTIFIER NOT NULL,
    SectionPageRef            NVARCHAR(200)     NOT NULL,
    UserQuestion              NVARCHAR(500)     NOT NULL,
    SystemAnswer              NVARCHAR(MAX)      NOT NULL,
    IsHelpful                  BIT,
    FeedbackBy                NVARCHAR(150),
    FeedbackAt                DATETIME2(3),
    UserComment               NVARCHAR(2000),
    CONSTRAINT PK_KnowledgeFeedback PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT FK_FB_Documents FOREIGN KEY (DocumentId)
        REFERENCES dbo.Documents(Id) ON DELETE CASCADE
);

/* -------------------------------------------------------------------------
   08. SOP MODÈLE + EXÉCUTION (100% humain, JAMAIS auto)
   ------------------------------------------------------------------------- */

IF OBJECT_ID('dbo.Sops', 'U') IS NULL
CREATE TABLE dbo.Sops (
    Id                          UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    EnvironmentId               UNIQUEIDENTIFIER NULL,   /* NULL=multi-envs générique */
    StableCode                NVARCHAR(30)      NOT NULL,
    Title                      NVARCHAR(300)     NOT NULL,
    Version                    INT               NOT NULL DEFAULT 1,
    LifecycleStatus           INT               NOT NULL DEFAULT 0, /* Brouillon→Actif */
    HasBeenExecutedOnce       BIT               NOT NULL DEFAULT 0,
    RequiresElevatedRole      BIT               NOT NULL DEFAULT 0,
    Objective                 NVARCHAR(2000),
    Perimeter                NVARCHAR(2000),
    Prerequisites            NVARCHAR(4000),
    Risks                    NVARCHAR(4000),
    PostChecks               NVARCHAR(4000),
    ExpectedResult           NVARCHAR(2000),
    RollbackPlan             NVARCHAR(4000),
    EscalationPath           NVARCHAR(2000),
    AppliesToN4Version       NVARCHAR(50),
    GeneratedFromDocumentId  UNIQUEIDENTIFIER NULL,
    GeneratedFromExecutionId UNIQUEIDENTIFIER NULL,
    GeneratedFromSessionId   UNIQUEIDENTIFIER NULL,
    CONSTRAINT PK_Sops PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT UK_Sop_CodeVersion UNIQUE NONCLUSTERED (StableCode, Version),
    CONSTRAINT FK_Sop_Envs FOREIGN KEY (EnvironmentId)
        REFERENCES dbo.Environments(Id) ON DELETE SET NULL,
    CONSTRAINT FK_Sop_Doc FOREIGN KEY (GeneratedFromDocumentId)
        REFERENCES dbo.Documents(Id) ON DELETE SET NULL,
    CONSTRAINT FK_Sop_Exec FOREIGN KEY (GeneratedFromExecutionId)
        REFERENCES dbo.Executions(Id) ON DELETE SET NULL,
    CONSTRAINT FK_Sop_Sess FOREIGN KEY (GeneratedFromSessionId)
        REFERENCES dbo.Sessions(Id) ON DELETE SET NULL
);

IF OBJECT_ID('dbo.SopSteps', 'U') IS NULL
CREATE TABLE dbo.SopSteps (
    Id                          UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    SopId                      UNIQUEIDENTIFIER NOT NULL,
    Order                      INT               NOT NULL,
    Name                       NVARCHAR(200)     NOT NULL,
    Instruction               NVARCHAR(2000),
    ExpectedOutcome           NVARCHAR(1000),
    TargetComponentId          UNIQUEIDENTIFIER NULL,
    FrozenComponentName       NVARCHAR(200),
    IsSkippable                BIT               NOT NULL DEFAULT 1,
    CONSTRAINT PK_SopSteps PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT FK_SopSteps_Sops FOREIGN KEY (SopId)
        REFERENCES dbo.Sops(Id) ON DELETE CASCADE,
    CONSTRAINT FK_SopSteps_Components FOREIGN KEY (TargetComponentId)
        REFERENCES dbo.Components(Id) ON DELETE SET NULL,
    CONSTRAINT UK_SopSteps_UniqueOrder UNIQUE NONCLUSTERED (SopId, [Order])
);

/* ASSOCIATIONS SOP → Composant, Signature, SessionDiag */
IF OBJECT_ID('dbo.SopAssociations', 'U') IS NULL
CREATE TABLE dbo.SopAssociations (
    Id                          UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    SopId                      UNIQUEIDENTIFIER NOT NULL,
    Nature                     INT               NOT NULL,  /* 0=Composant/1=Signature/2=SessionDiag */
    ComponentId               UNIQUEIDENTIFIER NULL,
    SuggestedRole             INT NULL,
    SignatureId               UNIQUEIDENTIFIER NULL,
    SignatureCodeRef         NVARCHAR(50),
    DiagnosticSessionId      UNIQUEIDENTIFIER NULL,
    CONSTRAINT PK_SopAssociations PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT FK_SopAssoc_Sops FOREIGN KEY (SopId)
        REFERENCES dbo.Sops(Id) ON DELETE CASCADE,
    CONSTRAINT CK_SopAssoc_Consistency CHECK (
        (Nature = 0 AND ComponentId IS NOT NULL) OR
        (Nature = 1 AND SignatureId IS NOT NULL) OR
        (Nature = 2 AND DiagnosticSessionId IS NOT NULL)
    )
);

IF OBJECT_ID('dbo.SopExecutions', 'U') IS NULL
CREATE TABLE dbo.SopExecutions (
    Id                          UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    SopId                      UNIQUEIDENTIFIER NOT NULL,
    FrozenSopVersion          INT               NOT NULL,
    FrozenSopCode            NVARCHAR(30)      NOT NULL,
    FrozenSopTitle           NVARCHAR(300)     NOT NULL,
    EnvironmentId              UNIQUEIDENTIFIER NOT NULL,
    FrozenEnvironmentCode    NVARCHAR(20)      NOT NULL,
    Status                     INT               NOT NULL DEFAULT 0,  /* 0=EnCours/1=Terminé/2=Abandonné */
    ExecutedBy                NVARCHAR(150)     NOT NULL,
    ExecutionReason          NVARCHAR(1000),
    ReferenceTicket           NVARCHAR(50),
    TriggeredFromAlertId     UNIQUEIDENTIFIER NULL,
    TriggeredFromSessionId   UNIQUEIDENTIFIER NULL,
    StartedAt                 DATETIME2(3)      NOT NULL DEFAULT SYSUTCDATETIME(),
    FinishedAt                DATETIME2(3),
    AbandonReason             NVARCHAR(2000),
    FinalResultAttestation    INT NULL,        /* NULL/Resolu/NonResolu/Indeterminé — HumaIN SEUL */
    ResultNote               NVARCHAR(2000),
    ResultAttestedBy         NVARCHAR(150),
    ResultAttestedAt         DATETIME2(3),
    CorrelationId            NVARCHAR(20),
    HistoricalSuccessRateAtLaunchPct DECIMAL(7,2), /* info lancement */
    CONSTRAINT PK_SopExecutions PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT FK_SopEx_Sops FOREIGN KEY (SopId)
        REFERENCES dbo.Sops(Id),
    CONSTRAINT FK_SopEx_Envs FOREIGN KEY (EnvironmentId)
        REFERENCES dbo.Environments(Id),
    CONSTRAINT FK_SopEx_Alert FOREIGN KEY (TriggeredFromAlertId)
        REFERENCES dbo.Alerts(Id) ON DELETE SET NULL,
    CONSTRAINT FK_SopEx_Sess FOREIGN KEY (TriggeredFromSessionId)
        REFERENCES dbo.Sessions(Id) ON DELETE SET NULL
);

IF OBJECT_ID('dbo.SopExecutionSteps', 'U') IS NULL
CREATE TABLE dbo.SopExecutionSteps (
    Id                          UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    SopExecutionId             UNIQUEIDENTIFIER NOT NULL,
    Order                      INT               NOT NULL,
    Name                       NVARCHAR(200)     NOT NULL,
    FrozenInstruction         NVARCHAR(2000),
    FrozenExpectedOutcome     NVARCHAR(1000),
    TargetComponentId          UNIQUEIDENTIFIER NULL,
    FrozenComponentName       NVARCHAR(200),
    Status                     INT               NOT NULL DEFAULT 0,  /* AVenir..Annulée */
    StartedAt                 DATETIME2(3),
    FinishedAt                DATETIME2(3),
    FrozenIsSkippable         BIT               NOT NULL DEFAULT 1,
    ConfirmedBy               NVARCHAR(150),   /* HUMAIN SEUL */
    ConfirmedAt               DATETIME2(3),   /* HUMAIN SEUL */
    OperatorProof             NVARCHAR(4000), /* HUMAIN SEUL */
    DeviationNote             NVARCHAR(2000),
    SkippedBy                 NVARCHAR(150),
    SkipReason                NVARCHAR(1000),
    ReOpeningsHistory         NVARCHAR(MAX),   /* JSON */
    CONSTRAINT PK_SopExecutionSteps PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT FK_SopExSteps_Ex FOREIGN KEY (SopExecutionId)
        REFERENCES dbo.SopExecutions(Id) ON DELETE CASCADE,
    CONSTRAINT FK_SopExSteps_Cmp FOREIGN KEY (TargetComponentId)
        REFERENCES dbo.Components(Id) ON DELETE SET NULL,
    CONSTRAINT UK_SopExSteps_Order UNIQUE NONCLUSTERED (SopExecutionId, [Order])
);

/* -------------------------------------------------------------------------
   09. DOSSIERS PARTAGÉS SANTE + SUIVI EDI
   ------------------------------------------------------------------------- */

IF OBJECT_ID('dbo.SharedFolderSnapshots', 'U') IS NULL
CREATE TABLE dbo.SharedFolderSnapshots (
    Id                          UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    ComponentId                 UNIQUEIDENTIFIER NOT NULL,
    MeasuredAt                 DATETIME2(3)      NOT NULL DEFAULT SYSUTCDATETIME(),
    PendingCount               BIGINT            NOT NULL DEFAULT 0,
    ConsumedCount              BIGINT            NOT NULL DEFAULT 0,
    BlockedCount               BIGINT            NOT NULL DEFAULT 0,
    ErrorCount                 BIGINT            NOT NULL DEFAULT 0,
    OldestPendingFileAgeSec    BIGINT,
    WriteProbeLatencyMs        INT,
    GrowthGBPerHour            DECIMAL(12,2),
    SuspiciousCorruption      BIT               NOT NULL DEFAULT 0,
    SuspiciousLockFiles       BIT               NOT NULL DEFAULT 0,
    KahaDbDoubleLockFiles     BIT               NOT NULL DEFAULT 0,   /* SPLIT BRAIN */
    CONSTRAINT PK_SFShapshot PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT FK_SFS_Components FOREIGN KEY (ComponentId)
        REFERENCES dbo.Components(Id) ON DELETE CASCADE
);

IF OBJECT_ID('dbo.EdiFiles', 'U') IS NULL
CREATE TABLE dbo.EdiFiles (
    Id                          UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    EnvironmentId               UNIQUEIDENTIFIER NOT NULL,
    SourceFolderComponentId    UNIQUEIDENTIFIER NULL,
    FileName                   NVARCHAR(255)     NOT NULL,
    PartnerName               NVARCHAR(200),
    EdiMessageType            NVARCHAR(100),
    DetectedOnFolderAt        DATETIME2(3)      NOT NULL,
    IntegratedInDBAt          DATETIME2(3),
    Status                     INT               NOT NULL DEFAULT 0, /* 0=Reçu..5=ErreurTraitement */
    IntegrationAttempts       INT               NOT NULL DEFAULT 0,
    LastErrorMessage          NVARCHAR(4000),
    MatchedSignatureId        UNIQUEIDENTIFIER NULL,
    OpenedDiagnosticSessionId UNIQUEIDENTIFIER NULL,
    FinalFilePathAfter       NVARCHAR(300),
    CONSTRAINT PK_EdiFiles PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT FK_Edi_Envs FOREIGN KEY (EnvironmentId)
        REFERENCES dbo.Environments(Id) ON DELETE CASCADE,
    CONSTRAINT FK_Edi_Cmp FOREIGN KEY (SourceFolderComponentId)
        REFERENCES dbo.Components(Id) ON DELETE SET NULL,
    CONSTRAINT FK_Edi_Sig FOREIGN KEY (MatchedSignatureId)
        REFERENCES dbo.Signatures(Id) ON DELETE SET NULL
);
CREATE NONCLUSTERED INDEX IX_Edi_EnvStatus ON dbo.EdiFiles(EnvironmentId, Status, DetectedOnFolderAt);

/* -------------------------------------------------------------------------
   10. RÉTENTIONS + ACTIONS EXTÉRIEURES DÉCLARÉES
   ------------------------------------------------------------------------- */

IF OBJECT_ID('dbo.RetentionPolicies', 'U') IS NULL
CREATE TABLE dbo.RetentionPolicies (
    Id                          UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    DataCategoryCode          NVARCHAR(50)      NOT NULL,
    Label                     NVARCHAR(200)     NOT NULL,
    RetentionDays              INT               NOT NULL,
    EndOfLifeAction           INT               NOT NULL DEFAULT 0,  /* 0=Supp/1=Archivage/2=Anonymisation */
    ArchivePathIfApplicable   NVARCHAR(400),
    IsEnabled                  BIT               NOT NULL DEFAULT 1,
    CONSTRAINT PK_RetentionPolicies PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT UK_Retention_Code UNIQUE NONCLUSTERED (DataCategoryCode)
);

/* HISTORIQUE ACTIONS EFFECTUÉES HORS SENTINEL (ex: reboot VM vCenter) */
IF OBJECT_ID('dbo.ExternalActionDeclarations', 'U') IS NULL
CREATE TABLE dbo.ExternalActionDeclarations (
    Id                          UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    SupportKind               INT               NOT NULL,  /* 0=Exec/1=Diag/2=StepBloquée/3=SOP */
    PrimaryEntityId           UNIQUEIDENTIFIER NOT NULL,
    SecondaryEntityId         UNIQUEIDENTIFIER NULL,
    Description               NVARCHAR(2000)    NOT NULL,
    DeclaredBy                NVARCHAR(150)     NOT NULL,
    DeclaredAt                DATETIME2(3)      NOT NULL DEFAULT SYSUTCDATETIME(),
    EvidenceObserved          NVARCHAR(4000),
    ImpactOnCurrentState     NVARCHAR(2000),
    CorrelationId            NVARCHAR(20),
    CONSTRAINT PK_ExternalActionDeclarations PRIMARY KEY CLUSTERED (Id)
);
CREATE NONCLUSTERED INDEX IX_ExtAct_Primary ON dbo.ExternalActionDeclarations(PrimaryEntityId, SupportKind);

/* -------------------------------------------------------------------------
   11. AUDIT — APPEND ONLY (trigger bloque UPDATE/DELETE) + AMÉLIORATIONS CAPITALISATION
   ------------------------------------------------------------------------- */

IF OBJECT_ID('dbo.AuditEntries', 'U') IS NULL
CREATE TABLE dbo.AuditEntries (
    Id                          UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    EventAt                   DATETIME2(3)      NOT NULL DEFAULT SYSUTCDATETIME(),
    ActionKind                INT               NOT NULL,  /* 0=Connexion..11=Contournement..12=StopForce */
    Outcome                   INT               NOT NULL,  /* 0=Succès/1=Refus/2=Échec */
    EntityCategory           NVARCHAR(100),
    ConcernedEntityId        NVARCHAR(100),
    ActorIdentityLabel      NVARCHAR(255)     NOT NULL,  /* Compte nominatif */
    ClientIpAddr             NVARCHAR(50),
    HttpMethodOrOperation   NVARCHAR(20),
    ResourcePathOrOperation NVARCHAR(500),
    SnapshotBeforeJson       NVARCHAR(MAX),      /* CHIFFRÉ / secrets masqués */
    SnapshotAfterJson        NVARCHAR(MAX),
    HumanReadableMessage     NVARCHAR(1000),
    CorrelationId            NVARCHAR(20),
    ConcernedEnvironmentId   UNIQUEIDENTIFIER NULL,
    ConcernedEnvironmentCode NVARCHAR(20),
    CONSTRAINT PK_AuditEntries PRIMARY KEY CLUSTERED (Id)
);
CREATE CLUSTERED INDEX /* on remplace par non clustered si colonnar : */
    /* CCI_AuditEntries CLUSTERED COLUMNSTORE */
    /* Index temporel : */
CREATE NONCLUSTERED INDEX IX_Audit_Temps ON dbo.AuditEntries(EventAt DESC);
CREATE NONCLUSTERED INDEX IX_Audit_Acteur ON dbo.AuditEntries(ActorIdentityLabel, EventAt DESC);
CREATE NONCLUSTERED INDEX IX_Audit_Entite ON dbo.AuditEntries(EntityCategory, ConcernedEntityId);

/* -------------------------------------------------------------------------
   TRIGGER EMPÊCHANT MODIFICATION / SUPPRESSION AUDIT (append-only — SEC-008)
   ------------------------------------------------------------------------- */
GO
CREATE OR ALTER TRIGGER dbo.TR_AuditEntries_NoModifyNoDelete
ON dbo.AuditEntries
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT * FROM inserted) OR EXISTS (SELECT * FROM deleted)
    BEGIN
        RAISERROR(N'RG-0805 / SEC-008 : JOURNAL AUDIT = APPEND ONLY. Toute modification / suppression est interdite.', 16, 1);
        ROLLBACK TRANSACTION;
    END
END;
GO

/* -------------------------------------------------------------------------
   11b. — AMÉLIORATIONS CAPITALISATION INCIDENTS + MÉTRIQUES BUSINESS — NOUVEAUX
   ------------------------------------------------------------------------- */

IF OBJECT_ID('dbo.CapitalizedIncidents', 'U') IS NULL
CREATE TABLE dbo.CapitalizedIncidents (
    Id                          UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    ExternalReference         NVARCHAR(50)      NOT NULL,  /* P1-INC-2026-0912-0012 */
    EnvironmentId               UNIQUEIDENTIFIER NOT NULL,
    DeclaredSeverity          INT               NOT NULL,   /* 0=P1..3=P4 */
    InitialSymptom            NVARCHAR(2000),
    RealIncidentStartedAt     DATETIME2(3)      NOT NULL,
    RealIncidentEndedAt       DATETIME2(3),
    TotalDurationMin          BIGINT,          /* MTTR */
    DetectedBySentinelAt      DATETIME2(3),
    MainRootCauseCode        NVARCHAR(50),    /* CauseP1 #5 Horloges, etc. */
    MainRootCauseLabel       NVARCHAR(500),
    FiveWhysRootCause         NVARCHAR(MAX),    /* 5 Pourquoi */
    CorrectiveActionsTaken   NVARCHAR(MAX),
    LinkedDiagnosticSessionId UNIQUEIDENTIFIER NULL,
    LinkedExecutionId        UNIQUEIDENTIFIER NULL,
    SOPsUsedListString       NVARCHAR(1000),
    GeneratedSOPId           UNIQUEIDENTIFIER NULL,
    QuantifiedBusinessImpact NVARCHAR(2000),
    CompensatoryMeasures     NVARCHAR(4000),
    OperatorsInvolvedCount   INT,
    ResolutionSkillLevel     INT NULL,         /* 0=N1,1=N2,2=N3,3=ExpertNavis */
    ClosedBy                 NVARCHAR(150),
    ClosedAt                 DATETIME2(3),
    PostMortemDocumentRef   NVARCHAR(500),
    LessonsAndCorrectives   NVARCHAR(MAX),
    CONSTRAINT PK_CapitalizedIncidents PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT UK_Incidents_Ref UNIQUE NONCLUSTERED (ExternalReference),
    CONSTRAINT FK_Incidents_Envs FOREIGN KEY (EnvironmentId)
        REFERENCES dbo.Environments(Id),
    CONSTRAINT FK_Incidents_Sess FOREIGN KEY (LinkedDiagnosticSessionId)
        REFERENCES dbo.Sessions(Id) ON DELETE SET NULL,
    CONSTRAINT FK_Incidents_Exec FOREIGN KEY (LinkedExecutionId)
        REFERENCES dbo.Executions(Id) ON DELETE SET NULL,
    CONSTRAINT FK_Incidents_Sop FOREIGN KEY (GeneratedSOPId)
        REFERENCES dbo.Sops(Id) ON DELETE SET NULL
);
CREATE NONCLUSTERED INDEX IX_Incidents_EnvGravStart
    ON dbo.CapitalizedIncidents(EnvironmentId, DeclaredSeverity, RealIncidentStartedAt);

IF OBJECT_ID('dbo.BusinessSuccessMetrics', 'U') IS NULL
CREATE TABLE dbo.BusinessSuccessMetrics (
    Id                          UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    ReportingPeriod           NVARCHAR(10)      NOT NULL,  /* Mois-2026-09 / Sem-37 */
    EnvironmentId               UNIQUEIDENTIFIER NULL,    /* NULL = Global */
    MetricCode                NVARCHAR(50)      NOT NULL,
    MetricLabel               NVARCHAR(200)     NOT NULL,
    NumericValueAvgOrSum     DECIMAL(18,4),
    TargetFixedValue         DECIMAL(18,4),
    BaselineBeforeSentinel    DECIMAL(18,4),
    TrendVsPreviousPeriodPct DECIMAL(8,2),
    AggregationKind           INT               NOT NULL, /* 0=Moyenne/1=Somme/2=Médiane/3=Pourcentage */
    CalculatedAt              DATETIME2(3)      NOT NULL DEFAULT SYSUTCDATETIME(),
    SampleSizeCount           BIGINT,
    CONSTRAINT PK_BusinessSuccessMetrics PRIMARY KEY CLUSTERED (Id),
    UK_Metrics UNIQUE NONCLUSTERED (ReportingPeriod, MetricCode, EnvironmentId),
    CONSTRAINT FK_Metrics_Envs FOREIGN KEY (EnvironmentId)
        REFERENCES dbo.Environments(Id) ON DELETE SET NULL
);

/* -------------------------------------------------------------------------
   12. DONNÉES DE RÉFÉRENCE (SEED) — RÔLES RBAC + POLITIQUES RÉTENTION PAR DÉFAUT
   ------------------------------------------------------------------------- */

/* — Rôles ASP.NET Identity à créer via seed (nom conforme RG-0801) :
   Lecteur, OperateurN4, AdminN4, AdminInfra, Validateur, AdminSolution, Auditeur, TOSManager */

/* Politiques Rétention par défaut (SEC-009 / RG-0806) */
SET IDENTITY_INSERT OFF;
IF NOT EXISTS (SELECT * FROM dbo.RetentionPolicies WHERE DataCategoryCode = 'LOGS_TECH')
INSERT dbo.RetentionPolicies (Id,DataCategoryCode,Label,RetentionDays,EndOfLifeAction,ArchivePathIfApplicable,IsEnabled)
VALUES (NEWID(),'LOGS_TECH',N'Journaux techniques, analyses brutes, fichiers journaux importés',30,0,N'\\SRV-ARCH01\retention\n4sentinel\logs-tech',1);

IF NOT EXISTS (SELECT * FROM dbo.RetentionPolicies WHERE DataCategoryCode = 'RAPPORTS')
INSERT dbo.RetentionPolicies (Id,DataCategoryCode,Label,RetentionDays,EndOfLifeAction,ArchivePathIfApplicable,IsEnabled)
VALUES (NEWID(),'RAPPORTS',N'Rapports incidents, Post-Mortems PDF/Word, rapports exécutions workflow',365,1,N'\\SRV-ARCH01\retention\n4sentinel\rapports',1);

IF NOT EXISTS (SELECT * FROM dbo.RetentionPolicies WHERE DataCategoryCode = 'AUDITS')
INSERT dbo.RetentionPolicies (Id,DataCategoryCode,Label,RetentionDays,EndOfLifeAction,ArchivePathIfApplicable,IsEnabled)
VALUES (NEWID(),'AUDITS',N'Journal d''audit immuable (RG-0805 / SEC-008)',2555,1,N'\\SRV-ARCH01\retention\n4sentinel\audits',1);  /* 7 ANS */

IF NOT EXISTS (SELECT * FROM dbo.RetentionPolicies WHERE DataCategoryCode = 'SOP_WKF_VALID')
INSERT dbo.RetentionPolicies (Id,DataCategoryCode,Label,RetentionDays,EndOfLifeAction,ArchivePathIfApplicable,IsEnabled)
VALUES (NEWID(),'SOP_WKF_VALID',N'Workflows & SOP validés + historiques versions + habilitations',0,2,NULL,1); /* Permanent anonymisé */

IF NOT EXISTS (SELECT * FROM dbo.RetentionPolicies WHERE DataCategoryCode = 'SIGNAUX_COURT_TERME')
INSERT dbo.RetentionPolicies (Id,DataCategoryCode,Label,RetentionDays,EndOfLifeAction,ArchivePathIfApplicable,IsEnabled)
VALUES (NEWID(),'SIGNAUX_COURT_TERME',N'Mesures de supervision détaillées composant (Signal composant)',14,0,NULL,1);

/* Top 10 Signatures OFFICIELLES Navis 3.8.25 à insérer via seed (SIGDIAG) :
   - SIG-CLUST-HZ-JOIN   : Hazelcast join timeout
   - SIG-DB-ORA-12154    : TNS Oracle
   - SIG-DB-SQL-CONN     : Could not connect to SparcsN4 apex.xml
   - SIG-SPLIT-BRAIN    : Both Center + Standby role=ACTIVE
   - SIG-LOG4J-BOMBE     : Log4j DEBUG activé (détection explosion logs)
   - SIG-N4-1053-STOP    : Erreur 1053 Windows Service StopPending
   - SIG-READY-TOMCAT    : "Web tier servlet 'action' initialized"
   - SIG-SLOW-QUERY      : SLOW QUERY warning
   - SIG-MQ-KAHA-CORRUPT : KahaDB corruption detected
   - SIG-ECN4-PURPLE     : Purple System Bypass (rejet dispatch ECN4) */

/* =========================================================================
   FIN DU MPD
   ========================================================================= */
```

---

## Notes techniques MPD (rapprochement avec le code existant)

| Aspect | Détail |
|---|---|
| **Provider EF Core** | SQL Server (string de connexion dans [appsettings.json](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Web/appsettings.json#L5)) |
| **Migrate strategy** : les ~50 migrations existantes sont sources de vérité actuelles ; ce DDL **complète** les nouveautés MERISE : `ComponentAssociatedServices`, `WorkflowPreChecks`, `PreventiveRecommendedActions`, `CapitalizedIncidents`, `BusinessSuccessMetrics` |
| **Nom des tables** : alignés sur les noms `DbSet<T>` de [N4SentinelDbContext](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Infrastructure/Persistence/N4SentinelDbContext.cs#L22-L59) |
| **Contraintes RG-0105 (1 seul Center/Standby)** : implémentées via Index Filtre Unique SQL Server (WHERE Role = 1 / Role = 2) |
| **Contrainte Mutex actif unique** : `UX_EnvLock_ActiveOne` index conditionnel |
| **Append-only Audit** : Trigger SQL `TR_AuditEntries_NoModifyNoDelete` bloque UPDATE/DELETE |
| **JSON ReadyPatterns** : stockés via ValueConverter EF Core déjà dans le code (cf lignes 76-83 du DbContext) |
| **Secret masqués** : ne transitent JAMAIS en clair dans le MPD, tous les champs sensibles (login/MDP credentials) sont chiffrés par [CredentialStore](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Infrastructure/Security/CredentialStore.cs) avant écriture |
| **Row-Level-Security (optionnel V2)** : Policy sécurité par EnvironmentId pourra être ajoutée ultérieurement via `CREATE SECURITY POLICY` sur Environments pour cloisonner UAT vs Prod en multi-tenant. A ce stade, RBAC applicatif sépare les environnements. |

## Tables VOLUMINEUSES — Recommandations partitionnement

| Table | Taille attendue à 2 ans | Recommandation |
|---|---|---|
| `ComponentSignals` | 30M+ lignes (24 composants, 30 métriques, toutes les 10s) | Partitionnement par mois sur `MeasuredAt` + CLUSTERED COLUMNSTORE facultatif |
| `AuditEntries` | 5M-10M | Partitionnement annuel sur `EventAt` |
| `EdiFiles` | 1M-3M | Partitionnement trimestriel |
| `SharedFolderSnapshots` | 15M+ | Partitionnement mensuel |
| CapitalizedIncidents / Metrics | < 10 000 | Heap standard, pas de partition |
| `Sessions` + `Sources` + `Findings` | 50k à 500k (selon fréquence incidents) | Index secondaire adéquats suffisent |

---

## Fin du Modèle Physique de Données (MPD)
