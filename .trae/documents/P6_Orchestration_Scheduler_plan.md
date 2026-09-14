# P6 — Planification Maintenance N4 + Calendrier + Workflows prédéfinis §3.10.2 + WatchFolder léger FR-059

## Repository Research (état existant avant P6)

### Enums & domaines
- `LifecycleStatus` : Brouillon=0..Revoque=5 (P5 livré). `AuditAction` : queue=12 Revocation.
- `AprStatus` (APR P2) : Detectee..Expiree. `ScheduleStatus` / `ScheduleKind` n'existent PAS.
- `ComponentRole` : 11 rôles N4 BaseDeDonnees..InterfaceEdi. Ordres `RangDemarrage` / `RangArret` dans WorkflowService.cs:L543/L577.
- `WorkflowKind` : DemarrageComplet/ArretComplet/ControleSeul/OperationPartielle/BasculeCenter — on ajoutera MaintenanceHebdo/MaintenanceMensuelle/RedemarrageComplet.
- Pas d'entité `ExecutionSchedule`, `MaintenanceHoliday`, `ScheduleRun`. Pas de `WorkflowExecution.TriggeredById` / `.TriggeredByScheduleId`.

### Services référentiels existants (NE PAS TOUCHER)
- `OrchestrationBackgroundService` : 20s veille / 3s active. S'occupe du moteur. N'ÉCOUTE PAS les programmations. P6 ajoute **un hosted service séparé** `SchedulerTriggerBackgroundService` — pas de modification du moteur.
- `AprScheduleUseCase` : 12 lignes, programme ScheduledAt/ScheduledBy sur une APR unique. N'est PAS le planificateur récurrent. P6 ne le modifie pas.
- `AprBackgroundService` : background indépendant. P6 ajoute un autre hosted service séparé.
- `WorkflowService.CreatePredefinedWorkflowsAsync` : déjà 6 workflows (Démarrage/Arrêt/Contrôle complets + Redémarrage Interfaces/Pupitre + Bascule Center). P6 ajoute 3 workflows (REDEMARRAGE-COMPLET §3.10.2, CONTROLE-HEBDOMADAIRE, MAINTENANCE-MENSUELLE).
- `SharedFolder`/`N4Component.SharedFolders` déclaration + `EdiTrackingService` + `AlertService` FR-059I existent. Pas de Background ingestion léger. P6 ajoute `EdiWatchFolderLightBackgroundService`.
- `PreventiveRecommendedActions` (APR) existe. P6 crée `ExecutionSchedules` + `MaintenanceHolidays` DbSets séparés.

### Build + constraints existants
- Pas de `.sln` → `dotnet build N4Sentinel.slnx --no-restore`.
- 11 erreurs tests préexistantes (CTOR args logger/merisePolicies) → HORS SCOPE.
- 9 warnings baseline (CS8601×2 + CS9113×3 + EF1002×2 + CS0649 + BL0008) → P6 0 new warning.
- GetDiagnostics vide requis.
- Migrations Sqlite : EF conventions suffisent pour runtime, pas de migration forcée (option utilisateur post-P6).

## Files and Modules à modifier/ajouter

P6 scoped strictement à Domain + Infrastructure. Tests, Web, Razor = NON touchés.

### Domain (ajouts uniquement, pas de modif d'existant autre que champs FK)
1. `N4Sentinel.Domain/Enums.cs` : tail enum values ajoutés EN QUEUE.
   - `ScheduleKind` (Quotidien=0 / JoursOuvres=1 / HebdoJoursChoisis=2 / MensuelJoursPrecis=3 / CronExpression=4).
   - `ScheduleStatus` (Actif=0 / Suspendu=1 / Expire=2).
   - `ExecutionTriggeredBy` (Manuel=0 / Planification=1 / RepriseApresCrash=2 / APRAutomatique=3).
   - `AuditAction.PlanificationCreee=13` / `PlanificationModifiee=14` / `PlanificationDeclenchee=15` / `PlanificationSuspendue=16`.
2. `N4Sentinel.Domain/WorkflowExecution.cs` : ajouter 2 champs Nullable (pas cassant DB convention EF) :
   - `Guid? TriggeredByScheduleId` (FK vers ExecutionSchedule, nullable).
   - `ExecutionTriggeredBy TriggeredBy` (default Manuel).
3. NOUVEAU `N4Sentinel.Domain/Scheduling/ExecutionSchedule.cs` : Entité planification.
4. NOUVEAU `N4Sentinel.Domain/Scheduling/MaintenanceHoliday.cs` : Entité jour férié/exclu par environnement.
5. `N4Sentinel.Infrastructure/Persistence/N4SentinelDbContext.cs` : ajouter DbSets `ExecutionSchedules` + `MaintenanceHolidays`.

### Infrastructure (nouveaux services + modifications ciblées)
6. NOUVEAU `N4Sentinel.Infrastructure/Scheduling/MaintenanceScheduleService.cs` : CRUD planifications, activer/suspendre, GetNextOccurrences(n occurrences), gardes anti-conflit env.
7. NOUVEAU `N4Sentinel.Infrastructure/Scheduling/MaintenanceCalendarService.cs` : ajouter/supprimer jour férié, checker si un DateTime en est un.
8. NOUVEAU `N4Sentinel.Infrastructure/Scheduling/SchedulerTriggerBackgroundService.cs` : IHostedService, 60s cadence, calcule déclenchements échus → appelle `ExecutionService.PrepareAsync` + si `AutoApprove=true` alors `ApproveAsync` (acteur = `Schedule.RanAs`). Skip jour férié. Skip si `WorkflowId` a déjà une exécution `IsActive=true` dans le même environnement. Audit PlanificationDeclenchee.
9. MODIF `N4Sentinel.Infrastructure/Orchestration/WorkflowService.cs` : ajouter 3 workflows prédéfinis dans `CreatePredefinedWorkflowsAsync` (après BASCULE-CENTER, avant `if (crees.Count==0)`) :
   - `REDEMARRAGE-COMPLET` : séquence Arrêt complet ordre Kaleris §3.10.2 (Standby avant Center, Clusters avant Center) PUIS Démarrage (Cluster→Center→Standby→Bridge→XPS→ECN4→Web). Utilise `CreerRedemarragePartielAsync` pattern OU inline dédié.
   - `CONTROLE-HEBDOMADAIRE` : 1 étape Contrôle 10 post-démarrage enrichie + toutes vérifications composants Verifier + vérifications santé DB + EDI synchronisation health (FR-056) + disque libre FR-054.
   - `MAINTENANCE-MENSUELLE-N4` : 1 étape manuelle vérification Windows Update + snapshots + purge logs temporaires + contrôle 10 + redémarrage sélectif interfaces.
10. NOUVEAU `N4Sentinel.Infrastructure/Edi/EdiWatchFolderLightBackgroundService.cs` : 30s cadence. Scanne tous SharedFolder déclarés d'un environnement. Vérifie les nouveaux fichiers détectés. Si l'un est en statut Echec plusieurs fois → appelle `DiagnosticSessionService` existant avec FR-059J. Sinon écrit juste l'alerte via `AlertService.OpenAsync` existant si FichierEnAttente depasse `StalenessThresholdSeconds`.
11. MODIF `N4Sentinel.Infrastructure/DependencyInjection.cs` :
    - AddScoped `MaintenanceScheduleService`, `MaintenanceCalendarService`.
    - AddHostedService `SchedulerTriggerBackgroundService`.
    - AddHostedService `EdiWatchFolderLightBackgroundService`.

## Implementation Steps (ordre de dépendance)

1. **P6 Step 1 — Domain enums + entités + DbSets** (1 fichier modif + 2 fichiers nouveau + 1 DbSet)
   - Enqueue 4 nouvelles valeurs AuditAction, 3 enums ScheduleKind/Status/TriggeredBy.
   - Ajouter 2 champs Nullable `TriggeredByScheduleId` / `TriggeredBy` WorkflowExecution.
   - Créer entité ExecutionSchedule (Guid Id, EnvironmentId, WorkflowId, Code, Name, ScheduleKind, TimeOfDay Local TimeSpan, WeeklyDayOfWeekMask DayOfWeekFlags ushort 0x1=Lun..0x40=Dim ou List<DayOfWeek> simple, MonthlyDays ICollection<int> 1..31, CronExpression string?, Status ScheduleStatus, RanAs string acteur, ReasonTemplate, TicketTemplate, AutoApprove bool, StartAt/EndAt ValidFrom/ValidUntilNullable, LastTriggeredAt, NextPlannedAt, MaxConcurrentSameEnv bool default true). Hériter AuditableEntity.
   - Créer MaintenanceHoliday (Id, EnvironmentId, Date DateOnly, Reason, CreatedBy/At AuditableEntity).
   - Mettre 2 DbSets dans N4SentinelDbContext.

2. **P6 Step 2 — Services métier planification**
   - `MaintenanceScheduleService` : CreateAsync avec code unique par env + garde workflow Valide/Actif, UpdateAsync, DeleteAsync (soft si jamais déclenché, hard sinon), EnableAsync/DisableAsync bascule Status, GetNextOccurrencesAsync(scheduleId, n=10) rend Liste<DateTimeOffset> prochaines occurrences. Garde anti-conflit : 2 planifications du même WorkflowId dans même fenêtre ≤ 2h intervalle min => Warning string, pas blocage dur.
   - `MaintenanceCalendarService` : AddHoliday/RemoveHoliday/ListHolidays/IsHolidayAsync(environmentId, DateTimeOffset local).

3. **P6 Step 3 — Déclencheur Background planifications**
   - `SchedulerTriggerBackgroundService` : 8s warmup comme Orchestration. Boucle 60s. Par environnement actif : charge schedules Status=Actif, ValidFrom<=Now && (ValidUntil==null || Now<=ValidUntil), date DU JOUR hors holiday, TimeOfDay ≤ Now (en TimeZone Info.Local — configurable Static TimeZoneId). Si LastTriggeredAt < Today + TimeOfDay (même jour) => déclencher. Appel Scoped ExecutionService.PrepareAsync(requestedBy=schedule.RanAs, reason=schedule.ReasonTemplate ?? $"Planification [{schedule.Code}] automatique.", startWindow null). Si prepare return null success && schedule.AutoApprove && ExecutionStatus == EnAttenteApprobation → ApproveAsync. Mettre LastTriggeredAt=DateTimeOffset.Now. Audit PlanificationDeclenchee. Catch exception sans casser la loop.

4. **P6 Step 4 — Workflows prédéfinis nouveaux**
   - `REDEMARRAGE-COMPLET` ordre Arret exact (RangArret Ecn4Web=0 / Ecn4=1 / Xps=2 / BridgeDaemon=3 / StandbyCenter=4 / ClusterNode=5 / CenterNode=6 / BDD=7 / AMQ=8) PUIS Demarrage exact RangDemarrage. Inclure Confirmations clients avant arret et après demarrage.
   - `CONTROLE-HEBDOMADAIRE` nature WorkflowKind.MaintenanceHebdo. Étapes Verifier pour chaque composant CanBeControlled, + DB health via `SupervisionDatabaseHealthService` si dispo, + contrôles manuels Contrôle 10 post + NTP check + Alertes ouvertes revue.
   - `MAINTENANCE-MENSUELLE-N4` nature WorkflowKind.MaintenanceMensuelle : Windows Update review (manuel), snapshot OS (manuel), purge journaux temporaires Bridge/Kaha (manuel ou commande), vérification espace disque, contrôle 10, redémarrage des Interfaces Bridge/XPS si > 15 jours uptime.
   - Ajouter WorkflowKind enum nouvelles valeurs.

5. **P6 Step 5 — WatchFolder EDI léger Background**
   - `EdiWatchFolderLightBackgroundService` : StartAsync 10s warmup. Toutes 30s. Par SharedFolder déclaré avec path réel accessible :
     a. Lister fichiers récents (FileAge <= StalenessThreshold sinon oublier).
     b. Pour chaque fichier en attente > `StalenessThresholdSeconds` → ouvrir Alerte FichierEnAttente via AlertService existant.
     c. Si échec répété ≥ EdiTrackingService.FailureThreshold → ouvrir session Diagnostic FR-059J via DiagnosticSessionService.OpenForFileAsync existant.
   - Pas de parsing métier lourd, pas d'ingestion EDI payload, juste surveillance et ouverture auto d'alerte/session diagnostic.

6. **P6 Step 6 — DI, build, diagnostics**
   - DependencyInjection.cs : enregistrements services + hosted.
   - Build `dotnet build N4Sentinel.slnx --no-restore`. Vérifier 0 erreur Domain/Infra/Web/Migrations.Sqlite. Vérifier warning count = baseline 9 exactement.
   - GetDiagnostics vide.

## Dependencies and Considerations
- **TimeZone** : utiliser `TimeZoneInfo.FindSystemTimeZoneById("Romance Standard Time")` pour Europe/Paris (Windows) ; static helper dans services ; les DateTimeOffset sont stockés UTC mais TimeOfDay est Local pour rester lisible par planificateur humain.
- **Anti-conflit Workflow actif** : avant PrepareAsync, requête WorkflowExecutions Where IsActive && WorkflowId==schedule.WorkflowId && EnvironmentId.Any → si Oui, SKIP déclenchement + log warning PlanificationDeclenchementSuspendu (pas audit TentativeNonAutorisee, c'est normal).
- **Audit** : le Scoped ExecutionService écrira son propre audit. Planification CUD passe par MaintenanceScheduleService et écrit AuditAction PlanificationCreee/Modifiee/Suspendue.
- **Nullable FK TriggeredByScheduleId** : EF convention crée colonne nullable + FK, pas de migration Add-Migration tant que non forcée. OK runtime.
- **Workflows prédéfinis** : génération seulement si `!existants.Contains("CODE")` ; ne régénère pas les workflows déjà créés (version utilisateur peut avoir été relue).
- **Hosted services séparés** : pas de mélange Orchestration/Scheduler/WatchFolder — 3 hosted services indépendants = crash de l'un ne tue pas les autres.
- **No HttpClient / No Server commands** : déclencheur prépare la préparation seulement. StepExecutor existant gère les actions composants. P6 n'écrit AUCUNE commande sur les composants directement.

## Validation
- `dotnet build N4Sentinel.slnx --no-restore` → 0 erreur Domain/Infrastructure/Web/Migrations.Sqlite (11 errors tests ignorés).
- `GetDiagnostics` → 0 diagnostics actifs.
- Warning count = baseline avant P6 (9) → aucun nouveau warning P5/P6.
- Examen visuel des enum insertions en queue : AuditAction max=16 (pas de réordonnancement). LifecycleStatus non touché.
- Compile checks : records referenced properly, TriggeredById/TriggeredBy default=Manuel dans ctor, hosted services utilisent IServiceScopeFactory pour injecter services Scoped.

## Risks et mitigations
1. **Cadence trop rapide 30s WatchFolder** → mitigé par statique readonly `CadenceWatchFolder = TimeSpan.FromSeconds(30)`, et scan seulement composants où `StalenessThresholdSeconds != null`.
2. **TimeOfDay double déclenchement (10:00 lancé 2x dans même heure)** → guard `LastTriggeredAt.Date == today.Date` + temps 12h sliding (pas juste même calendar day, pour maintenance nuit).
3. **Planification suspendue mal reprise** → ScheduleStatus enum explicite Suspendu, aucune occurrence déclenchée tant que pas repassé Actif.
4. **Workflows MaintenanceHebdo/Kind nouvelles valeurs** → ajouter à `enum WorkflowKind` en QUEUE (P6 ne modifie pas existants).
5. **EF conventions sans migration** : colonnes nouveaux fields/entités sont créées à la première utilisation runtime seulement — acceptable pour Sqlite fichier local ; un Add-Migration utilisateur post-P6 est optionnel.
