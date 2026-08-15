using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Procedures;

/// <summary>
/// Déroulement d'une exécution de SOP (FR-089, FR-089A-C).
///
/// AUCUNE COMMANDE N'EST JAMAIS ÉMISE ICI. Ce service n'a pas de dépendance
/// vers l'orchestrateur ou un connecteur : chaque étape est réalisée à la
/// main, hors de l'application, et ce service se contente d'enregistrer sa
/// confirmation, sa preuve, ses écarts, ou son contournement justifié.
/// </summary>
public sealed class SopExecutionService(
    IDbContextFactory<N4SentinelDbContext> dbFactory,
    IAuditWriter auditWriter,
    ILogger<SopExecutionService> logger)
{
    // -----------------------------------------------------------------------
    // Démarrage (FR-089)
    // -----------------------------------------------------------------------
    /// <summary>
    /// Instancie une exécution à partir d'un SOP. Les étapes sont RECOPIÉES,
    /// pas référencées : le rapport doit rester exact même si le SOP évolue
    /// ensuite — identique à <c>ExecutionService.PrepareAsync</c>.
    /// </summary>
    public async Task<StartSopResult> StartAsync(
        Guid sopId, string startedBy, string? reason, string? ticketReference,
        Guid? sourceAlertId = null, Guid? sourceDiagnosticSessionId = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var sop = await db.Sops
            .Include(s => s.Steps.OrderBy(st => st.Order))
            .Include(s => s.Environment)
            .FirstOrDefaultAsync(s => s.Id == sopId, ct);

        if (sop is null) return StartSopResult.Failed("SOP introuvable.");
        if (!sop.IsUsable) return StartSopResult.Failed("Ce SOP n'est pas validé, ou ne comporte aucune étape.");
        if (string.IsNullOrWhiteSpace(startedBy)) return StartSopResult.Failed("Acteur manquant.");

        var execution = new SopExecution
        {
            SopId = sop.Id,
            SopVersion = sop.Version,
            SopCode = sop.Code,
            SopTitle = sop.Title,
            EnvironmentId = sop.EnvironmentId,
            EnvironmentCode = sop.Environment?.Code ?? "INCONNU",
            Status = SopExecutionStatus.EnCours,
            StartedBy = startedBy,
            Reason = reason,
            TicketReference = ticketReference,
            SourceAlertId = sourceAlertId,
            SourceDiagnosticSessionId = sourceDiagnosticSessionId,
            StartedAt = DateTimeOffset.UtcNow
        };

        var premiere = true;
        foreach (var modele in sop.Steps)
        {
            execution.Steps.Add(new SopExecutionStep
            {
                Order = modele.Order,
                Title = modele.Title,
                Instruction = modele.Instruction,
                ExpectedResult = modele.ExpectedResult,
                ComponentId = modele.ComponentId,
                IsSkippable = modele.IsSkippable,
                State = premiere ? SopExecutionStepState.EnCours : SopExecutionStepState.AVenir,
                StartedAt = premiere ? DateTimeOffset.UtcNow : null
            });
            premiere = false;
        }

        sop.HasBeenExecuted = true;

        db.SopExecutions.Add(execution);
        await db.SaveChangesAsync(ct);

        await RenseignerComposantsAsync(db, execution, ct);

        logger.LogInformation(
            "Exécution de SOP {Correlation} démarrée : {Code} v{Version} sur {Env}, par {Acteur}.",
            execution.CorrelationId, execution.SopCode, execution.SopVersion, execution.EnvironmentCode, startedBy);

        await auditWriter.WriteAsync(
            AuditAction.ExecutionOperation, AuditOutcome.Succes, startedBy,
            entityType: nameof(SopExecution), entityId: execution.Id.ToString(), entityLabel: execution.SopTitle,
            environmentId: execution.EnvironmentId, reason: reason, correlationId: execution.CorrelationId, ct: ct);

        return StartSopResult.Ok(execution.Id);
    }

    private static async Task RenseignerComposantsAsync(N4SentinelDbContext db, SopExecution execution, CancellationToken ct)
    {
        var ids = execution.Steps.Where(s => s.ComponentId is not null).Select(s => s.ComponentId!.Value).Distinct().ToList();
        if (ids.Count == 0) return;

        var noms = await db.Components.AsNoTracking()
            .Where(c => ids.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.LogicalName, ct);

        foreach (var etape in execution.Steps)
            if (etape.ComponentId is { } id && noms.TryGetValue(id, out var nom))
                etape.ComponentName = nom;

        await db.SaveChangesAsync(ct);
    }

    // -----------------------------------------------------------------------
    // Confirmation, écart, contournement (FR-089A, FR-089C)
    // -----------------------------------------------------------------------
    /// <summary>
    /// Compte rendu d'une étape (FR-089A). La preuve est obligatoire : une
    /// confirmation sans preuve n'est pas une preuve, c'est une case cochée.
    /// Un écart (résultat constaté différent de l'attendu) ne bloque pas la
    /// suite — il reste visible dans le rapport, pas masqué (FR-089C).
    /// </summary>
    public async Task<string?> ConfirmStepAsync(
        Guid stepId, string actor, string evidence, string? deviationNote, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(evidence))
            return "Une confirmation sans preuve n'est pas une preuve, c'est une case cochée. "
                 + "Décrivez ce qui a été constaté.";

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var etape = await db.SopExecutionSteps.Include(s => s.SopExecution)
            .FirstOrDefaultAsync(s => s.Id == stepId, ct);
        if (etape is null) return "Étape introuvable.";

        if (etape.State != SopExecutionStepState.EnCours)
            return $"L'étape « {etape.Title} » est en état {etape.State} : elle n'attend pas de confirmation.";

        etape.ConfirmedBy = actor;
        etape.ConfirmedAt = DateTimeOffset.UtcNow;
        etape.Evidence = evidence.Trim();
        etape.DeviationNote = string.IsNullOrWhiteSpace(deviationNote) ? null : deviationNote.Trim();
        etape.State = etape.DeviationNote is null ? SopExecutionStepState.Confirmee : SopExecutionStepState.Ecart;
        etape.EndedAt = DateTimeOffset.UtcNow;

        await AvancerAsync(db, etape.SopExecution!, ct);
        await db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>
    /// Contournement d'une étape (FR-089A). Une étape non déclarée
    /// contournable ne peut jamais être ignorée.
    /// </summary>
    public async Task<string?> SkipStepAsync(Guid stepId, string actor, string reason, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var etape = await db.SopExecutionSteps.Include(s => s.SopExecution)
            .FirstOrDefaultAsync(s => s.Id == stepId, ct);
        if (etape is null) return "Étape introuvable.";

        if (!etape.IsSkippable)
            return $"L'étape « {etape.Title} » n'est pas déclarée contournable.";

        if (string.IsNullOrWhiteSpace(reason))
            return "Un contournement sans justification n'est pas un contournement, c'est un trou dans la traçabilité.";

        if (etape.State != SopExecutionStepState.EnCours)
            return $"L'étape « {etape.Title} » est en état {etape.State} : elle n'est pas en cours.";

        etape.State = SopExecutionStepState.Ignoree;
        etape.SkippedBy = actor;
        etape.SkipReason = reason.Trim();
        etape.EndedAt = DateTimeOffset.UtcNow;

        await AvancerAsync(db, etape.SopExecution!, ct);
        await db.SaveChangesAsync(ct);

        logger.LogWarning("Étape de SOP « {Etape} » contournée par {Acteur} : {Motif}", etape.Title, actor, reason);

        await auditWriter.WriteAsync(
            AuditAction.Contournement, AuditOutcome.Succes, actor,
            entityType: nameof(SopExecutionStep), entityId: etape.Id.ToString(), entityLabel: etape.Title,
            environmentId: etape.SopExecution?.EnvironmentId, reason: reason,
            correlationId: etape.SopExecution?.CorrelationId, ct: ct);

        return null;
    }

    /// <summary>
    /// Fait passer l'exécution à l'étape suivante, ou la conclut s'il n'y en a
    /// plus. Appelé après chaque confirmation ou contournement.
    /// </summary>
    private static async Task AvancerAsync(N4SentinelDbContext db, SopExecution execution, CancellationToken ct)
    {
        var toutes = await db.SopExecutionSteps
            .Where(s => s.SopExecutionId == execution.Id)
            .OrderBy(s => s.Order)
            .ToListAsync(ct);

        var suivante = toutes.FirstOrDefault(s => s.State == SopExecutionStepState.AVenir);

        if (suivante is not null)
        {
            suivante.State = SopExecutionStepState.EnCours;
            suivante.StartedAt = DateTimeOffset.UtcNow;
            return;
        }

        execution.Status = SopExecutionStatus.Termine;
        execution.EndedAt = DateTimeOffset.UtcNow;
    }

    // -----------------------------------------------------------------------
    // Retour en arrière (FR-089A)
    // -----------------------------------------------------------------------
    /// <summary>
    /// Rouvre la DERNIÈRE étape terminée pour la refaire. Un pas en arrière à
    /// la fois, jamais un saut arbitraire : c'est ce qui garde la trace
    /// lisible. RIEN N'EST EFFACÉ — la confirmation, la preuve et l'écart
    /// précédents sont archivés dans <see cref="SopExecutionStep.History"/>
    /// avant d'être réinitialisés.
    /// </summary>
    public async Task<string?> GoBackAsync(Guid executionId, string actor, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return "Un retour en arrière sans motif n'est pas traçable. Expliquez pourquoi cette étape doit être reprise.";

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var execution = await db.SopExecutions.Include(x => x.Steps.OrderBy(s => s.Order))
            .FirstOrDefaultAsync(x => x.Id == executionId, ct);
        if (execution is null) return "Exécution introuvable.";

        if (execution.Status == SopExecutionStatus.Abandonne)
            return "Cette exécution a été abandonnée : elle ne peut plus être reprise.";

        var derniere = execution.Steps.Where(s => s.IsTerminal).OrderByDescending(s => s.Order).FirstOrDefault();
        if (derniere is null) return "Aucune étape confirmée à reprendre : c'est déjà la première.";

        // L'etape EnCours eventuelle (celle qui suivait) redevient A venir :
        // le pointeur recule d'un cran, pas seulement l'etape rouverte.
        var enCours = execution.Steps.FirstOrDefault(s => s.State == SopExecutionStepState.EnCours);
        if (enCours is not null)
        {
            enCours.State = SopExecutionStepState.AVenir;
            enCours.StartedAt = null;
        }

        derniere.History = ArchiverEtatSortant(derniere, actor, reason);

        derniere.State = SopExecutionStepState.EnCours;
        derniere.StartedAt = DateTimeOffset.UtcNow;
        derniere.EndedAt = null;
        derniere.ConfirmedBy = null;
        derniere.ConfirmedAt = null;
        derniere.Evidence = null;
        derniere.DeviationNote = null;
        derniere.SkippedBy = null;
        derniere.SkipReason = null;

        if (execution.Status == SopExecutionStatus.Termine)
        {
            execution.Status = SopExecutionStatus.EnCours;
            execution.EndedAt = null;
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Retour en arrière sur l'étape « {Etape} » de l'exécution SOP {Correlation}, par {Acteur} : {Motif}",
            derniere.Title, execution.CorrelationId, actor, reason);

        return null;
    }

    private static string ArchiverEtatSortant(SopExecutionStep etape, string actor, string motifRetour)
    {
        var resume = etape.State switch
        {
            SopExecutionStepState.Confirmee =>
                $"Confirmée par {etape.ConfirmedBy} le {etape.ConfirmedAt:dd/MM/yyyy HH:mm}. Preuve : {etape.Evidence}",
            SopExecutionStepState.Ecart =>
                $"Confirmée avec écart par {etape.ConfirmedBy} le {etape.ConfirmedAt:dd/MM/yyyy HH:mm}. "
                + $"Preuve : {etape.Evidence} — Écart : {etape.DeviationNote}",
            SopExecutionStepState.Ignoree =>
                $"Contournée par {etape.SkippedBy}. Motif : {etape.SkipReason}",
            _ => $"État {etape.State}."
        };

        var entree = $"[Repris par {actor} le {DateTimeOffset.UtcNow:dd/MM/yyyy HH:mm}, motif : {motifRetour}] {resume}";
        var historique = string.IsNullOrWhiteSpace(etape.History) ? entree : $"{etape.History}\n{entree}";

        return historique.Length <= 4000 ? historique : historique[^4000..];
    }

    // -----------------------------------------------------------------------
    // Abandon
    // -----------------------------------------------------------------------
    public async Task<string?> AbandonAsync(Guid executionId, string actor, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return "Un abandon sans motif n'est pas traçable.";

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var execution = await db.SopExecutions.Include(x => x.Steps)
            .FirstOrDefaultAsync(x => x.Id == executionId, ct);
        if (execution is null) return "Exécution introuvable.";
        if (execution.IsFinished) return $"Cette exécution est déjà terminée ({execution.Status}).";

        foreach (var etape in execution.Steps.Where(s => !s.IsTerminal))
        {
            etape.State = SopExecutionStepState.Annulee;
            etape.EndedAt = DateTimeOffset.UtcNow;
        }

        execution.Status = SopExecutionStatus.Abandonne;
        execution.AbandonReason = reason.Trim();
        execution.EndedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);

        logger.LogWarning(
            "Exécution de SOP {Correlation} abandonnée par {Acteur} : {Motif}",
            execution.CorrelationId, actor, reason);

        await auditWriter.WriteAsync(
            AuditAction.ExecutionOperation, AuditOutcome.Echec, actor,
            entityType: nameof(SopExecution), entityId: execution.Id.ToString(), entityLabel: execution.SopTitle,
            environmentId: execution.EnvironmentId, reason: reason, correlationId: execution.CorrelationId, ct: ct);

        return null;
    }

    // -----------------------------------------------------------------------
    // Lecture
    // -----------------------------------------------------------------------
    public async Task<SopExecution?> GetAsync(Guid executionId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.SopExecutions
            .AsNoTracking()
            .Include(x => x.Steps.OrderBy(s => s.Order))
            .Include(x => x.Sop)
            .Include(x => x.Environment)
            .FirstOrDefaultAsync(x => x.Id == executionId, ct);
    }

    public async Task<List<SopExecution>> GetRecentAsync(Guid? environmentId, int count = 50, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var requete = db.SopExecutions.AsNoTracking().Include(x => x.Steps).AsQueryable();
        if (environmentId is { } id) requete = requete.Where(x => x.EnvironmentId == id);

        return await requete
            .OrderByDescending(x => x.StartedAt)
            .Take(count)
            .ToListAsync(ct);
    }
}

public sealed record StartSopResult
{
    public bool Succeeded { get; init; }
    public Guid ExecutionId { get; init; }
    public string? Error { get; init; }

    public static StartSopResult Ok(Guid id) => new() { Succeeded = true, ExecutionId = id };
    public static StartSopResult Failed(string error) => new() { Succeeded = false, Error = error };
}
