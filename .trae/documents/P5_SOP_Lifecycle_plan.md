# P5 — Cycle de vie SOP & Validation FR-088/FR-089/FR-090 — Implementation Plan

## Repository Research

### Ce qui existe déjà (ne pas réécrire)

Domain POCO dans [Sop.cs](file:///c:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/Sop.cs) :
- `Sop` : Code/Version/LifecycleStatus.HasBeenExecuted/RequiresElevatedRole + 8 sections FR-088 (Objective/Scope/Prerequisites/Risks/Controls/ExpectedOutcome/RollbackPlan/EscalationPath) + AppliesToVersion + 3 FKs origine (DocumentId / ExecutionId / SourceDiagnosticSessionId) + Steps + Associations (Kind=Composant|Signature|SessionDiagnostic). `IsUsable = Valide||Actif && Steps.Any`
- `SopStep`, `SopAssociation`, `SopExecution` (SopVersion copié + Outcome FR-089D), `SopExecutionStep` (History champ archivage FR-089A retour-arrière non destructif)
- Enums : `SopExecutionStatus`, `SopOutcome` (ProblemeResolu/NonResolu/Indetermine), `SopExecutionStepState` (AVenir/EnCours/Confirmee/Ecart/Ignoree/Annulee), `SopAssociationKind`

Services Infrastructure :
- [SopService.cs](file:///c:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Infrastructure/Procedures/SopService.cs) : Create/Save (versionne si déjà exécuté, comme WorkflowService), ChangeStatus audit FR-091, Add/RemoveAssociation, SuggestForReuse FR-089D, GetUsageStats (Terminé/Abandonné/EnCours + % réussite attesté, null si 0 attesté). Brouillon autogènes : `GenerateFromExecutionAsync` FR-089B, `GenerateFromDiagnosticSessionAsync` FR-097, `SeedReconstitutionKahaDbAsync` FR-059E/F/G sensible elevated.
- [SopExecutionService.cs](file:///c:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Infrastructure/Procedures/SopExecutionService.cs) : StartAsync (gardes IsUsable, RequiresElevatedRole serveur + audit TentativeNonAutorisee), ConfirmStep (preuve obligatoire FR-089A, écart non masqué FR-089C), SkipStep (IsSkippable + motif obligatoire + audit Contournement FR-089A), GoBack un cran avec History, Abandon (étapes restantes Annulee), DeclareOutcome FR-089D modifiable.

Ce qui **manque** = objet de P5 :
1. **Statut `RevueExpertise` manquant** dans le cycle : aujourd'hui ChangeStatus passe d'un statut à l'autre sans garde ni acteur d'approbation. La transition Brouillon → Validé doit nécessiter un relecteur distinct de l'auteur + checklist validation.
2. **Score qualité SOP** (rubrique FR-090) : 0–100 avec 5 axes (sections FR-088 remplies ≥6, ≥3 étapes non vides, ≥1 association composant/signature, ≥1 exécution attestée, Risque/Rollback/Escalade non null si sensible). Affiche `NiveauQualite` : Insuffisant/<70/Bon/Excellence.
3. **Révocation FR-094** : statut Révoqué (plus `LifecycleStatus` = Révoqué avec motif, date, auteur). Un SOP Révoqué n'est plus suggéré, n'est plus `IsUsable`.
4. **Feedback post-exécution** : 0–5 stars + note libre *par exécution* → nourrit ScoreQualite (optionnel) et les SuggestForReuse tri (par score puis récence puis taux).
5. **Amélioration GetUsageStats** : ajouter temps médian par étape, % d'étapes avec Écarts, dernière utilisation, top dérogations fréquentes.
6. **Lien retour Incident → SOP** : P4 a ajouté `CapitalizedIncident.GeneratedSOPId` — manquait une méthode pour capitaliser depuis une exécution SOP réussie (en plus de la voie Diagnostic déjà couverte).

### Contraintes & archétypes respectés
- Ne pas modifier les tests.
- Ne pas casser de signatures d'API publiques (nouveaux champs POCO = convention EF, sans migration tant que non requis ; nouvelles méthodes sur services = non-breaking).
- Enums LifecycleStatus : ajouter `RevueExpertise = 4` et `Revoque = 5` seulement si pas existant — vérifier d'abord.
- Toujours audit `AuditAction.ChangementDeStatut` + `AuditAction.Approbation` éventuelle sur changement de statut majeur.

## Files and Modules
- `src/N4Sentinel.Domain/Enums.cs` : si besoin ajouter `LifecycleStatus.RevueExpertise` / `Revoque` / `NiveauQualiteSop` / `AuditAction.Approbation` / `AuditAction.Revocation`.
- `src/N4Sentinel.Domain/Sop.cs` : ajouter `QualifiedBy/QualifiedAt/QualificationNote` (transition Brouillon→Validé), `RevokedBy/RevokedAt/RevocationReason` (FR-094), `FeedbackScoreAveragePercent?` + `ScoreQualiteDetail` (calculé en lecture). Ajouter `SopExecution.FeedbackStars0To5?` + `FeedbackNote?` + `FeedbackGivenBy/At`.
- `src/N4Sentinel.Infrastructure/Procedures/SopService.cs` :
  - `SubmitForReviewAsync(sopId, actor)` : Brouillon → RevueExpertise (garde sections OK)
  - `ApproveQualificationAsync(sopId, reviewerActor, note)` : RevueExpertise → Validé (garde reviewer != auteur FR-094), met `QualifiedBy/At/Note`
  - `PublishAsync(sopId, actor)` : Validé → Actif
  - `RevokeAsync(sopId, actor, reason)` : Valide/Actif → Révoqué, `RevokedBy/At/Reason`
  - `CalculerScoreQualite(sop, executions, reviews)` → `SopQualityReport` 5 axes + score global + niveau
  - `GetUsageStatsAsync enrichi` → `SopDetailedUsageStats` (médiane durée étape, % écarts, derniers usages, top écarts)
- `src/N4Sentinel.Infrastructure/Procedures/SopExecutionService.cs` :
  - `SubmitFeedbackAsync(executionId, actor, stars0To5, note)` : alimente feedback exécution (garde exécution finie + acteur a démarré)
- `src/N4Sentinel.Infrastructure/Procedures/CapitalizationService.cs` NOUVEAU **?** Non, préférer ajouter dans SopService :
  - `PromoteExecutionToSopDraftThenCapitalizeAsync(executionId, incidentId?...)` : génère SOP Brouillon + rattache à incidentCapitalisé via `GeneratedSOPId` s'il existe (complète la voie de capitalisation exécution → incident, sans perturber la voie diagnostic).
- `src/N4Sentinel.Infrastructure/Observability/MetricsService.cs` : si pertinent, enregistrer `RecordSopPublished` / `RecordSopRevoked` / `SopQualityDistribution` (optionnel).

## Implementation Steps
1. **Compléter le domaine FR-094 / FR-090**
   - 1a. Vérifier la valeur actuelle de LifecycleStatus. Ajouter RevueExpertise et Revoque si manquants (ne JAMAIS réordonner les valeurs existantes).
   - 1b. Ajouter AuditAction.Approbation / AuditAction.Revocation si absents.
   - 1c. Ajouter champs Sop : QualifiedBy(string?), QualifiedAt(DateTimeOffset?), QualificationNote(string?), RevokedBy(string?), RevokedAt(DateTimeOffset?), RevocationReason(string?).
   - 1d. Ajouter champs SopExecution : FeedbackStars0To5 (byte? 0-5), FeedbackNote (string?), FeedbackGivenBy (string?), FeedbackGivenAt (DateTimeOffset?).
   - 1e. Ajouter record `SopQualityReport`, `SopAxisScore`, `NiveauQualiteSop` enum, `SopDetailedUsageStats`.

2. **SopService : cycle de vie approuvé FR-094**
   - `SubmitForReviewAsync(sopId, actor)` : garde Status=Brouillon, Steps>0, au moins 6/8 sections FR-088 non nulles → passe RevueExpertise + audit.
   - `ApproveQualificationAsync(sopId, reviewer, note)` : garde Status=RevueExpertise, reviewer != CreatedBy → passe Validé, renseigne QualifiedXxx + audit Approbation.
   - `PublishAsync(sopId, actor)` : garde Status=Validé → passe Actif + audit ChangementDeStatut.
   - `RevokeAsync(sopId, actor, raison)` : garde Status Valide/Actif → Révoqué, renseigne RevokedXxx. Met à jour `IsUsable` pour exclure Révoqué.
   - `CalculateQualityReportAsync(sopId)` : axes (ComplétudeFR088 / QualiteEtapes / CouvertureAssociations / HistoriqueExecutions / GestionRisques) → score 0-100, Niveau.

3. **SopService enrichissement GetUsageStats + Promote Capitalization**
   - `GetDetailedUsageStatsAsync(sopId)` : enrichi avec médiane durée étape, % Confirmée vs Écart vs Ignorée, top 3 écarts fréquents, dateDerniereUtilisation, feedback moyen étoiles.
   - `PromoteExecutionToCapitalizedIncidentAsync(executionId, actor, rootCause...)` → optionnel : crée rattache CapitalizedIncident.GeneratedSOPId = l'exécution.SopId, alimente Metrics RecordIncidentCapitalized.

4. **SopExecutionService.Feedback**
   - `SubmitFeedbackAsync(executionId, actor, stars, note)` : garde execution.IsFinished, garde 0 <= stars <= 5, acteur est startedBy OU admin → écrit FeedbackXxx + audit.

5. **Compléter SuggestForReuseAsync** : trier par ScoreQualite DESC, puis taux de réussite attesté DESC, puis dernière utilisation DESC, puis version DESC.

6. **Build final 0 erreur Domain+Infrastructure+Web 0 nouveau warning + GetDiagnostics vide**

## Dependencies and Considerations
- Pas de migration EF tant que non demandé explicitement : nouveaux champs POCO sont nullables/strings → convention EF, l'app ajout `Add-Migration P5_SopLifecycle` plus tard au besoin.
- LifecycleStatus : vérifier **valeurs existantes avant insertion**. Les comparaisons dans SopService.IsUsable, GetUsableAsync, StartAsync doivent rester OK — ajouter Révoqué aux listes "n'est pas utilisable".
- Le garde reviewer != CreatedBy : Sop a CreatedBy via AuditableEntity → OK.
- Métriques facultatives : ne pas casser snapshot Metrics ; ajouter champs get-only avec default() pour compatibilité ascendante.
- Pas de modifications au front (Web/razor) sauf si déjà prévu ; on reste Domain + Infrastructure.

## Validation
- `dotnet build N4Sentinel.slnx --no-restore` → Domain/Infra/Web/SqliteMig 0 erreur, 0 new warning (11 errors tests out of scope)
- `GetDiagnostics` vide
- Rejouer chemin nominal : CreateAsync(Brouillon) → SaveAsync(étapes+sections) → SubmitForReview → ApproveQualification → Publish → StartAsync + toutes ConfirmStep + DeclareOutcome(ProblemeResolu) + SubmitFeedback(5) → CalculateQualityReportAsync score >=90 → Revoke → StartAsync désormais refusé car IsUsable=false.

## Risques
- [Réordonner LifecycleStatus casserait migrations anciennes] : **solution** — insérer RevueExpertise/Revoque **APRÈS les valeurs existantes** avec de nouvelles valeurs entières supérieures (jamais réaffecter). Toujours lire valeurs existantes avant.
- [Feedback calcul sur SOP casse SuggestForReuse] : SuggestForReuse tri score doit être un ThenBy, pas un Where ; un SOP sans feedback reste suggérable par les autres critères.
- [Calcul ScoreQualite trop lent] : on le fait à la demande, pas au Save (lazy).
- [Cas où Reviewer == CreatedBy] : garde en serveur, pas seulement UI. Log Warning + audit TentativeNonAutorisee.
