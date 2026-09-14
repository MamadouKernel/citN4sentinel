# P2 — Actions Préventives Recommandées (APR) Implementation Plan

## Repository Research

**État de l'art actuel P1 terminé** :
- Service `MeriseBusinessPolicyService` centralise 6 RG MERISE métier pures.
- Pipeline barrage effectif : ReferentialService (sauvegarde composants), PreflightService (avant exécution → Bloquant), SequenceValidator (ordre séquences), ApproveExecutionUseCase / ControlExecutionUseCase (contrôles Opération/Admin N4).
- Build 0 erreur, GetDiagnostics 0 nouveau diagnostic.

**Infrastructure réutilisable P2 déjà présente** :
- **Domaine `PreventiveRecommendedAction` ENT_APR** : entité complète créée lors de P0 MERISE (voir [PreventiveRecommendedAction.cs](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/PreventiveRecommendedAction.cs)). Tous les champs nécessaires sont codés : `PriorityScore` (calculé), `EstimatedP1RiskPct`, `EstimatedDurationMinutes`, `RootCauseFamily` (enum WorkflowPreCheckCategory × 10 TOP causes P1 Navis), `APRStatus` (7 états transitionnels), `IsBasedOnTrend` (15min), `RawSignalSnapshot` (JSON preuves), `FirstWeakSignalAt`, `LaunchWorkflowId/ResultingExecutionId`, `DismissedBy/At/Reason`.
- **Enum `WorkflowPreCheckCategory`** ([Enums.cs](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/Enums.cs#L189-L201)) : correspondance 1:1 aux 10 causes P1 récurrentes Navis (HorlogesNtp, GestionDonneesEtIndex, ConnectiviteReseau, EtatAmqKahaDb, EtatFilesBridge, OrdreArretDemarrage, SnapshotVirtuel, CompatibiliteTiers, VerrouillageEnvironnement, Autre).
- **Enum `AprStatus`** ([Enums.cs](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/Enums.cs#L207-L217)) : `Détectée(0) → Proposée(1) → Programmée(2) → EnCoursDExecution(3) → Exécutée(4) → Résolue(6)` ; états finaux alternatifs : `Ignorée(5)` + `Expirée(7)`.
- **Source signaux faibles** :
  - SQL persisté : `DbSet<ComponentSignal> ComponentSignals` [ComponentSignal.cs](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/ComponentSignal.cs) → multi-lignes horodatées `CapturedAt`, `SignalType` (Chaîne libre : "Écart horloge", "Service Windows", "Queue Bridge"…), `Value` (string), `Threshold`.
  - Mémoire : `SupervisionStateCache` singleton `ConcurrentDictionary<Guid, ComponentHealthSnapshot>` [SupervisionStateCache.cs](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Infrastructure/Supervision/SupervisionStateCache.cs) → GetSnapshot(Guid componentId) pour fallback si pas encore persisté en SQL.
- **Pattern BackgroundServices établis** : SupervisionBackgroundService (10s), OrchestrationBackgroundService, RetentionPurgeBackgroundService (quotidien). Pattern = `AddHostedService<T>()` dans [DependencyInjection.cs](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Infrastructure/DependencyInjection.cs).
- **Pattern UseCases existants** : ApproveExecutionUseCase, ControlExecutionUseCase → mono-classe `sealed`, `ctor` injection Scoped, retour `string?` erreur, null = OK.

**Formule priorité MERISE RG-0904 confirmée P0** :
> PriorityScore = EstimatedP1RiskPct ÷ max(1, EstimatedDurationMinutes)
— Plus le risque est haut ET l'action courte, plus elle passe en TOP-10.

## Files and Modules

**Fichiers à créer (Infrastructure uniquement — Domain déjà présent)** :
- `src/N4Sentinel.Infrastructure/Apr/AprDetectionService.cs` — Scellé Scoped. Moteur détection 6 détecteurs + 3 sources composants (ComponentSignals 15min fenêtre + SupervisionCache fallback).
- `src/N4Sentinel.Infrastructure/Apr/AprQueryService.cs` — Scellé Scoped. TRI top-10, filtres (env, status, famille cause), upsert/doublon éviction.
- `src/N4Sentinel.Infrastructure/Apr/UseCases/AprScheduleUseCase.cs` — Transition Détectée/Proposée → Programmée (DateTime ScheduledAt, ScheduledBy, audit).
- `src/N4Sentinel.Infrastructure/Apr/UseCases/AprLaunchViaWorkflowUseCase.cs` — Transition Programmée/Proposée → EnCoursDExecution. Crée exécution via ExecutionService (ou PrepareExecutionUseCase), stocke ResultingExecutionId, ExecutedBy.
- `src/N4Sentinel.Infrastructure/Apr/UseCases/AprDismissUseCase.cs` — Transition Détectée/Proposée/Programmée → Ignorée. `DismissalReason` OBLIGATOIRE non vide, DismissedBy/At.
- `src/N4Sentinel.Infrastructure/Apr/AprBackgroundService.cs` — IHostedService. Périodicité : 5 minutes. (1) Génère nouvelles APR via détecteurs ; (2) Expire automatiquement les APR dépassant `EstimatedDeadlineForAction` ; (3) Met à jour celles attachées à une exécution terminée.

**Fichiers à modifier** :
- `src/N4Sentinel.Infrastructure/DependencyInjection.cs` — Section APR : `AddScoped<AprDetectionService>()` + 3 UseCases + AprQueryService ; `AddHostedService<AprBackgroundService>()`.
- `src/N4Sentinel.Infrastructure/Persistence/N4SentinelDbContext.cs` — Vérifier `DbSet<PreventiveRecommendedAction>` est bien présent ; sinon l'ajouter.
- Si DbSet manquant : migration `20260913_P2AprDbSet.cs`.

## Implementation Steps (Ordre de dépendances)

1. **Vérification DbSet + table SQL**
   - Lire `N4SentinelDbContext.cs` et confirmer DbSet<PreventiveRecommendedAction> PreventiveRecommendedActions.
   - `dotnet ef dbcontext list` puis check SQL Server `INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME='PreventiveRecommendedActions'`.
   - Si absent : créer une migration `Add-Migration P2_20260913_AprDbSet` + apply.

2. **`AprDetectionService` — 6 détecteurs MERISE** (scellé Scoped, retour `List<PreventiveRecommendedAction>` générée par appel)
   Constante de fenêtre glissante : `public static readonly TimeSpan TrendWindow = TimeSpan.FromMinutes(15);` + nombre mini relevés pour tendance confirmée : `MinimumSamplesForTrend = 3`.
   Chaque détecteur produit un APR candidat avec champs alimentés :
   - **Detecteur 1 · HorlogeNTP** : `ComponentSignals SignalType LIKE '%horloge%' OR LIKE '%ClockSkew%'` last 15min. Si `MaxAbs(Value) >= 0.6 s` (sous-seuil progressif) ET pente croissante `latest - oldest >= +0.1 s` → famille `HorlogesNtp`, risque `80%`, durée 2 min, DeadLine Now+2h.
   - **Detecteur 2 · QueueBridge** : `SignalType LIKE '%Queue%' OR Target LIKE '%KahaDB%'`. Si `Max(Value) >= 300 msg` (sous-seuil 500 avert / 1000 bloc) OU tendance +100 msg/10min → famille `EtatFilesBridge`, risque 70%, durée 5 min, DeadLine Now+8h.
   - **Detecteur 3 · IndexFragmentation** : Si `SignalType LIKE '%frag%' OR LIKE '%index%'` + Value >= 25 % → famille `GestionDonneesEtIndex`, risque 65 %, durée 30 min, DeadLine Now+3j.
   - **Detecteur 4 · EspaceDisqueServ** : `SignalType LIKE '%Disk%' OR LIKE '%disk%'`. Value (free %) <10 % → famille `GestionDonneesEtIndex`, risque 55 %, durée 10 min, DeadLine Now+24h.
   - **Detecteur 5 · ServicesInstables** : Composants où `SupervisionStateCache[].Health` est `Degrade/Down` depuis 3 cycles consécutifs (au moins 30 s). → famille `ConnectiviteReseau` si réseau sinon `CompatibiliteTiers`, risque 40 %, durée 15 min, DeadLine Now+48h.
   - **Detecteur 6 · SnapshotRecent** : si VM Host a été snapshoté <72h via `ComponentSignals Target LIKE '%snapshot%'` et `DATEDIFF_HOUR(CapturedAt, Now) < 72` → famille `SnapshotVirtuel`, risque 75 %, durée 3 min (consolidation), DeadLine Now+24h.
   - Post-traitement : pour chaque APR candidat → calcul `PriorityScore = EstimatedP1RiskPct / Math.Max(1, EstimatedDurationMinutes)` + `IsBasedOnTrend = (signal_count >= MinimumSamplesForTrend)` + `RawSignalSnapshot` (JsonSerializer.Serialize Dictionary<string,object>).

3. **`AprQueryService` — Top10, Upsert anti-doublon, expirations**
   - Méthode `UpsertOrUpdateAsync(List<PreventiveRecommendedAction> candidates)` : recherche Code+SourceComponentId+Status ∈ {Detectee, Proposee, Programmee}. Si match → mettre à jour RawSignalSnapshot, PriorityScore, LastSeenAt, ne pas créer nouveau (évite 1000 APR du même problème). Si nouveau → insert + `GeneratedAt = Now`.
   - Méthode `GetTop10DashboardAsync(Guid? envId, AprStatus[]? statusFilter)` : `OrderByDescending(p => p.PriorityScore).Take(10)`.
   - Méthode `ExpireOverdueAsync()` : passe statut Expiree quand Now > EstimatedDeadlineForAction ET status dans {Detectee, Proposee, Programmee}.

4. **3 Use Cases transitions d'état APR (pattern sealed Scoped, retour string?)**
   - **AprScheduleUseCase.Execute(aprId, scheduledAt, actor, ct)** : Vérif (status ∈ {Detectee, Proposee, Programmee}), scheduledAt > Now. Écrit ScheduledAt, ScheduledBy = actor, Status = Programmée. Audit trace.
   - **AprLaunchViaWorkflowUseCase.Execute(aprId, workflowId, actor, ct)** : Vérif status autorisé. Appelle `PrepareExecutionUseCase` OU `WorkflowService` pour créer nouvelle exécution WorkflowExecution. Stocke LaunchWorkflowId, ResultingExecutionId (nouvelle Guid exe créée), ExecutedBy=actor. Status → EnCoursDExecution. Audit.
   - **AprDismissUseCase.Execute(aprId, dismissalReason, actor, ct)** : `string.IsNullOrWhiteSpace(dismissalReason)` → retour immédiat "RG P2 : motif d'ignorance OBLIGATOIRE". Sinon DismissedAt=Now, DismissedBy=actor, DismissalReason=reason. Status → Ignoree. Audit.

5. **`AprBackgroundService : BackgroundService`** (Ajout via `AddHostedService`)
   - `Period = TimeSpan.FromMinutes(5)` (moins fréquent que Supervision 10s).
   - 3 tâches séquentielles par tick :
     (1) `AprDetectionService.DetectAllAsync(envId or all)` → candidates ; (2) `AprQueryService.UpsertOrUpdateAsync(candidates)` ; (3) `AprQueryService.ExpireOverdueAsync()` ; (4) *Bonus* : Récupérer les APR en EnCoursDExecution dont ResultingExecutionId ≠ null → check DB statut Execution → si `ExecutionStatus` terminal → APR Status → Executée puis Résolue.
   - Log info `[APR] Pass effectué : {n} détectées, {u} updatées, {e} expirées`.

6. **DependencyInjection.cs section APR**
   - Région `// APR — Actions Préventives Recommandées (P2 MERISE)` :
     `services.AddScoped<AprDetectionService>();`
     `services.AddScoped<AprQueryService>();`
     `services.AddScoped<AprScheduleUseCase>();`
     `services.AddScoped<AprLaunchViaWorkflowUseCase>();`
     `services.AddScoped<AprDismissUseCase>();`
     `services.AddHostedService<AprBackgroundService>();`

7. **Build + Diagnostics**
   - `dotnet build src/N4Sentinel.Web/N4Sentinel.Web.csproj -c Debug` → 0 erreur. 0 nouveau warning lié APR.
   - `GetDiagnostics` → 0 nouveau diagnostic hors code existant.

## Dependencies and Considerations

- **Pattern P1 respect IMPÉRATIVEMENT** : tous services sealed ; retour `string?` erreur (pas d'exceptions métier) ; injection Scoped ; JsonSerializer System.Text.Json pour RawSignalSnapshot (pas Newtonsoft). Aucun nouveau package NuGet.
- **IsBasedOnTrend = true SEULEMENT SI `captures ≥ MinimumSamplesForTrend(3)`.** Un unique snapshot de 0.7s NTP ne génère PAS d'APR → faux positif réduit.
- **DbSet Migrations** : l'entité PreventiveRecommendedAction a été ajoutée dans la migration P0 MeriseCompletion20260912. Si le DbSet n'est pas dans N4SentinelDbContext (oubli humain P0), on l'ajoute en STEP 1 + migration dédiée P2.
- **Les seuils progressifs P2 sont VOLONTAIREMENT SOUS les seuils BLOQUANTS P1** : ex horloge 0.6 s APR (alors que P1 bloquant = 1.0 s). C'est l'objectif APR : déclencher avant seuil critique. Tous les seuils sont `public const` dans AprDetectionService pour surcharge future config.
- **EstimatedDurationMinutes** : heuristique fixe par type famille (table en const). Pas d'IA — MTTR historisable P3.
- **Deadline calcul** : Now + `(100 - EstimatedP1RiskPct)/100` pondérée. Risque 90 % → 2 heures ; risque 60 % → 24 heures ; risque 20 % → 7 jours.

## Validation

- **Build** : `dotnet build src/N4Sentinel.Web/N4Sentinel.Web.csproj` → `0 erreur`. Les warnings CS9113 (param non lus ExecutionService), CS8601 (EnvironmentDuplicationService CS8601), EF1002 (BackupService SQL raw), CS0649 / BL0008 (Razor pages Diagnostics/PremierDemarrage) préexistants → TOLÉRÉS, 0 nouveau warning.
- **GetDiagnostics** : Tableau vide ou diagnostics hors P2 (comparaison avant/après build).
- **Test par scénario (bouchons)** :
  (1) Inserer 3 ComponentSignaux horloge croissants 0.5 → 0.6 → 0.7 s en 15min → `AprDetectionService` retourne 1 APR famille HorlogesNtp avec PriorityScore=80/2=40.
  (2) Appel `AprDismissUseCase.Execute` avec motif vide → retour erreur.
  (3) Appel `AprScheduleUseCase.Execute` avec date future → status Programmée OK.
- **Vérification DI** : résolution depuis Web via `[Inject] AprQueryService` ou service provider → pas de MissingMethodException.

## Risques

| Risque | Mitigation |
|--------|------------|
| Trop de faux positifs APR saturent Dashboard | `MinimumSamplesForTrend = 3` (≥3 captures sur 15 min), Upsert éviction doublon, seuils progressifs bien SOUS bloquants P1. |
| Lenteur SQL sur `ComponentSignals` (100K+ lignes) query 15min | Index vérifié (P1): `IX_ComponentSignals_CapturedAt_ComponentId` (existe dans migration `SignauxSupervisionPersistes`). Si absent → `CREATE NONCLUSTERED INDEX ... INCLUDE (SignalType, Target, Value)` lors Step 1. |
| APR expirées non nettoyées passent inaperçues | Step 3 `ExpireOverdueAsync` lancé systématiquement chaque 5 min dans AprBackgroundService ; Dashboard Top10 n'affiche pas Expirée par défaut. |
| Appel `WorkflowLaunchUseCase` échoue mais APR reste `EnCoursDExecution` | UseCase wrappé dans try/catch → si échec, rollback APR → Proposée + écriture champ `Notes = "Erreur lancement : {exception.Message}"`. |
| Délai Background startup au reboot | Pattern AddHostedService déjà utilisé par OrchestrationBackgroundService → fiable. Period = 5 min → ne spamme pas au démarrage. |
