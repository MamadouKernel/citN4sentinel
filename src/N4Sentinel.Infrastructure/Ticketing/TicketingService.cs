using Microsoft.EntityFrameworkCore;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Ticketing;

/// <summary>
/// Service de gestion des tickets d'incident de N4 Sentinel (S3-09).
///
/// Deux grandes familles d'usage :
///   (A) Tickets manuels : ouverts / modifiés / fermés depuis l'écran Admin
///       par un ExploitN4 ou AdminN4.
///   (B) Tickets automatiques : créés par le moteur de Diagnostic P1, par le
///       système APR (Actions Préventives Recommandées), par Supervision ou
///       par le moniteur EDI WatchFolder.
///
/// L'intégration vers l'outil externe (ServiceNow / Jira / GLPI) n'est pas
/// implémentée en V1 : on renseigne simplement le champ ExternalTicketId et
/// on propose un export CSV/JSON des tickets pour l'import manuel dans la
/// plateforme DSI.
/// </summary>
public sealed class TicketingService(
    IDbContextFactory<N4SentinelDbContext> dbFactory,
    IAuditWriter auditWriter)
{
    // -----------------------------------------------------------------------
    // Lecture
    // -----------------------------------------------------------------------
    public async Task<List<Ticket>> ListAsync(
        TicketStatus? status = null,
        TicketPriority? priorite = null,
        Guid? environmentId = null,
        int? limit = 500,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var q = db.Tickets.AsNoTracking().AsQueryable();
        if (status.HasValue) q = q.Where(t => t.Status == status.Value);
        if (priorite.HasValue) q = q.Where(t => t.Priority == priorite.Value);
        if (environmentId.HasValue) q = q.Where(t => t.EnvironmentId == environmentId.Value);

        return await q
            .Include(t => t.Comments)
            .OrderByDescending(t => t.OpenedAt)
            .Take(limit ?? 500)
            .ToListAsync(ct);
    }

    public async Task<Ticket?> GetByIdAsync(Guid ticketId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Tickets
            .AsNoTracking()
            .Include(t => t.Comments.OrderBy(c => c.WrittenAt))
            .FirstOrDefaultAsync(t => t.Id == ticketId, ct);
    }

    public async Task<int> CountOuvertsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Tickets.CountAsync(
            t => t.Status != TicketStatus.Ferme && t.Status != TicketStatus.Annule, ct);
    }

    // -----------------------------------------------------------------------
    // Ouverture manuelle
    // -----------------------------------------------------------------------
    public async Task<(Ticket Ticket, string? Erreur)> OuvrirManuelAsync(
        string actor,
        string title,
        string description,
        TicketPriority priority,
        TicketSeverity severity,
        Guid? environmentId = null,
        string? assignedTo = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title)) return (null!, "Le titre du ticket est obligatoire.");
        if (string.IsNullOrWhiteSpace(actor)) return (null!, "Auteur manquant.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var code = await GenererCodeUnique(db, ct);
        var ticket = new Ticket
        {
            Code = code,
            Title = title.Trim(),
            Description = (description ?? string.Empty).Trim(),
            Status = TicketStatus.Nouveau,
            Priority = priority,
            Severity = severity,
            Source = TicketSource.Manuel,
            EnvironmentId = environmentId,
            AssignedTo = string.IsNullOrWhiteSpace(assignedTo) ? null : assignedTo,
            OpenedAt = DateTimeOffset.UtcNow
        };

        db.Tickets.Add(ticket);
        await db.SaveChangesAsync(ct);

        await auditWriter.WriteAsync(
            AuditAction.TicketCree, AuditOutcome.Succes, actor,
            entityType: nameof(Ticket), entityId: ticket.Id.ToString(),
            entityLabel: $"{ticket.Code} — {ticket.Title}",
            environmentId: ticket.EnvironmentId,
            reason: $"Ouverture manuelle. Priorité {priority}, sévérité {severity}.",
            ct: ct);

        return (ticket, null);
    }

    // -----------------------------------------------------------------------
    // Ouverture automatique / intégration depuis Diagnostic / APR / EDI
    // -----------------------------------------------------------------------
    /// <summary>
    /// Ouverture automatique à partir d'une session de diagnostic close ayant
    /// abouti à un verdict de cause caractérisée (TOP10 causes P1 Navis).
    /// Garde anti-doublon : si un ticket existe déjà avec le même
    /// DiagnosticSignatureId + les 12 dernières heures, on réutilise celui-ci.
    /// </summary>
    public async Task<Ticket?> OuvrirDepuisDiagnosticSiNecessaireAsync(
        DiagnosticSession session,
        Guid? signatureId,
        CancellationToken ct = default)
    {
        if (session is null) return null;

        // On ne crée de ticket auto que si la session a vraiment identifié une
        // cause connue (TOP10 P1). Pistes / anomalies isolées / rien de
        // concluant = escalade humaine sans ticket auto.
        if (session.Verdict != DiagnosticVerdict.CauseCaracterisee)
            return null;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var seuil = DateTimeOffset.UtcNow.AddHours(-12);
        var existant = await db.Tickets
            .FirstOrDefaultAsync(t =>
                t.DiagnosticSignatureId == signatureId
                && t.EnvironmentId == session.EnvironmentId
                && t.Status != TicketStatus.Ferme
                && t.Status != TicketStatus.Annule
                && t.OpenedAt >= seuil, ct);
        if (existant is not null) return existant;

        var code = await GenererCodeUnique(db, ct);
        var ticket = new Ticket
        {
            Code = code,
            Title = $"[P1/AUTO] Diagnostic {session.Title} — {session.EnvironmentCode}",
            Description =
                "Ouverture automatique par le moteur de diagnostic.\n"
                + $"Session : {session.Title} (id={session.Id})\n"
                + $"Verdict : {session.Verdict}\n"
                + $"Explication : {session.VerdictExplanation ?? "(aucune)"}\n"
                + $"Créé par : {session.RequestedBy}",
            Status = TicketStatus.EnCours,
            Priority = TicketPriority.P1Critique,
            Severity = TicketSeverity.Critique,
            Source = TicketSource.DiagnosticP1,
            EnvironmentId = session.EnvironmentId,
            DiagnosticSignatureId = signatureId,
            OpenedAt = DateTimeOffset.UtcNow
        };

        db.Tickets.Add(ticket);
        await db.SaveChangesAsync(ct);

        await auditWriter.WriteAsync(
            AuditAction.TicketCree, AuditOutcome.Succes,
            actor: "systeme-diagnostic-p1",
            entityType: nameof(Ticket), entityId: ticket.Id.ToString(),
            entityLabel: $"{ticket.Code} — {ticket.Title}",
            environmentId: ticket.EnvironmentId,
            reason: "Ouverture automatique après diagnostic P1 Navis (cause caractérisée TOP10).",
            ct: ct);

        return ticket;
    }

    /// <summary>Ouverture automatique déclenchée par une APR à haut risque.</summary>
    public async Task<Ticket?> OuvrirDepuisAprAsync(
        PreventiveRecommendedAction apr,
        N4Environment env,
        CancellationToken ct = default)
    {
        if (apr is null || env is null) return null;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var seuil = DateTimeOffset.UtcNow.AddHours(-8);
        var existant = await db.Tickets
            .FirstOrDefaultAsync(t =>
                t.AprId == apr.Id
                && t.Status != TicketStatus.Ferme && t.Status != TicketStatus.Annule
                && t.OpenedAt >= seuil, ct);
        if (existant is not null) return existant;

        // Seuil raisonnable : EstimatedP1RiskPct >= 60 → au moins P2.
        // Règle métier : >85 → P2 Majeur / 60-85 → P3 Normal / <60 → pas de ticket auto
        // (laisser l'APR être triée manuellement dans le tableau de bord).
        if (apr.EstimatedP1RiskPct < 60m) return null;

        var code = await GenererCodeUnique(db, ct);
        var (prio, sev) = apr.EstimatedP1RiskPct switch
        {
            >= 85 => (TicketPriority.P2Majeur, TicketSeverity.Majeur),
            _ => (TicketPriority.P3Normal, TicketSeverity.Mineur)
        };

        var ticket = new Ticket
        {
            Code = code,
            Title = $"[APR/AUTO] {apr.Title} sur {env.Code}",
            Description =
                "Ouverture automatique APR — action préventive recommandée déclenchée avant le seuil bloquant.\n"
                + $"Code détecteur : {apr.Code}\n"
                + $"Environnement : {env.Code} — {env.Name}\n"
                + $"Famille de cause : {apr.RootCauseFamily}\n"
                + $"Risque P1 estimé : {apr.EstimatedP1RiskPct:F1}%\n"
                + $"Durée estimée : {apr.EstimatedDurationMinutes} min\n"
                + $"Détail : {apr.Description}",
            Status = TicketStatus.Nouveau,
            Priority = prio,
            Severity = sev,
            Source = TicketSource.ActionPreventive,
            EnvironmentId = env.Id,
            AprId = apr.Id,
            OpenedAt = DateTimeOffset.UtcNow
        };

        db.Tickets.Add(ticket);
        await db.SaveChangesAsync(ct);
        await auditWriter.WriteAsync(
            AuditAction.TicketCree, AuditOutcome.Succes,
            actor: "systeme-apr",
            entityType: nameof(Ticket), entityId: ticket.Id.ToString(),
            entityLabel: $"{ticket.Code} — {ticket.Title}",
            environmentId: env.Id,
            reason: $"Ouverture automatique par une Action Préventive Recommandée (APR risque {apr.EstimatedP1RiskPct:F1}%).",
            ct: ct);
        return ticket;
    }

    /// <summary>Ouverture automatique par EdiWatchFolderLight après 3 échecs consécutifs.</summary>
    public async Task<Ticket?> OuvrirDepuisEdiAsync(
        N4Environment env,
        string motif,
        string correlation,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var seuil = DateTimeOffset.UtcNow.AddHours(-4);
        var existant = await db.Tickets
            .FirstOrDefaultAsync(t =>
                t.EnvironmentId == env.Id
                && t.OperationCorrelationId == correlation
                && t.Status != TicketStatus.Ferme && t.Status != TicketStatus.Annule
                && t.OpenedAt >= seuil, ct);
        if (existant is not null) return existant;

        var code = await GenererCodeUnique(db, ct);
        var ticket = new Ticket
        {
            Code = code,
            Title = $"[EDI/AUTO] Échecs récurrents sur {env.Code} — {correlation}",
            Description =
                "Ouverture automatique par le moniteur EDI WatchFolder après au moins 3 échecs consécutifs sur le dossier surveillé.\n"
                + motif,
            Status = TicketStatus.EnCours,
            Priority = TicketPriority.P3Normal,
            Severity = TicketSeverity.Mineur,
            Source = TicketSource.EdiEchecRecurrent,
            EnvironmentId = env.Id,
            OperationCorrelationId = correlation,
            OpenedAt = DateTimeOffset.UtcNow
        };
        db.Tickets.Add(ticket);
        await db.SaveChangesAsync(ct);
        await auditWriter.WriteAsync(
            AuditAction.TicketCree, AuditOutcome.Succes,
            actor: "systeme-edimonitor",
            entityType: nameof(Ticket), entityId: ticket.Id.ToString(),
            entityLabel: $"{ticket.Code} — {ticket.Title}",
            environmentId: env.Id,
            reason: "Ouverture automatique EDI échecs récurrents.",
            ct: ct);
        return ticket;
    }

    // -----------------------------------------------------------------------
    // Évolution d'un ticket
    // -----------------------------------------------------------------------
    public async Task<string?> MettreAJourStatutAsync(
        Guid ticketId,
        TicketStatus nouveauStatut,
        string actor,
        string? raison = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actor)) return "Acteur manquant.";
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var t = await db.Tickets.FirstOrDefaultAsync(x => x.Id == ticketId, ct);
        if (t is null) return "Ticket introuvable.";

        var ancien = t.Status;
        t.Status = nouveauStatut;
        if (nouveauStatut is TicketStatus.Ferme or TicketStatus.Resolu or TicketStatus.Annule)
        {
            t.ClosedAt = DateTimeOffset.UtcNow;
            t.ClosureReason = raison;
        }
        else if (ancien is TicketStatus.Ferme or TicketStatus.Resolu or TicketStatus.Annule)
        {
            t.ClosedAt = null;
            t.ClosureReason = null;
        }

        await db.SaveChangesAsync(ct);
        await auditWriter.WriteAsync(
            AuditAction.TicketModifie, AuditOutcome.Succes, actor,
            entityType: nameof(Ticket), entityId: ticketId.ToString(),
            entityLabel: $"{t.Code} — {t.Title}",
            environmentId: t.EnvironmentId,
            reason: $"Changement de statut : {ancien} → {nouveauStatut}. {raison}",
            ct: ct);
        return null;
    }

    public async Task<string?> CommenterAsync(
        Guid ticketId,
        string auteur,
        string texte,
        bool interne = true,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(texte)) return "Commentaire vide.";
        if (string.IsNullOrWhiteSpace(auteur)) return "Auteur manquant.";

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var t = await db.Tickets.FirstOrDefaultAsync(x => x.Id == ticketId, ct);
        if (t is null) return "Ticket introuvable.";

        db.TicketComments.Add(new TicketComment
        {
            Id = Guid.NewGuid(),
            TicketId = ticketId,
            Author = auteur,
            Text = texte.Trim(),
            IsInternal = interne,
            WrittenAt = DateTimeOffset.UtcNow
        });

        await db.SaveChangesAsync(ct);
        await auditWriter.WriteAsync(
            AuditAction.TicketModifie, AuditOutcome.Succes, auteur,
            entityType: nameof(TicketComment), entityId: ticketId.ToString(),
            entityLabel: $"Commentaire sur {t.Code}",
            environmentId: t.EnvironmentId,
            reason: interne ? "Commentaire interne ajouté." : "Commentaire public ajouté.",
            ct: ct);
        return null;
    }

    public async Task<string?> DefinirIdExterneAsync(
        Guid ticketId,
        string? externalId,
        string actor,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var t = await db.Tickets.FirstOrDefaultAsync(x => x.Id == ticketId, ct);
        if (t is null) return "Ticket introuvable.";

        t.ExternalTicketId = string.IsNullOrWhiteSpace(externalId) ? null : externalId.Trim();
        await db.SaveChangesAsync(ct);
        await auditWriter.WriteAsync(
            AuditAction.TicketModifie, AuditOutcome.Succes, actor,
            entityType: nameof(Ticket), entityId: ticketId.ToString(),
            entityLabel: $"{t.Code} — ExternalId={t.ExternalTicketId ?? "(supprimé)"}",
            ct: ct);
        return null;
    }

    // -----------------------------------------------------------------------
    // Code métier unique
    // -----------------------------------------------------------------------
    private static async Task<string> GenererCodeUnique(N4SentinelDbContext db, CancellationToken ct)
    {
        var annee = DateTime.UtcNow.Year;
        var baseName = $"TIC-{annee}-";
        var dernier = await db.Tickets
            .Where(x => x.Code != null && x.Code.StartsWith(baseName))
            .Select(x => x.Code!)
            .OrderByDescending(x => x, StringComparer.Ordinal)
            .FirstOrDefaultAsync(ct);

        var numero = 1;
        if (dernier is not null && int.TryParse(dernier.AsSpan(baseName.Length), out var existant))
        {
            numero = existant + 1;
        }
        return baseName + numero.ToString("D5");
    }
}
