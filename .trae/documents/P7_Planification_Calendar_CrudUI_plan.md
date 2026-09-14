# P7 — UI CRUD Planification & Calendrier (Gap P6) Implementation Plan

## Repository Research
État P0-P6 livré 100% : Domain/Infra/Web builds 0 erreur (hors tests), baseline 9 warnings, GetDiagnostics vide.

**Gap identifié (vérifié via 0 matches grep + 0 pages dédiées) :**
- P6 a livré `MaintenanceScheduleService` (CRUD + guards infra) + `MaintenanceCalendarService` + `SchedulerTriggerBackgroundService` (Hosted 60s) + entités + enums — **strictement Domain + Infrastructure**.
- **Aucune page Razor Admin** n'existe pour gérer : `ExecutionSchedule` (planifier un workflow) ni `MaintenanceHoliday` (jours fériés/exclus). Glob pattern `*Planif*.razor` / `*Schedule*.razor` = 0 fichiers. Pages Admin existantes : Sop*, Workflows*, Environnements*, Composant*, Serveur*, Corbeille, Audit, ApprovalMatrix… — la rubrique Planification est absente.
- **Autres options P7 éliminées car déjà livrées** (gap 0) :
  - Retention/Purge → `RetentionPolicyService` + `RetentionPurgeBackgroundService` + UI `Admin/Audit.razor` existent. Migration `20260816081537_PolitiqueRetention` appliquée.
  - SOP Management → `Admin/Sop.razor`, `Admin/SopDetail.razor`, `SopExecutions.razor` existent, cycle vie P5 (Brouillon/AValider/Valide/Actif/Revoque) 100% livré.
  - Import SOP → hors court terme (pas de gap « bloquant »).
→ Conclusion : P7 = **combler le gap UI de P6 : 3 pages Admin CRUD Planification** (liste, détail, calendrier) + 2 méthodes publiques manquantes sur le service.

**Pattern UI existant étudié** ([Workflows.razor](file:///c:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Web/Components/Pages/Admin/Workflows.razor), [WorkflowDetail.razor](file:///c:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Web/Components/Pages/Admin/WorkflowDetail.razor)) :
- Route `/admin/xxx`, Policy `N4Policies.PeutAdministrerReferentiel`, `@rendermode InteractiveServer`
- Bootstrap Icons `bi bi-*`, classes `glass-card rounded-2xl`, `btn-theme-primary`, typo xs/sm/slate-400/sky-400/emerald/rose
- Banner feedback `_message + _messageClass` (succès/erreur/avertissement)
- Guid.Empty comme « sélecteur vide » sur les listes déroulantes environnement.
- Injection `ReferentialService` pour charger la liste environnements + workflows.

**Méthodes manquantes sur MaintenanceScheduleService** (grep des signatures) :
- ❌ `ListAsync(Guid envId)` → page liste.
- ❌ `GetByIdAsync(Guid id, bool includeRelated=false)` → page détail.
Existent : Create / Update / Enable / Disable / Delete / GetNextOccurrences. → Step 1 complète.

**Contraintes P7 respectées :**
- 0 touch aux tests (11 CS7036 baseline HORS-SCOPE, confirmé depuis P0).
- Séparation forte : pages UI consomment les Scoped services P6 via DI déjà enregistrée ([DependencyInjection.cs:L262-L268](file:///c:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Infrastructure/DependencyInjection.cs#L262-L268)) — 0 nouvelle ligne DI à ajouter.
- 0 migration EF forcée (les entités P6 existent déjà dans le modèle EF via conventions runtime ; Add-Migration reste une tâche utilisateur optionnelle post-P6/P7 si prod la souhaite).
- 0 nouveau NuGet : MudBlazor non utilisé, pages construites avec le style CSS existant (tailwind + bi).

## Files and Modules
| Fichier | Changement |
|---|---|
| `src/N4Sentinel.Infrastructure/Scheduling/MaintenanceScheduleService.cs` | Step1 : ajouter 2 méthodes publiques `ListAsync(Guid envId, ct)` + `GetByIdAsync(Guid id, ct)` |
| `src/N4Sentinel.Web/Components/Pages/Admin/Planifications.razor` | **NOUVEAU** — Step2 : liste planifications par env (tableau Kind/Status/Heure/Prochain déclenchement), enable/disable toggle, boutons Nouveau / Éditer / Supprimer, affichage warning si conflit 2h détecté |
| `src/N4Sentinel.Web/Components/Pages/Admin/PlanificationDetail.razor` | **NOUVEAU** — Step3 : création + édition détail d'une planification. Sélect Env → Workflow filtré. Code/Name, Kind radio avec UI conditionnelle (Hebdo: checkboxes 7j bitset mask; Mensuel: liste 1..31 éditable; Cron: input expression), TimeOfDay `input type="time"`, RanAs, Reason/Ticket template, AutoApprove toggle, ValidFrom/Until, SkipIfActiveAlready toggle, preview N prochaines occurrences via `GetNextOccurrencesAsync` |
| `src/N4Sentinel.Web/Components/Pages/Admin/MaintenanceCalendar.razor` | **NOUVEAU** — Step4 : gestion jours fériés/exclus PAR ENV. Liste tableau trié Date + Reason, formulaire DateOnly + Reason Add, Remove par ligne, info-bulle lien vers la page Planifications (les jours fériés sont skippés par le Scheduler) |

## Implementation Steps
1. **Infra complétion service** — `MaintenanceScheduleService` ajouter `ListAsync(Guid envId)` (AsNoTracking, .Include(e=>e.Workflow) pour afficher le nom; tri Code) + `GetByIdAsync(Guid id)` (FindAsync → null si introuvable). Aucune garde à ajouter (simple lecture).
2. **Page Planifications.razor** — route `/admin/planifications`. Sélect env. Recharger appelle `Main.ListAsync(_envId)` + `Referential.ListEnvironmentsAsync()`. Table : Code, Name, Kind (libellé FR), TimeOfDay, Workflow (code+nom), Status (badge Actif=vert / Suspendu=amber / Expiré=slate), LastTriggeredAt, NextPlannedAt. Actions row : Activer/Suspendre toggle (si Actif → bouton Suspendre, si Suspendu → bouton Activer), Éditer → navigue `/admin/planifications/{id}`, Supprimer (confirm). Haut : bouton Nouveau → `/admin/planifications/new?env=guid`. Banner message.
3. **Page PlanificationDetail.razor** — route `/admin/planifications/{Id:guid?}` (Id = new Guid = création). Charge l'env via Query `?env=` si création. Sélect Env, puis Workflow filtrés par IsRunnable=true sur cet env. Validation côté UI avant submit : Code renseigné, WorkflowId≠empty, TimeOfDay 0..24h, Hebdo mask≠0, Mensuel MonthlyDays non vide, Cron CronExpression renseigné, ValidUntil>ValidFrom. Submit → CreateAsync/UpdateAsync avec `Actor` récupéré via `AuthenticationStateProvider.GetUserName()` (pattern existant WorkflowDetail). Preview occurrences : bouton « Calculer 10 prochaines exécutions » → affiche liste DateTimeOffset en local via `MaintenanceScheduleService.ToLocal()`. Garde erreur/warning du service renvoyé dans banner.
4. **Page MaintenanceCalendar.razor** — route `/admin/maintenance-calendrier`. Sélect env. Table Date (format yyyy-MM-dd), Reason. Formulaire `input type="date"` (bind DateOnly via conversion) + input Reason + bouton Ajouter → `Calendar.AddAsync`. Ligne bouton Supprimer → `Calendar.RemoveAsync`. Info bandeau en haut : « Les jours marqués exclus skippent automatiquement TOUTES les planifications de cet environnement ».
5. **Build + Diagnostics** — `dotnet build N4Sentinel.slnx --no-restore`. Vérifier : Domain/Infra/Web/Migrations.Sqlite → 0 erreur. Warning count = baseline (8 hors test + 1 test EF1002 = 9 total). `GetDiagnostics` → `[]`.

## Dependencies and Considerations
- **Injections scoped déjà faites P6** : `AddScoped<MaintenanceScheduleService>()` + `AddScoped<MaintenanceCalendarService>()` dans [DependencyInjection.cs:L262-L268](file:///c:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Infrastructure/DependencyInjection.cs#L262-L268). Les @inject Razor fonctionneront sans nouvelle DI line.
- **Policy `N4Policies.PeutAdministrerReferentiel`** : Vérifier dans _Imports.razor + pages Admin existantes (Workflows, Sop) c'est bien la policy standard pour la rubrique Admin. Appliquée sur les 3 nouvelles pages.
- **Acteur (actor string)** : Récupération via `AuthenticationStateProvider` comme dans `WorkflowDetail.razor` — pattern déjà établi, pas d'extension IdentityManager nécessaire.
- **TimeOfDay UI** : `<input type="time" />` produit `TimeSpan?` C# via Bind. Affecter à `schedule.TimeOfDay.Value` (nullable → non-nullable avec garde côté UI).
- **WeeklyDayOfWeekMask UI** : 7 checkboxes (Lun…Dim). Changer un toggle maj le bitset. Rappel mask : Mon=bit0=0x01, …, Sun=bit6=0x40 (helper `MaintenanceScheduleService.MaskContientJour` exposé via `@static` depuis le service — utiliser une méthode privée de composant qui reproduit la même logique pour éviter le static en Razor).
- **MonthlyDays UI** : `<input type="text" />` comma/semicolon-separated integers 1..31. Parser en `List<int>`. Filtrer doublons+tricheries <1 ou >31 avec un message avertissement.
- **CronExpression UI** : Zone texte libre + indication « 5 ou 6 champs standard » ; la validation formelle reste côté service (`Kind=Cron && string.IsNullOrWhiteSpace(Cron) → error`).
- **Dates ValidFrom/Until** : `<input type="datetime-local" />` + conversion DateTimeOffset UTC. Traitement de la locale côté client Razor InteractiveServer OK.
- **Route création sans Id** : `[Parameter] public Guid? Id { get; set; }`. Si `Id == null || Id == Guid.Empty` → mode création ; précharger _form avec Kind=JoursOuvres, TimeOfDay=new TimeSpan(22,0,0), AutoApprove=false, SkipIfActiveAlready=true (defaults raisonnables).
- **Pas d'AuditService dans la page Web** : L'audit est déjà écrit côté `MaintenanceScheduleService.*Async()` (Create/Update/Enable/Disable/Delete/Calendar Add/Remove). Les pages Web NE DUPLIQUENT PAS les logs d'audit.
- **Pitfall Create vs Update** : `CreateAsync(ExecutionSchedule schedule, actor)` prend l'instance complète ; `UpdateAsync(Guid id, Action<ExecutionSchedule> mutate, actor)` prend un mutate. → En création : instancier `new ExecutionSchedule { ... }` + `CreateAsync`. En édition : `GetByIdAsync` chargé → `UpdateAsync(id, s => { s.Field = form.Field; ... }, actor)`.

## Validation
1. `dotnet build N4Sentinel.slnx --no-restore 2>&1` :
   - Domaine : 0 erreur.
   - Infrastructure : 0 erreur.
   - Migrations.Sqlite : 0 erreur.
   - Web : 0 erreur (pages Razor compilent via `Microsoft.NET.Sdk.Razor`).
   - Warning total count = **9 exactement** (baseline : CS9113×3, CS8601×2, EF1002 infra, CS0649×1, BL0008×1 + EF1002 tests). Si > 9 : identifier le nouveau warning et le corriger (nullable CS8602/CS8604 fréquents sur composants Razor avec FirstOrDefault → ajouter guards).
2. `GetDiagnostics` → `[]` (tableau vide).
3. Check manuel des routes : `/admin/planifications` et `/admin/planifications/{guid}` et `/admin/maintenance-calendrier` existent dans l'arborescence `Pages/Admin/`.

## Risks
- **Risque Razor compilation warning non prévu (CS8602/CS8604)** : Fréquent sur FirstOrDefault sans ?. Si > 9 warnings, ajouter guards is not null / ?? nullish sur les accès aux propriétés après chargement. → handled Step5.
- **Risque Guid route `/admin/planifications/new` non match** : Pattern Razor `{Id:guid?}` ne match PAS littéral "new". → Solution : utiliser un paramètre query `?env=GUID&create=1` OU utiliser le pattern `/admin/planifications` avec un paramètre d'état `_isCreating = true`. Plan retenu : Guid? null → création, guid → édition. Clic « Nouveau » → Nav.NavigateTo("/admin/planifications" + QueryString Create=1 + envId), page interprète `Id == null && Request.Query.ContainsKey("create")` → mode création.
- **Risque ListAsync sans Include Workflow** → `sched.Workflow?.Code` null sur la liste. → Step1 explicitement inclut `.Include(s => s.Workflow).AsNoTracking()`.
- **Risque Delete schedule déjà déclenchée** → Service renvoie error string. → Page affiche l'erreur dans _message, pas de crash.
- **Risque MonthlyDays parser vide après trim** → UI refuse la soumission si `MonthlyDays.Count == 0 && Kind == MensuelJoursPrecis`.
- **Risque Scheduler non testé à l'échelle UI** → Hors scope P7 (P6 a déjà fourni l'Hosted + tests runtime unitaires implicites). P7 = uniquement UI qui persiste via le service.
