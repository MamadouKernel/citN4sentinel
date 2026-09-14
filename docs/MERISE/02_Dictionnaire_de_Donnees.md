# Analyse MERISE — N4 Sentinel
## 02. Dictionnaire de Données (DD)

Conventions de nommage MERISE :
- Code entité `ENT`, code propriété `PRO_<ent>_<nom>`
- Types : `A(n)` = Alphanumérique, `N(n)` = Numérique, `D` = Date/heure, `B` = Booléen, `ID` = Identifiant unique (GUID)

---

### Catégorie : Référentiel — Environnements & Serveurs

| Code | Propriété | Type | Description | Valeurs / Exemple | RG |
|---|---|---|---|---|---|
| ENT_ENV | ENVIRONNEMENT | | | | RG-01 |
| PRO_ENV_ID | IdEnvironnement | ID | Clé primaire | GUID | |
| PRO_ENV_CODE | CodeEnvironnement | A(20) | Code unique court | PRD, UAT, TST, DEV, FRM | RG-0102 |
| PRO_ENV_NOM | NomEnvironnement | A(100) | Nom affiché | Production Abidjan | |
| PRO_ENV_TYPE | TypeEnvironnement | A(20) | Catégorie | Production / UAT / Test / Développement / Formation | RG-0102 |
| PRO_ENV_DESCR | Description | A(500) | | | |
| PRO_ENV_ADMIN | AdminResponsable | A(100) | Équipe propriétaire | Solutions IT | |
| PRO_ENV_URL | UrlBaseN4 | A(255) | URL de base du load balancer | https://n4prd.civ.ci | |
| PRO_ENV_DOSPART | CheminDossierPartage | A(255) | UNC du dossier partagé racine | \\\\SRV-NFS01\\n4share-prd\\ | RG-05 SPLIT-BRAIN |
| PRO_ENV_TIMEZONE | FuseauHoraire | A(50) | Référence horloge | Africa/Abidjan | RG-0204 |
| PRO_ENV_STATUT | StatutCycleVie | A(20) | Cycle validation | Brouillon / AValider / Valide / Actif / Désactivé | RG-0104 |
| PRO_ENV_CREATED | CreeLe | D | | | |
| PRO_ENV_UPDATED | ModifieLe | D | | | |

| Code | Propriété | Type | Description | Valeurs / Exemple | RG |
|---|---|---|---|---|---|
| ENT_SRV | SERVEUR | | | | RG-01 |
| PRO_SRV_ID | IdServeur | ID | | GUID | |
| PRO_SRV_NOM | NomHote | A(100) | FQDN ou NetBIOS | SRV-N4CL01P.civ.ci | |
| PRO_SRV_IP | AdresseIp | A(50) | IPv4/6 | 10.20.30.11 | |
| PRO_SRV_OS | SystemeExploitation | A(100) | Windows Server ... | Windows Server 2022 Datacenter | |
| PRO_SRV_ROLE | RoleApplicatif | A(100) | Rôle(s) hébergés | Cluster Node, Bridge Daemon | |
| PRO_SRV_ENV_ID | IdEnvironnement | ID | FK → ENVIRONNEMENT | | RG-0101 |
| PRO_SRV_CPUMON | CoeursCPUAlloues | N(4) | | 8 | |
| PRO_SRV_RAM | RAMGo | N(4) | | 32 | |
| PRO_SRV_DISQUE | DisqueGo | N(8) | Espace total disque système | 500 | |
| PRO_SRV_STATUT | EstActif | B | Supervision activée | | RG-0206 |

---

### Catégorie : Référentiel — Composants N4 & Dépendances

| Code | Propriété | Type | Description | Valeurs / Exemple | RG |
|---|---|---|---|---|---|
| ENT_CMP | COMPOSANT N4 | | | | [N4Component.cs](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/N4Component.cs) |
| PRO_CMP_ID | IdComposant | ID | | GUID | |
| PRO_CMP_ENV_ID | IdEnvironnement | ID | FK → ENVIRONNEMENT | | RG-0101 |
| PRO_CMP_SRV_ID | IdServeur | ID | FK → SERVEUR (nullable) | | |
| PRO_CMP_NOMLOG | NomLogique | A(100) | Nom affiché opérateur | Cluster Node 2 | |
| PRO_CMP_ROLE | RoleApplicatif | A(30) | Rôle N4 officiel | ClusterNode / CenterNode / StandbyCenterNode / BridgeDaemon / Xps / Ecn4 / Ecn4Web / BaseDeDonnees / ActiveMq / DossierPartage / InterfaceEdi / JobConditionnel / Autre | RG-0105 |
| PRO_CMP_SRVNOM | NomServiceWindows | A(200) | Nom exact du service piloté | Navis Node 2 | |
| PRO_CMP_PROCESS | NomProcessus | A(100) | Ex: tomcat9.exe | java.exe / tomcat10.exe | |
| PRO_CMP_ENDPOINT | EndpointSante | A(255) | URL health check | | RG-0201 |
| PRO_CMP_PORT | PortApplicatif | N(6) | 8080 / 61616... | | |
| PRO_CMP_CRIT | Criticite | A(15) | Niveau risque | Faible / Moyenne / Élevée / Critique | RG-0103 |
| PRO_CMP_CTRLMODE | ModeControle | A(20) | Pilotable / SuperviseSeulement / NonSupervise | | RG-0104 |
| PRO_CMP_STATUT | StatutCycleVie | A(20) | Brouillon→Actif | | RG-0104 |
| PRO_CMP_MAINT | ModeMaintenance | B | | | RG-0206 |
| PRO_CMP_MAINTNOTE | NoteMaintenance | A(500) | | | |
| PRO_CMP_MAINTJUSQUA | MaintenanceJusqua | D | | | RG-0206 |
| PRO_CMP_MAINTMOTIF | MotifMaintenance | A(500) | | | RG-0206 |
| PRO_CMP_MAINTPAR | MaintenancePar | A(100) | | | |
| PRO_CMP_ORDREDEM | OrdreDemarrage | N(4) | Rang dans la séquence | | RG-DÉMARRAGE-01 |
| PRO_CMP_DESCR | Description | A(500) | | | |
| PRO_CMP_OWNER | ResponsableTechnique | A(100) | Contact | | |

| Code | Propriété | Type | Description | Valeurs / Exemple | RG |
|---|---|---|---|---|---|
| ENT_RDY | PROFIL DE PREUVE (composant) | | | | [ReadinessProfile](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/N4Component.cs#L147-L248) |
| PRO_RDY_CMP_ID | IdComposant | ID | FK 1:1 → COMPOSANT | | |
| PRO_RDY_CHEMLOG | CheminJournal | A(300) | Chemin local serveur | C:\ProgramData\Navis\node2\logs\navis-apex.log | |
| PRO_RDY_READY | MotifsPrets (JSON) | A(4000) | RegEx fin d'initialisation | `.*Web tier servlet 'action' initialized.*` | RG-0201 |
| PRO_RDY_ERR | MotifsErreur (JSON) | A(4000) | RegEx échec immédiat | | RG-0201 |
| PRO_RDY_IGN | MotifsIgnore (JSON) | A(4000) | Erreurs bénignes connues | | |
| PRO_RDY_ACTIVE | MotifsRoleActif (JSON) | A(4000) | Pour Center/Standby | | RG-0203 |
| PRO_RDY_SYNC | MotifsSync (JSON) | A(4000) | N4-XPS sync OK | | RG-0205 |
| PRO_RDY_SYNCDEL | SeuilRetardSyncMin | N(4) | Défaut 15 min | 15 | RG-0205 |
| PRO_RDY_TOSRV | TimeoutServiceSec | N(5) | Défaut 300s | 300 | |
| PRO_RDY_TOLOG | TimeoutLogPretSec | N(5) | Défaut 1800s (30min) | 1800 | RG-0201 |
| PRO_RDY_TOSTOP | TimeoutStopSec | N(5) | Défaut 600s (10min) | 600 | |
| PRO_RDY_POLL | IntervalleSondageSec | N(4) | Défaut 10s | 10 | |
| PRO_RDY_PROGEVERY | ProgressionChaqueSec | N(4) | Défaut 60s | 60 | |
| PRO_RDY_POSTREADY | ObservationApresPretSec | N(5) | Stabilisation après Ready | 0 | |

| Code | Propriété | Type | Description | Valeurs / Exemple | RG |
|---|---|---|---|---|---|
| ENT_DEP | DEPENDANCE COMPOSANT | | | | [ComponentDependency](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/N4Component.cs#L318-L329) |
| PRO_DEP_ID | IdDependance | ID | | GUID | |
| PRO_DEP_CMP_ID | IdComposantSource | ID | FK → COMPOSANT | | RG-0106 |
| PRO_DEP_DEPCMP_ID | IdComposantDependu | ID | FK → COMPOSANT | | RG-0106 |
| PRO_DEP_TYPE | NatureDependance | A(30) | RequisAuDemarrage / RequisEnFonctionnement / Informative | | RG-0106 |
| PRO_DEP_NOTES | Notes | A(500) | | | |

| Code | Propriété | Type | Description | Valeurs / Exemple | RG |
|---|---|---|---|---|---|
| ENT_DOSP | PROFIL DOSSIER PARTAGÉ | | | | [SharedFolderProfile](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/N4Component.cs#L262-L310) |
| PRO_DOSP_CMP_ID | IdComposant | ID | FK 1:1 → COMPOSANT (rôle DossierPartage) | | |
| PRO_DOSP_RACINE | CheminRacine | A(300) | UNC ou local | \\\\SRV-NFS01\\n4share-prd\\amq\\ | |
| PRO_DOSP_CATEGORIE | Categorie | A(30) | ConfigurationN4 / ActiveMqKahaDb / EchangeEdi / Archives / DossiersErreur / Autre | | RG-0705 |
| PRO_DOSP_SOUSDOS_ATT | SousDossierAttente | A(200) | pending | | |
| PRO_DOSP_SOUSDOS_CONS | SousDossierConsomme | A(200) | consumed | | |
| PRO_DOSP_SOUSDOS_BLK | SousDossierBloque | A(200) | blocked | | |
| PRO_DOSP_SOUSDOS_ERR | SousDossierErreur | A(200) | error | | |
| PRO_DOSP_AGEATTMAX | AncienneteMaxAttenteHeure | N(5) | Seuil alerte ancienneté | 4 | RG-0603 |
| PRO_DOSP_LATMAX | LatenceEcritureMaxMs | N(8) | Test d'écriture | 500 | RG-0603 |
| PRO_DOSP_CROISSANCE | CroissanceMaxGoParHeure | N(8,2) | Seuil anormal | 5.00 | Amélioration RG-0906 |
| PRO_DOSP_MOTIFEDI | MotifNommageFichierEdi | A(500) | RegEx avec groupes partner/type | | RG-0705 |
| PRO_DOSP_DELAIDERNIEREINT | DureeMaxSansIntegrationHeure | N(5) | | 2 | RG-0705 |
| PRO_DOSP_DERSAUVEGARDE | DerniereSauvegardeLe | D | | | RG-0705 |
| PRO_DOSP_SAUVEGARDEPAR | DerniereSauvegardePar | A(100) | | | |

| Code | Propriété | Type | Description | Valeurs / Exemple | RG |
|---|---|---|---|---|---|
| ENT_SVCASSOC | SERVICE ASSOCIÉ (groupe par composant) | | | | [N4Component.cs L40-67](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/N4Component.cs#L40-L67) |
| PRO_SVCASSOC_ID | IdServiceAssocie | ID | | GUID | |
| PRO_SVCASSOC_CMP_ID | IdComposant | ID | FK → COMPOSANT | | |
| PRO_SVCASSOC_NOM | NomServiceWindows | A(200) | Nom exact | XPSGateService, XMLRDTService... | |
| PRO_SVCASSOC_REQUIS | EstRequisPourOperationnel | B | Défaut vrai | true | |

---

### Catégorie : Sécurité — Utilisateurs, Rôles, Autorisations

| Code | Propriété | Type | Description | Valeurs / Exemple | RG |
|---|---|---|---|---|---|
| ENT_USR | UTILISATEUR (Compte nominatif) | | | | RG-0801 / RG-0803 |
| PRO_USR_ID | IdUtilisateur | ID | | GUID | |
| PRO_USR_LOGIN | LoginWindows | A(150) | UPN ou DOMAINE\login | CIV\jdupont / jdupont@civ.ci | RG-0803 (pas partagé) |
| PRO_USR_NOM | Nom | A(100) | | Dupont | |
| PRO_USR_PRENOM | Prenom | A(100) | | Jean | |
| PRO_USR_EMAIL | AdresseEmail | A(200) | Pour notifications/MFA | | |
| PRO_USR_MFAACTIF | MFAEstActif | B | TOTP Email/Passkey | | RG-0801 |
| PRO_USR_STATUT | EstActif | B | | | |
| PRO_USR_DERCONNEXION | DerniereConnexionLe | D | | | SEC-008 |

| Code | Propriété | Type | Description | Valeurs / Exemple | RG |
|---|---|---|---|---|---|
| ENT_ROL | RÔLE APPLICATIF | | | | [N4Roles.cs](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/N4Roles.cs) |
| PRO_ROL_ID | IdRole | ID | | GUID | |
| PRO_ROL_CODE | CodeRole | A(50) | Unique | Lecteur, OperateurN4, AdminN4, AdminInfra, Validateur, AdminSolution, Auditeur, TOSManager | RG-0801 |
| PRO_ROL_NOM | NomRole | A(100) | | Support N1 / Opérateur N4... | |
| PRO_ROL_DESCR | Description | A(500) | Droits décrits | | RG-0802 Moindre privilège |

| Code | Propriété | Type | Description | Valeurs / Exemple | RG |
|---|---|---|---|---|---|
| ENT_HAB | HABILITATION ENVIRONNEMENT × RÔLE × UTILISATEUR | | | | SEC-004 |
| PRO_HAB_ID | IdHabilitation | ID | | GUID | |
| PRO_HAB_USR_ID | IdUtilisateur | ID | FK → UTILISATEUR | | |
| PRO_HAB_ENV_ID | IdEnvironnement | ID | FK → ENVIRONNEMENT | | RG-0804 UAT ≠ PROD |
| PRO_HAB_ROL_ID | IdRole | ID | FK → RÔLE | | RG-0801 Séparation |
| PRO_HAB_VALIDEDEPUIS | ValideDepuisLe | D | | | |
| PRO_HAB_VALIDEJUSQUA | ValideJusquaLe | D | Null = permanent | | RG-0801 |

---

### Catégorie : Orchestration — Workflows & Exécutions

| Code | Propriété | Type | Description | Valeurs / Exemple | RG |
|---|---|---|---|---|---|
| ENT_WKF | WORKFLOW (modèle) | | | | CdC FR-003 |
| PRO_WKF_ID | IdWorkflow | ID | | GUID | |
| PRO_WKF_ENV_ID | IdEnvironnement | ID | FK → ENVIRONNEMENT | | |
| PRO_WKF_NOM | Nom | A(200) | | Arrêt Complet Production | |
| PRO_WKF_TYPE | TypeWorkflow | A(30) | ArretComplet / DemarrageComplet / RedemarrageCluster / BasculeCenter / ArretPartiel / DemarragePartiel / MaintenanceComposant / ReconstitutionDossierPartage | |
| PRO_WKF_VERSION | Version | N(4) | Incrément après modif si exécuté | 1, 2, 3... | |
| PRO_WKF_STATUT | StatutCycleVie | A(20) | Brouillon→Actif | | RG-0104 |
| PRO_WKF_AUTOMAT | NiveauAutomatisation | A(20) | SemiAutomatique (Palier1) / Automatique (Palier2) | | CdC §2.2 |
| PRO_WKF_DOUBLEAPPROB | RequiertDoubleApprobation | B | | true si CRITIQUE Prod | RG-0304 |
| PRO_WKF_DESCR | Description | A(1000) | | | |

| Code | Propriété | Type | Description | Valeurs / Exemple | RG |
|---|---|---|---|---|---|
| ENT_ETP | ÉTAPE WORKFLOW (modèle) | | | | |
| PRO_ETP_ID | IdEtapeModele | ID | | GUID | |
| PRO_ETP_WKF_ID | IdWorkflow | ID | FK → WORKFLOW | | |
| PRO_ETP_ORDRE | Ordre | N(4) | Dans la séquence | 1..N | RG-ARRÊT-01 / RG-DÉMARRAGE-01 |
| PRO_ETP_NOM | NomEtape | A(200) | | Démarrer Cluster Node 1 | |
| PRO_ETP_ACTION | Action | A(30) | Demarrer / Arreter / Redemarrer / Attente / VerrouPreRequis / ControlePreuve / InterventionManuelle / ArretForce | |
| PRO_ETP_CMP_ID | IdComposantCible | ID | FK → COMPOSANT (nullable pour étapes sans cible) | | |
| PRO_ETP_TIMEOUT | TimeoutSecondes | N(6) | | 1800 | |
| PRO_ETP_DUREEATTENDUE | DureeAttendueSecondes | N(5) | | 120 | |
| PRO_ETP_SEUILWARN | SeuilAvertissementSecondes | N(5) | | 240 | |
| PRO_ETP_MAXESSAIS | NombreMaxEssais | N(2) | 0 ou 1 pour critique | 0 RG-0408 |
| PRO_ETP_RETRY_AUTO | RetryAutomatique | B | faux si destructrice | false RG-0408 |
| PRO_ETP_DELAY_RETRY | DelaiRetrySecondes | N(5) | | 30 | |
| PRO_ETP_SKIPABLE | Contournable | B | | true si non destructrice | |
| PRO_ETP_CONFREQUISE | ConfirmationOperateur | B | Vrai avant chaque action mutative | true RG-04 |
| PRO_ETP_PARALLELISABLE | Parallelisable | B | Seulement validateur | | RG-0403 |
| PRO_ETP_ECHECPOL | PolitiqueEchec | A(15) | Bloquer / ContinuerAlerte / ContinuerSilence | Bloquer défaut | |
| PRO_ETP_INSTRUCTION | InstructionManuelle | A(2000) | Si intervention | | RG-0406 |

| Code | Propriété | Type | Description | Valeurs / Exemple | RG |
|---|---|---|---|---|---|
| ENT_VRF | CONTRÔLE PRÉALABLE (Modèle) — PRÉ-CHECK | | | | CdC FR-012 |
| PRO_VRF_ID | IdControleModele | ID | | GUID | |
| PRO_VRF_WKF_ID | IdWorkflow | ID | FK → WORKFLOW ou NULL si global | | |
| PRO_VRF_CODE | CodeControle | A(50) | Unique | PR-CHK-HOLOGES, PR-CHK-DISK... | RG-0307 TOP10 |
| PRO_VRF_NOM | Libelle | A(200) | Synchronisation horloges serveurs < 1s | | |
| PRO_VRF_SEVERITE | SeveriteSiEchec | A(15) | Bloquant / Avertissement / NonApplicable | | RG-0302 |
| PRO_VRF_SCRIPT | ScriptVerificationDefinition | A(4000) | JSON/PowerShell | | RG-0301 |
| PRO_VRF_CMP_ID | IdComposantCible | ID | FK → COMPOSANT nullable | | |

| Code | Propriété | Type | Description | Valeurs / Exemple | RG |
|---|---|---|---|---|---|
| ENT_EXE | EXÉCUTION WORKFLOW | | | | [WorkflowExecution.cs](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/WorkflowExecution.cs#L81-L254) |
| PRO_EXE_ID | IdExecution | ID | | GUID | |
| PRO_EXE_WKF_ID | IdWorkflow | ID | FK → WORKFLOW | | |
| PRO_EXE_WKFVERS | VersionWorkflowLancee | N(4) | Recopiée figée | | |
| PRO_EXE_WKFNOM | NomWorkflowFigé | A(200) | Figé | | |
| PRO_EXE_ENV_ID | IdEnvironnement | ID | FK → ENVIRONNEMENT | | |
| PRO_EXE_ENVCODE | CodeEnvironnementFige | A(20) | Figé audit | PRD | |
| PRO_EXE_STATUT | Statut | A(30) | EnPreparation / EnAttenteApprobation / EnCours / EnPause / AnnulationDemandee / ReconciliationRequise / TermineSucces / TermineAvecAvertissements / Echec / Annule | | RG-0401 |
| PRO_EXE_SIMULATION | EstSimulation | B | Aucune commande mutative | false défaut / true RG-0409 |
| PRO_EXE_AUTOMAT | NiveauAutomatisation | A(20) | SemiAutomatique / Automatique | | |
| PRO_EXE_FALLBACK | FallbackSemiAutoForce | B | Si Palier2 a échoué | | |
| PRO_EXE_DEMANDEUR | DemandePar | A(150) | Login | | |
| PRO_EXE_IDOPER | IdentiteOperateurLogin | A(150) | Figé lancement | | |
| PRO_EXE_ETIQOPER | EtiquetteIdentiteOperateur | A(255) | "N4Sentinel · DOMAINE\user" | | Audité |
| PRO_EXE_MOTIF | MotifOperation | A(1000) | OBLIGATOIRE Prod | Maintenance planifiée | RG-0303 Prod |
| PRO_EXE_TICKET | ReferenceTicket | A(50) | Ticket outil ITSM CIT | INC-2026-0912-0034 | RG-0303 |
| PRO_EXE_IMPACT | ImpactPrevu | A(1000) | | Arrêt ECN4 15 min | |
| PRO_EXE_FENDEB | FenetreDebut | D | Début autorisé | | RG-0303 Prod |
| PRO_EXE_FENFIN | FenetreFin | D | Fin autorisée | | |
| PRO_EXE_DUREEESTIM | DureeEstimeeTotale | N(8) | Secondes agrégées | | |
| PRO_EXE_APPROUV1PAR | Approbateur1Par | A(150) | | | RG-0304 |
| PRO_EXE_APPROUV1LE | Approbateur1Le | D | | | |
| PRO_EXE_APPROUV2PAR | Approbateur2Par | A(150) | Double approb Prod | | RG-0304 |
| PRO_EXE_APPROUV2LE | Approbateur2Le | D | | | |
| PRO_EXE_CHOIXCONT_REQUIS | ChoixContinuiteRequis | B | Vrai si touche Center/Standby | | RG-0306 |
| PRO_EXE_CHOIXCONT_VALEUR | ChoixContinuiteValeur | A(15) | ResterActif / Basculer | | RG-0306 |
| PRO_EXE_CHOIXCONT_PAR | ChoixContinuiteFaitPar | A(150) | | | |
| PRO_EXE_CHOIXCONT_LE | ChoixContinuiteFaitLe | D | | | |
| PRO_EXE_DEBUTREELLE | DebutEffectifLe | D | | | |
| PRO_EXE_FINREELLE | FinEffectifLe | D | | | |
| PRO_EXE_HEARTBEAT | DernierHeartbeatMoteurLe | D | | | NFR-002 reprise |
| PRO_EXE_PAR_PAUSE | PauseDemandeePar | A(150) | | | |
| PRO_EXE_PAR_ANNUL | AnnulationDemandeePar | A(150) | | | |
| PRO_EXE_RESULTAT | ResumeResultat | A(2000) | Cause échec, avertissements | | |
| PRO_EXE_RAPPORTPOSTANNULE | RapportPostAnnulation | A(4000) | État réel constaté | | RG-0405 |
| PRO_EXE_INTERVMANU_REQUISE | RequiertInterventionManuelle | B | | false RG-0405 |
| PRO_EXE_CORRELID | IdCorrelation | A(20) | Unique 12 hex | a1b2c3d4e5f6 | |
| PRO_EXE_PRECHECKJSON | RapportPreflightJSON | A(16000) | Stocké tel quel | | RG-0301 |
| PRO_EXE_PRECHECKLE | PreflightEffectueLe | D | | | RG-0301 |
| PRO_EXE_PRECHKBLOQ | PreflightContientBloquant | B | | true = bloqué | RG-0302 |

| Code | Propriété | Type | Description | Valeurs / Exemple | RG |
|---|---|---|---|---|---|
| ENT_EXEETP | ÉTAPE D'EXÉCUTION | | | | [ExecutionStep](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/WorkflowExecution.cs#L257-L371) |
| PRO_EXEETP_ID | IdEtapeExecution | ID | | GUID | |
| PRO_EXEETP_EXE_ID | IdExecution | ID | FK → EXÉCUTION | | |
| PRO_EXEETP_ORDRE | OrdreEtape | N(4) | Figé au lancement | | |
| PRO_EXEETP_NOM | NomEtape | A(200) | Figé | | |
| PRO_EXEETP_ACTION | Action | A(30) | Figé | | |
| PRO_EXEETP_CMP_ID | IdComposantCible | ID | FK nullable → COMPOSANT | | |
| PRO_EXEETP_CMPNOM | NomComposantFige | A(200) | Figé audit | | |
| PRO_EXEETP_HOTENOM | NomHoteFige | A(150) | Figé audit | | |
| PRO_EXEETP_STATUT | Statut | A(20) | AVenir / EnAttente / EnCours / Verification / Reussi / Avertissement / Bloque / Echec / Ignore / Annule | | RG-0401 |
| PRO_EXEETP_DEBUT | DebutEffectifLe | D | | | |
| PRO_EXEETP_FIN | FinEffectifLe | D | | | |
| PRO_EXEETP_PROGMSG | MessageAvancement | A(500) | Ex: "310s / 1800s — Attente journal navis-apex.log ReadyPattern" | | |
| PRO_EXEETP_PREUVE | PreuveTechnique | A(4000) | Ligne journal reconnue + statut | | RG-0402 |
| PRO_EXEETP_CMD | CommandeExecuteeExpurgee | A(4000) | Secrets masqués | | FR-028 |
| PRO_EXEETP_ERREUR | TexteErreur | A(4000) | | | |
| PRO_EXEETP_ERRTYPE | TypeErreurTaxinomie | A(30) | CommandeRefusee / TimeoutAttente / ComportementConnuStopPending / ComposantNonConfigure / PrerequisNonSatisfait / Inconnu | | RG-0407 |
| PRO_EXEETP_NBESSAIS | NombreEssaisEffectues | N(2) | | 0 défaut | |
| PRO_EXEETP_MAXESSAIS | MaxEssaisFiges | N(2) | Figé | | |
| PRO_EXEETP_RETRYAUTO | RetryAutoFige | B | Figé | | |
| PRO_EXEETP_RETRYDELAY | DelaiRetrySecFiges | N(5) | Figé | | |
| PRO_EXEETP_RETRYAPRES | RetryAutoriseApresLe | D | | | |
| PRO_EXEETP_CONTOUR_PAR | ContournementPar | A(150) | SkippedBy | | RG-0406 |
| PRO_EXEETP_CONTOUR_MOTIF | ContournementMotif | A(1000) | SkipReason | | RG-0406 |
| PRO_EXEETP_CONTOUR_COAPP_PAR | ContournementCoApprouvePar | A(150) | Prod double | | RG-0406 |
| PRO_EXEETP_CONTOUR_COAPP_LE | ContournementCoApprouveLe | D | Prod double | | RG-0406 |
| PRO_EXEETP_ARRETFORCE_PAR | ArretForcePar | A(150) | Jamais auto | | RG-0407 |
| PRO_EXEETP_ARRETFORCE_MOTIF | ArretForceMotif | A(1000) | Jamais auto | | RG-0407 |
| PRO_EXEETP_CONFIRM_PAR | ConfirmationOperateurPar | A(150) | | | RG-04xx |
| PRO_EXEETP_CONFIRM_LE | ConfirmationOperateurLe | D | | | |
| PRO_EXEETP_NOTEOPER | NoteOperateur | A(2000) | Libre | | |
| PRO_EXEETP_PREUVFICHIER | PreuveJointeObligatoire | B | | | RG-0406 |
| PRO_EXEETP_PREUVFICHNOM | NomFichierPreuveJoint | A(255) | | | |
| PRO_EXEETP_PREUVFICHTYP | TypeMimeFichierPreuve | A(100) | | | |
| PRO_EXEETP_PREUVFICHBIN | ContenuBinaireFichierPreuve | BLOB | | | (fréquence : faible) |
| PRO_EXEETP_SESSIONDIAG_ID | IdSessionDiagnosticRattachee | ID | FK nullable → SESSION DIAGNOSTIC | | RG-05xx |
| PRO_EXEETP_TIMEOUTSECONDES | TimeoutSecondesFige | N(6) | Figé | | |
| PRO_EXEETP_DUREEATTENDUESECONDES | DureeAttendueSecondesFige | N(5) | Figé | | |
| PRO_EXEETP_SEUILWARNSECONDES | SeuilAvertissementFige | N(5) | Figé | | |
| PRO_EXEETP_SKIPABLE | EstContournableFige | B | Figé | | |
| PRO_EXEETP_CONFREQUISE | ConfirmationRequiseFige | B | Figé | | |
| PRO_EXEETP_PARALLELISABLE | ParallelisableFige | B | Figé | | RG-0403 |
| PRO_EXEETP_ECHECPOL | PolitiqueEchecFige | A(15) | Figé | | |
| PRO_EXEETP_INSTRUCTION | InstructionManuelleFige | A(2000) | Figé | | |
| PRO_EXEETP_DESTRUCTRICE | EstActionDestructrice | B | Arreter / Redemarrer / ArretForce = vrai | | RG-0408 |

| Code | Propriété | Type | Description | Valeurs / Exemple | RG |
|---|---|---|---|---|---|
| ENT_VERR | VERROU ENVIRONNEMENT (MUTEX) | | | | [EnvironmentLock](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/WorkflowExecution.cs#L384-L402) |
| PRO_VERR_ID | IdVerrou | ID | | GUID | |
| PRO_VERR_ENV_ID | IdEnvironnement | ID | FK → ENVIRONNEMENT | | RG-0305 |
| PRO_VERR_EXE_ID | IdExecution | ID | FK → EXÉCUTION | | RG-0305 |
| PRO_VERR_DETENU_PAR | DetenuPar | A(150) | Opérateur | | |
| PRO_VERR_MOTIF | MotifAcquisition | A(500) | Arrêt complet demandé par ticket INC-xxx | | |
| PRO_VERR_ACQUISE_LE | AcquisLe | D | | | |
| PRO_VERR_EXPIRE_LE | ExpireLe | D | Prolongé par heartbeat | | RG-0305 expiration |

---

### Catégorie : Supervision — Signaux, Alertes, Vitalité

| Code | Propriété | Type | Description | Valeurs / Exemple | RG |
|---|---|---|---|---|---|
| ENT_SIG | SIGNAL COMPOSANT (historique supervision) | | | | [ComponentSignal.cs](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/ComponentSignal.cs) |
| PRO_SIG_ID | IdSignal | ID | | GUID | |
| PRO_SIG_CMP_ID | IdComposant | ID | FK → COMPOSANT | | |
| PRO_SIG_MESURE_LE | MesureLe | D | Horodatage collecte | | RG-0201 |
| PRO_SIG_NOMMESURE | NomMesure | A(100) | QueueSize, ConsumerCount, HeartbeatAgeSec, DiskUsagePct, ClockSkewSec, IndexFragPct, LocallyScheduledJobCount | | Amélioration RG-09 |
| PRO_SIG_VALEUR_NUM | ValeurNumerique | N(18,4) | | 87.3, 1254 | |
| PRO_SIG_VALEUR_TEXTE | ValeurTexte | A(500) | ACTIVE, LOADING, En Marche... | | |
| PRO_SIG_SEUILATTEINT | SeuilAlerteAtteint | B | | | |
| PRO_SIG_SOURCE | CollecteParConnecteur | A(200) | PowerShell/ConnectorX... | | |
| PRO_SIG_ERREUR | ErreurCollecte | A(2000) | Si échec | | RG-0202 Inconnu |

| Code | Propriété | Type | Description | Valeurs / Exemple | RG |
|---|---|---|---|---|---|
| ENT_ALT | ALERTE SUPERVISION / PRÉDICTIVE | | | | [Alert.cs](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/Alert.cs) |
| PRO_ALT_ID | IdAlerte | ID | | GUID | |
| PRO_ALT_ENV_ID | IdEnvironnement | ID | FK → ENVIRONNEMENT | | |
| PRO_ALT_CMP_ID | IdComposant | ID | FK nullable → COMPOSANT | | |
| PRO_ALT_CODE | CodeAlerte | A(50) | Unique | ALT-QUEUE-BRIDGE, ALT-CLOCK-SKEW, ALT-IDX-FRAG... | RG-09 |
| PRO_ALT_NIVEAU | Gravite | A(15) | Info / Avertissement / Mineure / Majeure / Critique | | RG-0103 |
| PRO_ALT_TITRE | Titre | A(200) | Fragmentation index INV_WQ atteinte 34% (seuil 30%) | | RG-0902 APR |
| PRO_ALT_DESCR | DescriptionDetaillee | A(4000) | Preuves + recommandation | | |
| PRO_ALT_TYPE | Nature | A(30) | SupervisionTempsReel / PredictiveTendance / Preventive (APR) | | Amélioration RG-09xx |
| PRO_ALT_CONFIANCE | NiveauConfiancePredit | N(3) | 0-100 pour prédictives | 82 | RG-09xx |
| PRO_ALT_SUGGACTION | ActionSuggeree | A(2000) | Rebuild OFFLINE index INV_WQ ; fenêtre estimée 22h-23h durée 14min | | Amélioration RG-09xx APR |
| PRO_ALT_RISQUE_P1_EVITE | RisqueP1EviteSiFaitePourcent | N(3) | 0-100 | 85 | RG-0904 APR |
| PRO_ALT_DUREE_ACTION_MIN | DureeActionSuggereeMin | N(4) | Minutes | 14 | RG-0904 |
| PRO_ALT_CREE_LE | CreeLe | D | | | |
| PRO_ALT_RECONNU_PAR | AcquiteePar | A(150) | Opérateur qui a pris en compte | | |
| PRO_ALT_RECONNU_LE | AcquitteeLe | D | | | |
| PRO_ALT_TRAITE_PAR | TraiteePar | A(150) | | | |
| PRO_ALT_TRAITE_LE | TraiteeLe | D | | | |
| PRO_ALT_STATUT | Statut | A(20) | Nouvelle / Acquittee / Traitee / FausseAlerte / Ignoree | | |
| PRO_ALT_SESSIONDIAG_ID | IdSessionDiagnosticOuverte | ID | FK nullable → SESSION DIAGNOSTIC | | |

---

### Catégorie : Diagnostic — Sessions, Signatures, Journaux

| Code | Propriété | Type | Description | Valeurs / Exemple | RG |
|---|---|---|---|---|---|
| ENT_SESDIAG | SESSION DIAGNOSTIC | | | | [DiagnosticSession](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/Diagnostic.cs#L293-L377) |
| PRO_SESDIAG_ID | IdSession | ID | | GUID | |
| PRO_SESDIAG_ENV_ID | IdEnvironnement | ID | FK → ENVIRONNEMENT | | |
| PRO_SESDIAG_ENVCODE | CodeEnvironnementFige | A(20) | Figé | | |
| PRO_SESDIAG_TITRE | Titre | A(200) | | XPS ne reçoit plus mises à jour depuis 08:32 | | |
| PRO_SESDIAG_MOTIF | Motif / Objectif | A(2000) | | | |
| PRO_SESDIAG_TICKET | ReferenceTicket | A(50) | | INC-xxx | |
| PRO_SESDIAG_DEMANDEUR | DemandePar | A(150) | | | |
| PRO_SESDIAG_FENDEB | FenetreAnalyseDebut | D | | | |
| PRO_SESDIAG_FENFIN | FenetreAnalyseFin | D | | | |
| PRO_SESDIAG_VERDICT | VerdictFinal | A(30) | CauseConfirmee / CauseCaracterisee / PlusieursCausesPossibles / PisteSerieuse / AnomaliesSansCause / RienDeConcluant / InformationsInsuffisantes | | RG-0502 |
| PRO_SESDIAG_VERDICTEXPL | ExplicationVerdict | A(4000) | Limites explicités | | RG-0503 |
| PRO_SESDIAG_ANALYSELE | AnalyseEffectueeLe | D | | | |
| PRO_SESDIAG_ALT_ID | IdAlerteSource | ID | FK nullable → ALERTE | | |
| PRO_SESDIAG_REF_SAINE | EstReferencePeriodeSaine | B | Affirmation humaine | false | RG-0505 |
| PRO_SESDIAG_REF_SESSION_ID | IdSessionReferencePeriodeSaine | ID | FK nullable → SESSION DIAGNOSTIC | | RG-0505 |
| PRO_SESDIAG_REF_MODE | ModeComparaisonActif | A(30) | PeriodeSaine / ExecutionReussie / ValeursHabituellesComposant / NoeudPair | | RG-0505 |
| PRO_SESDIAG_REF_EXE_ID | IdExecutionReferenceReussie | ID | FK nullable → EXÉCUTION | | RG-0505 |
| PRO_SESDIAG_REF_CMP_ID | IdComposantReference | ID | FK nullable → COMPOSANT | | RG-0505 |
| PRO_SESDIAG_PHASE | PhaseCourante8Phases | A(30) | DetectionEtEnregistrement / QualificationEtCollecte / Securisation / DiagnosticEtCorrelation / ChoixDuPlanDAction / ValidationEtExecution / RemiseEnServiceEtVerification / ClotureEtCapitalisation | | RG-0501 |
| PRO_SESDIAG_ESCALADVERS | EscaladeVersQui | A(150) | Infra / Support Navis... | | RG-0504 |
| PRO_SESDIAG_ESCALADLE | EscaladeFaiteLe | D | | | RG-0504 |
| PRO_SESDIAG_ESCALADPAR | EscaladeDemandeePar | A(150) | | | RG-0504 |

| Code | Propriété | Type | Description | Valeurs / Exemple | RG |
|---|---|---|---|---|---|
| ENT_PHDIAG | TRANSITION PHASE SESSION DIAGNOSTIC | | | | [DiagnosticPhaseTransition](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/Diagnostic.cs#L384-L400) |
| PRO_PHDIAG_ID | IdTransition | ID | | GUID | |
| PRO_PHDIAG_SES_ID | IdSession | ID | FK → SESSION DIAGNOSTIC | | RG-0501 NON LINÉAIRE |
| PRO_PHDIAG_PHASE | Phase | A(30) | Même enum | | |
| PRO_PHDIAG_PAR | EntreePar | A(150) | Opérateur | | |
| PRO_PHDIAG_LE | EntreeLe | D | | | |
| PRO_PHDIAG_NOTE | Justification | A(2000) | Obligatoire pour ClotureEtCapitalisation | | RG-0501 |

| Code | Propriété | Type | Description | Valeurs / Exemple | RG |
|---|---|---|---|---|---|
| ENT_SIGDIAG | SIGNATURE ANOMALIE CATALOGUÉE | | | | [DiagnosticSignature](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/Diagnostic.cs#L133-L200) |
| PRO_SIGDIAG_ID | IdSignature | ID | | GUID | |
| PRO_SIGDIAG_CODE | CodeSignature | A(50) | Stable | SIG-CLUST-HZ-JOIN, SIG-DB-ORA-12154... | | |
| PRO_SIGDIAG_NOM | Nom | A(200) | | Cluster Hazelcast timeout de join | |
| PRO_SIGDIAG_REGEX | MotifRegEx | A(1000) | Regex de recherche dans les lignes | .*HazelcastInstanceNotActiveException.* | |
| PRO_SIGDIAG_DOMAINE | DomaineTechnique | A(30) | BaseDeDonnees / Reseau / Memoire / Configuration / Licence / Cluster / Stockage / Integration / Securite / Horloge / Applicatif / Systeme / Services / N4Cluster / CenterStandby / ActiveMqKahaDb / BridgeXps / Ecn4Ecn4Web / SharedFolders / EdiInterfaces / Indetermine | | |
| PRO_SIGDIAG_SEVERITE | Severite | A(15) | Info / Avertissement / Erreur / Critique | | |
| PRO_SIGDIAG_ORIGINE | Origine | A(10) | Editeur / Site | | RG-0603 |
| PRO_SIGDIAG_SENS | SignificationMetier | A(2000) | Ce que ça veut dire pour un non expert | | RG-0603 |
| PRO_SIGDIAG_REMED | ActionCorrectriceConseillee | A(2000) | Conseil PAS auto | | RG-0603 |
| PRO_SIGDIAG_DOCREF | ReferenceDocumentaire | A(500) | Guide 3.8.25 section 2.1.4 p.842 | | RG-0603 |
| PRO_SIGDIAG_CONTREPREUV | ContrePreuvePossible | A(2000) | Ce qui infirmerait cette cause | | RG-0503 |
| PRO_SIGDIAG_VERSION | Version | N(4) | Incrément à chaque modif | 1 | RG-0603 |
| PRO_SIGDIAG_POIDS | PoidsConfiance0a100 | N(3) | 1-100 | 50 défaut | RG-0503 |
| PRO_SIGDIAG_CMP_ROLE | AppliqueARoleComposant | A(30) | Restriction de rôle nullable | | |
| PRO_SIGDIAG_ACTIF | EstActivee | B | true défaut | | |
| PRO_SIGDIAG_STATUT | StatutValidation | A(20) | Cycle Brouillon→Actif | Valide défaut | RG-0603 |

| Code | Propriété | Type | Description | Valeurs / Exemple | RG |
|---|---|---|---|---|---|
| ENT_REGCORR | RÈGLE DE CORRÉLATION MULTI-SIGNAUX | | | | [CorrelationRule](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/Diagnostic.cs#L207-L227) |
| PRO_REGCORR_ID | IdRegleCorrelation | ID | | GUID | |
| PRO_REGCORR_CODE | CodeRegle | A(50) | | COR-SPLIT-BRAIN-001 | |
| PRO_REGCORR_NOM | Nom | A(200) | Détection Split-Brain Center | | RG-SPLIT-BRAIN |
| PRO_REGCORR_DOMAINE | DomaineTechnique | A(30) | | CenterStandby | |
| PRO_REGCORR_HYPOTHESE | Hypothese | A(2000) | Les 2 nœuds Center & Standby émettent heartbeat = rôle actif dans fenêtre de 2min → split-brain AMQ imminent | | RG-0503 |
| PRO_REGCORR_CONFIANCE | NiveauConfiance0a100 | N(3) | 0-100 | 92 | RG-0503 |
| PRO_REGCORR_RECO | Recommandation | A(2000) | Redémarrer Standby immédiatement ; vérifier fichiers db.(n) dans \\share\amq | | RG-05xx |
| PRO_REGCORR_FENETRESEC | FenetreTempsSecondes | N(6) | Dans laquelle tous les signaux doivent se produire | 120 | RG-0506 |

| Code | Propriété | Type | Description | Valeurs / Exemple | RG |
|---|---|---|---|---|---|
| ENT_CONDCORR | CONDITION RÈGLE CORRÉLATION | | | | [CorrelationCondition](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/Diagnostic.cs#L232-L245) |
| PRO_CONDCORR_ID | IdCondition | ID | | GUID | |
| PRO_CONDCORR_REGC_ID | IdRegleCorrelation | ID | FK → RÈGLE CORRÉLATION | | |
| PRO_CONDCORR_SOURCE | IdSourceSignal | A(100) | CodeSignature ou CodeMesure | SIGDIAG-CENTER-ACTIVE | |
| PRO_CONDCORR_DESCR | DescriptionCondition | A(500) | Center Pattern ACTIVE détecté dans les logs CenterNode | | |
| PRO_CONDCORR_NEGATION | EstNegation | B | Vrai = absence de signal = condition | | |

| Code | Propriété | Type | Description | Valeurs / Exemple | RG |
|---|---|---|---|---|---|
| ENT_LOGSRC | SOURCE JOURNAL (Session Diagnostic) | | | | [LogSource](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/Diagnostic.cs#L418-L534) |
| PRO_LOGSRC_ID | IdSourceJournal | ID | | GUID | |
| PRO_LOGSRC_SES_ID | IdSession | ID | FK → SESSION DIAGNOSTIC | | |
| PRO_LOGSRC_CMP_ID | IdComposantDesigne | ID | FK nullable → COMPOSANT | | |
| PRO_LOGSRC_CMPNOM | NomComposantDesigneFige | A(200) | Figé | | |
| PRO_LOGSRC_CMPROLE | RoleComposantDesigneFige | A(30) | Figé | | |
| PRO_LOGSRC_HOTENOM | NomHoteFige | A(150) | Figé | | |
| PRO_LOGSRC_CMP_AUTODETEC | ComposantAutoIdentifie | B | Si deviné par nom/contenu | false RG-06 | |
| PRO_LOGSRC_VERSION | VersionDetecteeContenu | A(50) | 3.8.25, 4.9... | | RG-06xx |
| PRO_LOGSRC_FORMATLOG | FormatDetecte | A(50) | log4j/Apex, IIS, WindowsEvent... | | RG-06xx |
| PRO_LOGSRC_FUSEAU | FuseauHoraireDetecte | A(50) | | | RG-0204 |
| PRO_LOGSRC_SUGGCMP_ID | IdComposantSuggereContenu | ID | FK nullable → COMPOSANT | | RG-06xx |
| PRO_LOGSRC_SUGGCMP_NOM | NomComposantSuggere | A(200) | Figé | | RG-06xx |
| PRO_LOGSRC_SUGGCMP_PREUVE | IndiceIdentification | A(2000) | Extrait justifiant | | RG-06xx |
| PRO_LOGSRC_AMBIGU | OrigineMultiCandidats | B | Si plusieurs composants possibles | | RG-06xx |
| PRO_LOGSRC_CORRELID | IdCorrelationAssociation | A(20) | Relié à une opération / incident | | |
| PRO_LOGSRC_ORIGINECOLLECTE | OrigineCollecte | A(20) | CollecteCiblee via connecteur / ImportManuel par opérateur | | |
| PRO_LOGSRC_CHEMIN | CheminEffectifLecture | A(300) | Après résolution wildcard | | |
| PRO_LOGSRC_NOMFICHIER | NomFichier | A(200) | | navis-apex-20260912.log | |
| PRO_LOGSRC_TAILLE | TailleOctets | N(12) | | 10485760 | |
| PRO_LOGSRC_NBLIGNES | NombreLignes | N(8) | | 86432 | |
| PRO_LOGSRC_HACHAGE | HachageSHA256ContenuMasque | A(64) | Hex | Intégrité | RG-0602 |
| PRO_LOGSRC_1ERLIGNE | Horodatage1ereLigne | D | | | RG-073 résumé |
| PRO_LOGSRC_DERLIGNE | HorodatageDerniereLigne | D | | | |
| PRO_LOGSRC_CPT_INFO | CompteNiveauINFO | N(8) | | | |
| PRO_LOGSRC_CPT_WARN | CompteNiveauWARN | N(8) | | | |
| PRO_LOGSRC_CPT_ERR | CompteNiveauERROR | N(8) | | | |
| PRO_LOGSRC_ECARTHORLOGE | EcartHorlogeSourceSecondes | N(8,3) | Mesuré lors collecte ciblée | 0.327 | RG-0204 |
| PRO_LOGSRC_NBSECRETSMASQ | NombreSecretsMasques | N(6) | Affiché opérateur | 12 | RG-0601 |
| PRO_LOGSRC_TRONQUE | EstTronqueFinFichierSeulement | B | Si taille max lecture | | |
| PRO_LOGSRC_ERREUR | TexteErreurCollecte | A(2000) | | | RG-0605 |
| PRO_LOGSRC_ERRRAISON | RaisonEchecTaxinomie | A(30) | AccesRefuse / ConnecteurIndisponible / Timeout / SourceAbsente / FormatNonReconnu / ControleNonConfigure | | RG-0605 |
| PRO_LOGSRC_SUCCES | CollecteSucces | B | | | |

| Code | Propriété | Type | Description | Valeurs / Exemple | RG |
|---|---|---|---|---|---|
| ENT_LOGCONST | CONSTAT D'ANOMALIE DANS JOURNAL | | | | [LogFinding](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/Diagnostic.cs#L544-L593) |
| PRO_LOGCONST_ID | IdConstat | ID | | GUID | |
| PRO_LOGCONST_SES_ID | IdSession | ID | FK → SESSION DIAGNOSTIC | | |
| PRO_LOGCONST_SRC_ID | IdSourceJournal | ID | FK → SOURCE JOURNAL | | |
| PRO_LOGCONST_SIGDIAG_ID | IdSignatureReconnue | ID | FK nullable → SIGNATURE | | NULL si non cataloguée |
| PRO_LOGCONST_SIGDIAG_CODE | CodeSignatureFigé | A(50) | Figé audit | | |
| PRO_LOGCONST_DOMAINE | DomaineTechnique | A(30) | | | |
| PRO_LOGCONST_SEVERITE | Severite | A(15) | | | |
| PRO_LOGCONST_TITRE | IntituleConstat | A(200) | Figé ou signature | | |
| PRO_LOGCONST_LIGNE_EXEMPLE | LigneRepresentativeMasquee | A(500) | MÁSQUÉE | | RG-0601 |
| PRO_LOGCONST_CONTEXTE | LignesContexteMasquees | A(4000) | +/- lignes masquées | | RG-077 |
| PRO_LOGCONST_NBOCCURRENCES | NombreOccurrencesGroupees | N(8) | Regroupement (cf 40x même = 1 problème) | 412 | |
| PRO_LOGCONST_PREMIERE | PremiereObservationLe | D | | | |
| PRO_LOGCONST_DERNIERE | DerniereObservationLe | D | | | |
| PRO_LOGCONST_PREMIERENUM | PremierNumeroLigne | N(8) | | | |
| PRO_LOGCONST_SENS | SignificationCopiee | A(2000) | Depuis signature si reconnue | | |
| PRO_LOGCONST_REMED | RemediationCopiee | A(2000) | Depuis signature | | |
| PRO_LOGCONST_DOCREF | ReferenceDocCopiee | A(500) | Depuis signature | | |
| PRO_LOGCONST_THREAD | ThreadExtrait | A(200) | Si format journal le supporte | | |
| PRO_LOGCONST_CLASSLOGGER | ClasseLogger | A(200) | | com.navis.apex... | | |
| PRO_LOGCONST_TRANSACTIONID | IdTransaction | A(100) | Si disponible | | |
| PRO_LOGCONST_NIVEAULOG | NiveauLog | A(10) | INFO / WARN / ERROR... | | |
| PRO_LOGCONST_CODEERREUR | CodeErreurExtrait | A(50) | ORA-12154, HTTP 500... | ORA-60 | |

| Code | Propriété | Type | Description | Valeurs / Exemple | RG |
|---|---|---|---|---|---|
| ENT_HYPDIAG | HYPOTHÈSE DIAGNOSTIC | | | | [DiagnosticHypothesis](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/Diagnostic.cs#L604-L644) |
| PRO_HYPDIAG_ID | IdHypothese | ID | | GUID | |
| PRO_HYPDIAG_SES_ID | IdSession | ID | FK → SESSION DIAGNOSTIC | | |
| PRO_HYPDIAG_DOMAINE | DomaineTechnique | A(30) | | | |
| PRO_HYPDIAG_AFFORMULATION | EnonceHypothese | A(2000) | L'écart d'horloge entre les serveurs provoque... | | RG-0503 |
| PRO_HYPDIAG_CONFIANCE | NiveauConfiance0a100 | N(3) | | 87 | RG-0503 |
| PRO_HYPDIAG_PREUVES | ElementDePreuve | A(4000) | Liste des faits sourcés | | RG-0503 |
| PRO_HYPDIAG_CONTREPREUVES | ElementContrePreuve | A(2000) | Null autorisé avec mention explicite | | RG-0503 |
| PRO_HYPDIAG_PREUVE_LE | DerniereObservationPreuveLe | D | | | RG-0503 |
| PRO_HYPDIAG_REGLEVERS | VersionReglePlusDeterminante | A(50) | Code + version règle | COR-SPLIT-BRAIN-001 v2 | RG-06xx |
| PRO_HYPDIAG_RECO | ActionConseillee | A(2000) | Jamais exécuté auto | | |
| PRO_HYPDIAG_RANG | RangAffichage | N(3) | Par confiance décroissante | 1 = la plus probable | |

---

### Catégorie : Base Documentaire & Connaissances

| Code | Propriété | Type | Description | Valeurs / Exemple | RG |
|---|---|---|---|---|---|
| ENT_DOC | DOCUMENT BASE DOCUMENTAIRE | | | | [Knowledge.cs](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/Knowledge.cs) |
| PRO_DOC_ID | IdDocument | ID | | GUID | |
| PRO_DOC_TITRE | Titre | A(300) | | Guide 3.8.25 Setup, Maintenance & Diagnostics | |
| PRO_DOC_CATEGORIE | Categorie | A(50) | GuideEditeur / ProcedureInterne / PostMortem / NoteDeVersion / SOP | | CdC §3.12 |
| PRO_DOC_VERSION | Version | A(50) | 3.8.25, 4.9, v1.2 | | RG-07xx |
| PRO_DOC_EDITEUR | EditeurOuAuteur | A(200) | Kaleris / Solutions IT CIV | | |
| PRO_DOC_DATEPUB | DatePublication | D | | 2022-06-28 | |
| PRO_DOC_CHEMIN | CheminFichierOriginal | A(400) | | /doc/N4 3.8.25 Setup...pdf | |
| PRO_DOC_STATUT | StatutCycleVie | A(20) | Brouillon→Actif | | RG-0104 |
| PRO_DOC_INDEXELE | EstIndexePourRAG | B | | true | |
| PRO_DOC_TAILLEMO | TailleMo | N(8,2) | Pour estimation | 12.45 | |
| PRO_DOC_NBPAGES | NombrePages | N(5) | | 1353 | |

| Code | Propriété | Type | Description | Valeurs / Exemple | RG |
|---|---|---|---|---|---|
| ENT_DOCRET | RETOUR DOCUMENT (assistant Q&R sourcé) | | | | [KnowledgeFeedback.cs](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/KnowledgeFeedback.cs) |
| PRO_DOCRET_ID | IdRetour | ID | | GUID | |
| PRO_DOCRET_DOC_ID | IdDocument | ID | FK → DOCUMENT | | |
| PRO_DOCRET_SECTION | ReferenceSectionPage | A(200) | §2.1.4.8 page 847 | | OBLIGATOIRE CdC §3.12 |
| PRO_DOCRET_QUESTION | QuestionPosee | A(500) | Redémarrer Bridge si grosse queue ? | | |
| PRO_DOCRET_REPONSE | ReponseRenvoyee | A(8000) | Structure SOP | | |
| PRO_DOCRET_UTILE | EstUtile | B | Pouce levé / baissé | | |
| PRO_DOCRET_PAR | RetourPar | A(150) | | | CdC §3.12 Signalement |
| PRO_DOCRET_LE | RetourLe | D | | | |
| PRO_DOCRET_COMMENTAIRE | CommentaireLibre | A(2000) | Précision manquante : mentionner ordre restart Clusters d'abord | | Amélioration Signalement |

---

### Catégorie : SOP — Procédures Opératoires Standard

| Code | Propriété | Type | Description | Valeurs / Exemple | RG |
|---|---|---|---|---|---|
| ENT_SOP | SOP (Procédure Opératoire Standard) | | | | [Sop.cs](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/Sop.cs#L74-L135) |
| PRO_SOP_ID | IdSOP | ID | | GUID | |
| PRO_SOP_ENV_ID | IdEnvironnement | ID | FK → ENVIRONNEMENT | | |
| PRO_SOP_CODE | CodeStable | A(30) | Stable à travers versions | SOP-N4-SPLITBRAIN-01 | |
| PRO_SOP_TITRE | Titre | A(300) | | Résolution Split-Brain Center/Standby | |
| PRO_SOP_VERSION | Version | N(4) | +1 si modifié après exécution | 2 | RG-0702 |
| PRO_SOP_STATUT | StatutCycleVie | A(20) | Brouillon→Actif | | RG-0703 |
| PRO_SOP_DEJAEXEC | DejaExecuteAuMoins1Fois | B | → immuable après | | RG-0702 |
| PRO_SOP_ROLELEVE | RequiertRoleSensible | B | PeutExecuterActionsSensibles nécessaire | true RG-0704 |
| PRO_SOP_OBJECTIF | Objectif | A(2000) | Structuré FR-088 | | RG-0701 |
| PRO_SOP_PERIMETRE | Perimetre | A(2000) | | | |
| PRO_SOP_PREREQUIS | Prerequis | A(4000) | | | |
| PRO_SOP_RISQUES | RisquesEncourus | A(4000) | | | |
| PRO_SOP_CONTROLES | ControlesPostOperation | A(4000) | | | |
| PRO_SOP_RESULTAT | ResultatAttendu | A(2000) | | | |
| PRO_SOP_ROLLBACK | PlanRetourArriere | A(4000) | | | |
| PRO_SOP_ESCALADE | CheminEscalade | A(2000) | Equipe Infra 06 00 00 00 / Support Navis priority-high | | |
| PRO_SOP_VERSIONN4 | AppliqueVersionN4 | A(50) | 3.8.x, 4.9.x, all | | RG-0701 |
| PRO_SOP_DOCSRC_ID | IdDocumentSourceSiGenereDepuisDoc | ID | FK nullable → DOCUMENT | | FR-088 |
| PRO_SOP_EXESRC_ID | IdExecutionSourceSiGenereDepuisWorkflow | ID | FK nullable → EXÉCUTION | | FR-089B |
| PRO_SOP_SESSRCS_ID | IdSessionDiagnosticSourceSiGenereDepuisIncident | ID | FK nullable → SESSION DIAGNOSTIC | | FR-089B |

| Code | Propriété | Type | Description | Valeurs / Exemple | RG |
|---|---|---|---|---|---|
| ENT_SOPEX | EXÉCUTION SOP | | | | [SopExecution](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/Sop.cs#L187-L231) |
| PRO_SOPEX_ID | IdExecutionSOP | ID | | GUID | |
| PRO_SOPEX_SOP_ID | IdSOP | ID | FK → SOP | | |
| PRO_SOPEX_SOPVERS | VersionSOPFigee | N(4) | Figée audit | | |
| PRO_SOPEX_SOPCODE | CodeSOPFigee | A(30) | Figée audit | | |
| PRO_SOPEX_SOPNOM | TitreSOPFigee | A(300) | Figée audit | | |
| PRO_SOPEX_ENV_ID | IdEnvironnement | ID | FK → ENVIRONNEMENT | | |
| PRO_SOPEX_ENVCODE | CodeEnvironnementFigé | A(20) | Figé audit | | |
| PRO_SOPEX_STATUT | Statut | A(15) | EnCours / Termine / Abandonne | | |
| PRO_SOPEX_PAR | ExecutePar | A(150) | | | |
| PRO_SOPEX_MOTIF | MotifExecution | A(1000) | | Incident P1 à 08h32 | |
| PRO_SOPEX_TICKET | ReferenceTicket | A(50) | | INC-xxx | |
| PRO_SOPEX_ALT_ID | IdAlerteSourceDeclenchement | ID | FK nullable → ALERTE | | |
| PRO_SOPEX_SES_SRC_ID | IdSessionDiagnosticSourceSiDeclenche | ID | FK nullable → SESSION DIAGNOSTIC | | |
| PRO_SOPEX_DEBUTLE | DebutLe | D | | | |
| PRO_SOPEX_FINLE | FinLe | D | Null si en cours | | |
| PRO_SOPEX_MOTIFABANDON | MotifAbandon | A(2000) | Si statut Abandonne | | |
| PRO_SOPEX_RESULTAT_FINAL | ResultatFinalAtteste | A(20) | ProblemeResolu / ProblemeNonResolu / Indetermine | | RG-0705 (HUMAIN SEUL) |
| PRO_SOPEX_RESULTAT_NOTE | NoteResultat | A(2000) | Détails attestation | | |
| PRO_SOPEX_RESULTAT_PAR | ResultatAttestePar | A(150) | | | RG-0705 |
| PRO_SOPEX_RESULTAT_LE | ResultatAttesteLe | D | | | RG-0705 |
| PRO_SOPEX_CORRELID | IdCorrelation | A(20) | | | |
| PRO_SOPEX_TAUXREUSSI_HIST | TauxReussiteHistoriqueSOPAuLancement_Pct | N(5,2) | Valeur informative au moment de lancer | 78.45 | RG-0706 |

| Code | Propriété | Type | Description | Valeurs / Exemple | RG |
|---|---|---|---|---|---|
| ENT_SOPEXETP | ÉTAPE EXÉCUTION SOP | | | | [SopExecutionStep](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/Sop.cs#L234-L286) |
| PRO_SOPEXETP_ID | IdEtapeExecutionSOP | ID | | GUID | |
| PRO_SOPEXETP_EXESOP_ID | IdExecutionSOP | ID | FK → EXÉCUTION SOP | | |
| PRO_SOPEXETP_ORDRE | Ordre | N(4) | Figé | | |
| PRO_SOPEXETP_NOM | NomEtape | A(200) | Figé | | |
| PRO_SOPEXETP_INSTRUCTION | InstructionFigee | A(2000) | Figée | | RG-0701 (pas auto) |
| PRO_SOPEXETP_RES_ATTENDU | ResultatAttenduFigé | A(1000) | Figé | | |
| PRO_SOPEXETP_CMP_ID | IdComposantCible | ID | FK nullable → COMPOSANT | | |
| PRO_SOPEXETP_CMPNOM | NomComposantFige | A(200) | Figé audit | | |
| PRO_SOPEXETP_STATUT | Statut | A(20) | AVenir / EnCours / Confirmee / Ecart / Ignoree / Annulee | | RG-0701 |
| PRO_SOPEXETP_DEBUTLE | DebutLe | D | | | |
| PRO_SOPEXETP_FINLE | FinLe | D | | | |
| PRO_SOPEXETP_CONTOURNABLE | EstContournableFige | B | | | |
| PRO_SOPEXETP_CONFIRMPAR | ConfirmationPar | A(150) | HUMAIN SEUL | | RG-0701 |
| PRO_SOPEXETP_CONFIRMLE | ConfirmationLe | D | HUMAIN SEUL | | RG-0701 |
| PRO_SOPEXETP_PREUVE | PreuveConstateeOperateur | A(4000) | Saisie HUMAINE SEULE | | |
| PRO_SOPEXETP_ECART_NOTE | NoteEcartParRapportAttendu | A(2000) | | | |
| PRO_SOPEXETP_CONTOUR_PAR | ContournementPar | A(150) | | | |
| PRO_SOPEXETP_CONTOUR_MOTIF | ContournementMotif | A(1000) | | | |
| PRO_SOPEXETP_HISTORIQUE | HistoriqueRouvertures | A(8000) | Archivage confirmations précédentes si réouverte | | RG-0702 immuable |

| Code | Propriété | Type | Description | Valeurs / Exemple | RG |
|---|---|---|---|---|---|
| ENT_SOPASSOC | ASSOCIATION SOP → Composant / Signature / SessionDiag | | | | [SopAssociation](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/Sop.cs#L168-L182) |
| PRO_SOPASSOC_ID | IdAssociation | ID | | GUID | |
| PRO_SOPASSOC_SOP_ID | IdSOP | ID | FK → SOP | | |
| PRO_SOPASSOC_TYPE | NatureAssociation | A(30) | Composant / Signature / SessionDiagnostic | | |
| PRO_SOPASSOC_CMP_ID | IdComposant | ID | FK nullable → COMPOSANT | | |
| PRO_SOPASSOC_CMP_ROLE | RoleComposantSuggere | A(30) | Si associé par rôle | | |
| PRO_SOPASSOC_SIGDIAG_ID | IdSignature | ID | FK nullable → SIGNATURE ANOMALIE | | |
| PRO_SOPASSOC_SIGDIAG_CODE | CodeSignature | A(50) | | | |
| PRO_SOPASSOC_SES_DIAG_ID | IdSessionDiagnostic | ID | FK nullable → SESSION DIAGNOSTIC | | |

---

### Catégorie : Audit, Secrets, Crédentiels, Rétentions

| Code | Propriété | Type | Description | Valeurs / Exemple | RG |
|---|---|---|---|---|---|
| ENT_AUD | JOURNAL D'AUDIT (immuable) | | | | [AuditEntry.cs](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/AuditEntry.cs) / SEC-008 |
| PRO_AUD_ID | IdEntreeAudit | ID | | GUID | |
| PRO_AUD_LE | HorodatageEvenement | D | | | |
| PRO_AUD_ACTION | Action | A(30) | Connexion / DeconnexionOuEchecAuth / Creation / Modification / Suppression / ChangementDeStatut / TestDeConfiguration / TentativeNonAutorisee / ExecutionOperation / Contournement / ArretForce | | SEC-008 |
| PRO_AUD_ISSUE | Issue | A(10) | Succes / Refuse / Echec | | RG-0805 |
| PRO_AUD_CATEGORIE | CategorieEntite | A(100) | Type entité concernée | Composant, Workflow... | |
| PRO_AUD_ENTITE_ID | IdEntiteConcernee | A(100) | String nullable GUID | | |
| PRO_AUD_PAR | EvenementParIdentite | A(255) | Compte nominatif ou "SYSTEM" | | SEC-008 |
| PRO_AUD_IP | AdresseIpClient | A(50) | Si disponible | | |
| PRO_AUD_HTTP_METHOD | MethodeHttpOuOperation | A(20) | PUT, POST, Start, Stop... | | |
| PRO_AUD_CHEMIN | CheminRessourceOuOperation | A(500) | /api/components/{id} ou WF step 3 | | |
| PRO_AUD_ANCIENVAL | ValeursAvantChangementsJSON | A(8000) | Masqué si secrets | | RG-0805 append-only |
| PRO_AUD_NOUVVAL | ValeursApresChangementsJSON | A(8000) | Masqué si secrets | | RG-0805 append-only |
| PRO_AUD_MESSAGE | DescriptionEvenement | A(1000) | Libellé humain | | |
| PRO_AUD_CORRELID | IdCorrelation | A(20) | Si opération liée | | |
| PRO_AUD_ENV_ID | IdEnvironnementConcerne | ID | FK nullable → ENVIRONNEMENT | | SEC-004 |
| PRO_AUD_ENV_CODE | CodeEnvironnementFige | A(20) | Figé audit | | |

| Code | Propriété | Type | Description | Valeurs / Exemple | RG |
|---|---|---|---|---|---|
| ENT_CREDPROF | PROFIL CRÉDENTIEL TECHNIQUE | | | | [TechnicalCredential.cs](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/TechnicalCredential.cs) / SEC-003 |
| PRO_CREDPROF_ID | IdProfilCredential | ID | | GUID | |
| PRO_CREDPROF_ENV_ID | IdEnvironnement | ID | FK → ENVIRONNEMENT | | |
| PRO_CREDPROF_SRV_ID | IdServeur | ID | FK → SERVEUR | | |
| PRO_CREDPROF_NOM | NomUsage | A(200) | Compte service Windows Bridge | | |
| PRO_CREDPROF_TYPEAUTH | TypeAuthentification | A(30) | CompteDomaine / CompteLocal / SqlLogin / Certificat / SSHKey | | SEC-003 |
| PRO_CREDPROF_LOGIN | IdentifiantStockeProtege | A(500) | Chiffré / DPAPI / coffre | ⚠️ JAMAIS en clair | RG-0802 |
| PRO_CREDPROF_MDPCHIFFRE | MotDePasseChiffre | BLOB | DPAPI ou Key Vault | ⚠️ JAMAIS en clair | SEC-003 |
| PRO_CREDPROF_EMPREINTE | EmpreinteCertificatOuIdentite | A(200) | Empreinte thumbprint | | |
| PRO_CREDPROF_VALIDEDEPUIS | ValideDepuisLe | D | | | |
| PRO_CREDPROF_EXPIRELE | ExpireLe | D | Alerte expir 30j avant | | SEC-003 |
| PRO_CREDPROF_UTILISATIONCHEMIN | UtilisationCheminDescription | A(500) | Sert à lancer le service Bridge via connecteur PS | | |
| PRO_CREDPROF_STATUT | EstActif | B | | | |

| Code | Propriété | Type | Description | Valeurs / Exemple | RG |
|---|---|---|---|---|---|
| ENT_RET | POLITIQUE RÉTENTION PAR CATÉGORIE DONNÉES | | | | [RetentionPolicy.cs](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/RetentionPolicy.cs) / SEC-009 |
| PRO_RET_ID | IdPolitiqueRetention | ID | | GUID | |
| PRO_RET_CODE | CodeCategorieDonnees | A(50) | LOGS_TECH, RAPPORTS, AUDITS, SOP, WORKFLOW_HIST | | RG-0806 |
| PRO_RET_NOM | NomLibelle | A(200) | Journaux techniques collectés | | |
| PRO_RET_DUREEJOURS | DureeConservationJours | N(6) | | 30, 365, 2555 (7 ans) | SEC-009 |
| PRO_RET_ACTIONFIN | ActionFinDeVie | A(30) | Supprimer / Archiver / Anonymiser | | |
| PRO_RET_CHEMINARCHIVAGE | CheminArchivageSiNecessaire | A(400) | | \\\\SRV-ARCH01\retention\n4sentinel\ | | |
| PRO_RET_STATUT | EstActivee | B | | true | |

---

### Catégorie : AMÉLIORATIONS AJOUTÉES — APR, MÉTRIQUES BUSINESS, INCIDENTS HISTORIQUE CAPITALISÉ

| Code | Propriété | Type | Description | Valeurs / Exemple | RG |
|---|---|---|---|---|---|
| ENT_APR | ACTION PRÉVENTIVE RECOMMANDÉE (APR) — tableau de bord prévention | | | | Amélioration Axe2 RG-0904 |
| PRO_APR_ID | IdActionPreventive | ID | | GUID | |
| PRO_APR_ENV_ID | IdEnvironnement | ID | FK → ENVIRONNEMENT | | |
| PRO_APR_CODE | CodeReference | A(50) | APR-IDX-REBUILD-2026-0912 | | |
| PRO_APR_LIBELLE | Libelle | A(300) | Rebuild index INV_WQ fragmenté à 34% | | RG-0902 |
| PRO_APR_DESCRIPTION | DetailJustificatif | A(4000) | Tendance + preuves | | RG-09xx |
| PRO_APR_SEUIL_DECLENCH | ValeurSeuilDeclenchementMesure | A(50) | FragmentationPct > 30 | | RG-09xx |
| PRO_APR_MESURE_ACTUELLE | ValeurActuelleMesuree | A(50) | "34.7%" ou "1.2s écart horloge" | | RG-09xx |
| PRO_APR_RISQUE_P1_PCT | RisqueP1EviteSiFaitePourcent | N(3) | 0-100 | 85 | RG-0904 |
| PRO_APR_DUREE_ESTIM_MIN | DureeOperationEstimeeMin | N(4) | | 14 | RG-0904 |
| PRO_APR_IMPACT_METIER | ImpactMetierSiFaite | A(500) | "Aucun : hors heures ouvrées" | | APR |
| PRO_APR_FENETRE_SUGG_DEB | FenetreSuggereeDebut | D | 2026-09-13 22:00 | RG-0904 |
| PRO_APR_FENETRE_SUGG_FIN | FenetreSuggereeFin | D | 2026-09-13 23:59 | |
| PRO_APR_WKF_SUGG_ID | IdWorkflowSuggereSiApplicable | ID | FK nullable → WORKFLOW | | Pour raccourci |
| PRO_APR_ALT_SOURCE | IdAlerteSourceSiAlertePredictive | ID | FK nullable → ALERTE | | |
| PRO_APR_CREE_LE | GenereLe | D | | | |
| PRO_APR_STATUT | Statut | A(20) | Nouvelle / Programme / Realisee / Ignoree / Expiree | | Amélioration Axe2 |
| PRO_APR_PROG_PAR | ProgrammeePar | A(150) | | | |
| PRO_APR_PROG_LE | ProgrammeeLe | D | | | |
| PRO_APR_REALI_PAR | RealiseePar | A(150) | | | |
| PRO_APR_REALI_LE | RealiseeLe | D | | | |
| PRO_APR_REALI_EXE_ID | IdExecutionWorkflowRealisation | ID | FK nullable → EXÉCUTION si via Sentinel | | |
| PRO_APR_IGNORE_PAR | IgnoreePar | A(150) | | | |
| PRO_APR_IGNORE_MOTIF | MotifIgnorance | A(2000) | OBLIGATOIRE si ignoré | | |

| Code | Propriété | Type | Description | Valeurs / Exemple | RG |
|---|---|---|---|---|---|
| ENT_INC | FICHE INCIDENT CAPITALISÉ (mémoire organisationnelle) | | | | Amélioration Axe4 |
| PRO_INC_ID | IdIncidentCapitalise | ID | | GUID | |
| PRO_INC_CODE | ReferenceIncident | A(50) | Ticket ITSM CIV | P1-INC-2026-0912-0012 | |
| PRO_INC_ENV_ID | IdEnvironnement | ID | FK → ENVIRONNEMENT | | |
| PRO_INC_NIVEAU | GraviteDeclenchement | A(10) | P1 / P2 / P3 / P4 | | MTTR |
| PRO_INC_SYMPTOME | SymptomeInitialDecrit | A(2000) | Terminalistes ne reçoivent plus instructions | | |
| PRO_INC_DEBUTREELLE | DebutIncidentConstateLe | D | | | MTTR |
| PRO_INC_FINREELLE | FinIncidentConstateLe | D | | | MTTR |
| PRO_INC_DUREETOTALE_MIN | DureeTotaleIncidentMinutes | N(8) | Calculé | | RG-1001 |
| PRO_INC_DETECTIONLE | DetectionParSentinelOuOpsLe | D | | | |
| PRO_INC_CAUSE_PRINC | CausePrincipaleId | A(50) | CauseP1 #5 Horloges | | RG-1001 catégorisation |
| PRO_INC_CAUSE_PRINC_LIB | CausePrincipaleLibelle | A(500) | | Ecart horloges à 2.3s entre serveurs | | |
| PRO_INC_CAUSE_RACINE | CauseRacine5Pourquoi | A(8000) | Méthode 5 pourquoi | | PostMortem |
| PRO_INC_ACTION_CORREC | ActionsCorrectricesMenees | A(8000) | | | |
| PRO_INC_SES_DIAG_ID | IdSessionDiagnosticRattachee | ID | FK nullable → SESSION DIAGNOSTIC | | |
| PRO_INC_EXE_WKF_ID | IdExecutionWorkflowRattachee | ID | FK nullable → EXÉCUTION | | |
| PRO_INC_SOP_UTILISES | SOPsUtilisesListeString | A(1000) | SOP-SPLITBRAIN-01 v2 | | |
| PRO_INC_SOP_GENERE_ID | IdSOPSuiteIncidentGenere | ID | FK nullable → SOP | | FR-089B |
| PRO_INC_IMPACT_METIER_TXT | ImpactMetierQuantifie | A(2000) | 38 berlines impactées, retard 45min sur navire | | |
| PRO_INC_MESURES_COMP | MesuresCompensatoiresMisesEnOeuvre | A(4000) | Dispatch manuel TOS Manager | | |
| PRO_INC_NB_OPERATEURS_IMPLIQUES | NombreOperateursImpliques | N(2) | | 5 | RG-1003 |
| PRO_INC_NIVEAU_RESOLU_PAR | NiveauCompetenceResolution | A(20) | N1Support / N2Operateur / N3ExpertN4 / ExpertExterneNavis | RG-1003 | |
| PRO_INC_CLOS_PAR | IncidentClosPar | A(150) | | | RG-05 Cloture |
| PRO_INC_CLOS_LE | IncidentClosLe | D | | | |
| PRO_INC_POSTMORTEM_RAPPORT | LienPostMortemDocument | A(500) | FK ou chemin vers DOCUMENT | | Capitalisation |
| PRO_INC_LESSONS_LEARNED | LeconsTireesEtActionsCorrectives | A(8000) | Mettre en place supervision NTP dédiée + alerte seuil 0.8s | Amélioration continue | |

| Code | Propriété | Type | Description | Valeurs / Exemple | RG |
|---|---|---|---|---|---|
| ENT_METBUS | MÉTRIQUE BUSINESS SUCCÈS (agrégats historiques pour tableaux de bord) | | | | Amélioration Axe4 RG-10 |
| PRO_METBUS_ID | IdMesureBusiness | ID | | GUID | |
| PRO_METBUS_PERIODE | PeriodeReference | A(10) | Mois-2026-09 / Sem-37 | | |
| PRO_METBUS_ENV_ID | IdEnvironnement | ID | FK nullable → ENVIRONNEMENT (NULL=global) | | |
| PRO_METBUS_CODE | CodeMesure | A(50) | MTTR_P1_GLOBAL, NB_P1_PAR_MOIS, PCT_P1_RESOLUS_N2, TAUX_REUSSITE_WKF, NB_APR_REALISEES_VS_IGNOREES | RG-10xx | |
| PRO_METBUS_LIBELLE | LibelleMesure | A(200) | | MTTR moyen incidents P1 | |
| PRO_METBUS_VALEUR_NUM | ValeurNumeriqueMoyenneOuSomme | N(18,4) | 23.5 (minutes pour MTTR) | RG-1001 |
| PRO_METBUS_CIBLE_NUM | ValeurCibleFixee | N(18,4) | 12 (cible divisé par 2) | | RG-10 Objectif |
| PRO_METBUS_BASELINE_NUM | ValeurBaselineAvantSentinel | N(18,4) | 47 min (historique) | | Mesure progrès |
| PRO_METBUS_TENDANCE | TendancePeriodePrecedente_Pct | N(6,2) | -12.3 = amélioration 12.3% vs période N-1 | | Suivi |
| PRO_METBUS_AGREGEEPAR | TypeAgregation | A(20) | Moyenne / Somme / Mediane / Pourcentage | | |
| PRO_METBUS_CALCULELE | CalculeeLe | D | | | |
| PRO_METBUS_ECHANTILLON | TailleEchantillonN | N(8) | Nombre incidents/opérations | 18 P1 en septembre | | Fiabilité statistique |

---

### Catégorie : Actions externes déclarées (hors Sentinel) / EDI Tracking

| Code | Propriété | Type | Description | Valeurs / Exemple | RG |
|---|---|---|---|---|---|
| ENT_ACT_EXT | ACTION EXTERNE DÉCLARÉE (hors Sentinel) | | | | [ExternalActionDeclaration.cs](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/ExternalActionDeclaration.cs) |
| PRO_ACT_EXT_ID | IdActionExterne | ID | | GUID | |
| PRO_ACT_EXT_TYPE | TypeSupport | A(30) | ExecutionWorkflow / SessionDiagnostic / SessionDiagEtapeBloquee / SopExecution | | Multi |
| PRO_ACT_EXT_REF1_ID | IdEntiteSupport1 | ID | FK vers l'entité support principale | | |
| PRO_ACT_EXT_REF2_ID | IdEntiteSupport2 | ID | FK vers entité secondaire si étape | | |
| PRO_ACT_EXT_QUOI | DescriptionActionMeneeHorsSentinel | A(2000) | Reboot manuel VM via vCenter | | §3.19 |
| PRO_ACT_EXT_PAR | RealiseePar | A(150) | | | §3.19 |
| PRO_ACT_EXT_LE | RealiseeLe | D | | | §3.19 |
| PRO_ACT_EXT_PREUVE | PreuveConstatee | A(4000) | | | §3.19 |
| PRO_ACT_EXT_IMPACT | ImpactSurEtatCourant | A(2000) | "Centre a changé de rôle ACTIVE → Standby" | Réconciliation |
| PRO_ACT_EXT_CORRELID | IdCorrelation | A(20) | | | Traçabilité |

| Code | Propriété | Type | Description | Valeurs / Exemple | RG |
|---|---|---|---|---|---|
| ENT_EDIFIC | FICHIER EDI TRACKÉ | | | | EDI Tracking §3.8 CdC |
| PRO_EDIFIC_ID | IdFichierEdi | ID | | GUID | |
| PRO_EDIFIC_ENV_ID | IdEnvironnement | ID | FK → ENVIRONNEMENT | | |
| PRO_EDIFIC_DOSP_ID | IdDossierPartageSource | ID | FK nullable → COMPOSANT profil dossier partagé EDI | | CdC §3.8 |
| PRO_EDIFIC_NOMFICHIER | NomFichier | A(255) | | | RG-0705 |
| PRO_EDIFIC_PARTENAIRE | PartenaireExtraitOuSaisi | A(200) | MSC, MAERSK... | | RG-0705 EdiFileNamingPattern |
| PRO_EDIFIC_TYPE | TypeMessageEdi | A(100) | CODECO, COARRI, IFTMBF... | | |
| PRO_EDIFIC_REÇULE | RecuSurDossierLe | D | Date détection sur dossier | | §3.8 |
| PRO_EDIFIC_INTEGRELE | IntegreDansBddLe | D | Si OK | | §3.8 |
| PRO_EDIFIC_STATUT | StatutTraitement | A(20) | Recu / EnAttente / Integre / Rejete / Bloque / EnErreurTraitement | §3.8 | |
| PRO_EDIFIC_NB_TENTATIVES | NombreTentativesIntegration | N(3) | | | Alerte si ≥3 échecs |
| PRO_EDIFIC_ERREUR | DernierMessageErreur | A(4000) | | | §3.8 |
| PRO_EDIFIC_SIGN_DIAG_ID | IdSignatureRapprocheeSiErreur | ID | FK nullable → SIGNATURE ANOMALIE | | Diagnostic |
| PRO_EDIFIC_SESSIONDIAG_ID | IdSessionDiagnosticOuverteSiPblm | ID | FK nullable → SESSION DIAGNOSTIC | | §3.8 |
| PRO_EDIFIC_CHEMINFINAL | CheminFinalApresTraitement | A(300) | \\...\consumed\ ou \error\ | | |

---

### Catégorie : Approbation Matrix (niveau criticité × environnement × action)

| Code | Propriété | Type | Description | Valeurs / Exemple | RG |
|---|---|---|---|---|---|
| ENT_APROBMAT | RÈGLE MATRICE APPROBATION | | | | [ApprovalMatrixRule.cs](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/ApprovalMatrixRule.cs) |
| PRO_APROBMAT_ID | IdRegleApprobation | ID | | GUID | |
| PRO_APROBMAT_ENV_ID | IdEnvironnement | ID | FK → ENVIRONNEMENT ; NULL = défaut global | | SEC-004 |
| PRO_APROBMAT_ROLE_CIBLE | AppliqueARoleComposant | A(30) | NULL = tous | | RG-0103 |
| PRO_APROBMAT_CRITICITE | AppliqueACriticite | A(15) | NULL = toutes | | RG-0304 |
| PRO_APROBMAT_ACTION | AppliqueATypeActionWorkflow | A(30) | ArretComplet... NULL = toutes | | |
| PRO_APROBMAT_NOMBRE_APPROB | NombreApprobationsRequises | N(1) | 1 simple, 2 double | | RG-0304 |
| PRO_APROBMAT_ROLE_APPROB | RoleApplicatifRequisPourApprouver | A(50) | Validateur, AdminN4... | | RG-0801 séparation |
| PRO_APROBMAT_INCLURE_CLOISON_OPERATEUR | OperateurDoitEtreDifferent | B | Toujours true en Prod | true | RG-0801 séparation devoirs |
| PRO_APROBMAT_DELAIVALIDMIN | DélaiValiditeApprobationMinutes | N(5) | 120 = 2h validité | | Évite approbations périmées |
| PRO_APROBMAT_ACTIF | EstActif | B | | true | |

---

## Fin du Dictionnaire de Données — 45 entités / ~450 propriétés
