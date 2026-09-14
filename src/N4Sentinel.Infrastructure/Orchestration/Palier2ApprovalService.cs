using Microsoft.EntityFrameworkCore;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Orchestration;

/// <summary>
/// Garde d'autorisation du Palier 2 (AutomatiqueBoutEnBout) par environnement.
///
/// PRINCIPE RG-039 (CDC §2.2.3) : le passage en automatique intégral n'est JAMAIS
/// un réglage par défaut. Il exige une décision explicite, horodatée, attribuée
/// à un acteur humain habilité, et enregistrée au journal d'audit. Un
/// environnement qui n'a pas reçu cette approbation reste verrouillé au Palier 1
/// (validation humaine étape par étape), même si un workflow ou une exécution
/// tente de se déclarer AutomatiqueBoutEnBout.
///
/// Deux points d'entrée :
///   - IsApprovedForEnv : garde runtime appelée depuis Preflight, SaveWorkflow
///     et ControlExecutionUseCase pour BLOQUER s'il y a tentative non autorisée.
///   - SetApprovalAsync : écriture appelée depuis l'écran d'admin Environnement
///     par un AdminN4 ; trace AuditAction.Approbation + AuditAction.Revocation.
/// </summary>
public sealed class Palier2ApprovalService(
    IDbContextFactory<N4SentinelDbContext> dbFactory,
    IAuditWriter auditWriter)
{
    /// <summary>
    /// Indique si l'environnement a reçu une approbation explicite pour le
    /// Palier 2. Un résultat false ne dit pas pourquoi — la décision d'afficher
    /// le motif détaillé revient à l'appelant, selon qu'il est UI ou runtime.
    /// </summary>
    public async Task<bool> IsApprovedForEnv(Guid environmentId, CancellationToken ct = default)
    {
        if (environmentId == Guid.Empty) return false;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Environments
            .AsNoTracking()
            .Where(e => e.Id == environmentId)
            .Select(e => e.Palier2ApprovedBy != null && e.Palier2ApprovedAt != null)
            .SingleOrDefaultAsync(ct);
    }

    /// <summary>
    /// Retourne l'état complet d'approbation Palier 2 d'un environnement, y
    /// compris l'acteur et l'horodatage. Utile à l'écran Admin Environnement
    /// pour afficher le bandeau "État actuel".
    /// </summary>
    public async Task<Palier2ApprovalState?> GetStateAsync(Guid environmentId, CancellationToken ct = default)
    {
        if (environmentId == Guid.Empty) return null;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var env = await db.Environments
            .AsNoTracking()
            .Where(e => e.Id == environmentId)
            .Select(e => new { e.Code, e.Name, e.Palier2ApprovedBy, e.Palier2ApprovedAt })
            .FirstOrDefaultAsync(ct);

        if (env is null) return null;

        return new Palier2ApprovalState(
            environmentId,
            env.Code,
            env.Name,
            env.Palier2ApprovedBy,
            env.Palier2ApprovedAt);
    }

    /// <summary>
    /// Positionne ou retire l'approbation Palier 2 d'un environnement.
    /// </summary>
    /// <param name="environmentId">Environnement cible.</param>
    /// <param name="approvedBy">
    /// Acteur qui approuve. Si NULL, la méthode interprète l'appel comme une
    /// RÉVOCATION de l'approbation précédente (journalisé AuditAction.Revocation).
    /// </param>
    /// <param name="approverIsAdmin">
    /// Vrai quand l'appelant a été vérifié comme AdminN4. Si false, la méthode
    /// refuse systématiquement — pas de délégation possible sur cette décision.
    /// </param>
    /// <param name="ct">Jeton d'annulation.</param>
    /// <returns>Null en succès, sinon message d'erreur affichable à l'utilisateur.</returns>
    public async Task<string?> SetApprovalAsync(
        Guid environmentId,
        string? approvedBy,
        bool approverIsAdmin,
        CancellationToken ct = default)
    {
        if (!approverIsAdmin)
        {
            await auditWriter.WriteAsync(
                AuditAction.TentativeNonAutorisee, AuditOutcome.Refuse,
                actor: approvedBy ?? "anonyme",
                entityType: nameof(N4Environment), entityId: environmentId.ToString(),
                environmentId: environmentId,
                reason: "Tentative d'approbation Palier 2 par un acteur non AdminN4.",
                ct: ct);
            return "Seul un administrateur N4 peut approuver ou révoquer le Palier 2 automatique.";
        }

        if (approvedBy is not null && string.IsNullOrWhiteSpace(approvedBy))
        {
            return "L'identité de l'acteur approbateur ne peut pas être vide.";
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var env = await db.Environments.FirstOrDefaultAsync(e => e.Id == environmentId, ct);
        if (env is null) return "Environnement introuvable.";

        var etaitApprouve = env.Palier2ApprovedBy != null && env.Palier2ApprovedAt != null;
        var seraApprouve = approvedBy is not null;

        if (etaitApprouve && seraApprouve
            && string.Equals(env.Palier2ApprovedBy, approvedBy, StringComparison.Ordinal))
        {
            return null;
        }

        if (seraApprouve)
        {
            env.Palier2ApprovedBy = approvedBy;
            env.Palier2ApprovedAt = DateTime.UtcNow;

            await auditWriter.WriteAsync(
                AuditAction.Approbation, AuditOutcome.Succes,
                actor: approvedBy!,
                entityType: nameof(N4Environment), entityId: env.Id.ToString(),
                entityLabel: $"{env.Code} — {env.Name}",
                environmentId: env.Id,
                reason: "Approbation Palier 2 (AutomatiqueBoutEnBout) positionnée sur l'environnement.",
                detail: $"Validé par {approvedBy} le {env.Palier2ApprovedAt:O}.",
                ct: ct);
        }
        else
        {
            var ancienActeur = env.Palier2ApprovedBy;
            var ancienneDate = env.Palier2ApprovedAt;

            env.Palier2ApprovedBy = null;
            env.Palier2ApprovedAt = null;

            await auditWriter.WriteAsync(
                AuditAction.Revocation, AuditOutcome.Succes,
                actor: approvedBy ?? "systeme",
                entityType: nameof(N4Environment), entityId: env.Id.ToString(),
                entityLabel: $"{env.Code} — {env.Name}",
                environmentId: env.Id,
                reason: "Révocation de l'approbation Palier 2 sur l'environnement.",
                detail: ancienActeur is not null
                    ? $"Approbation précédente de {ancienActeur} le {ancienneDate:O} retirée par {approvedBy ?? "systeme"}."
                    : $"Révocation demandée par {approvedBy ?? "systeme"} (état déjà non approuvé).",
                ct: ct);
        }

        await db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>
    /// Vérifie, pour une étape donnée sur un environnement Palier 2, que la
    /// référence de connecteur technique utilisée est explicitement autorisée
    /// par la liste <see cref="N4Environment.Palier2AuthorizedConnectorReferences"/>
    /// (PB-23 : contrôle supplémentaire sur les étapes destructives).
    ///
    /// RÈGLE :
    ///   - Si l'environnement n'est PAS approuvé Palier 2 → true (sans contrôle).
    ///   - Si la liste est NULL ou vide → seul
    ///     <see cref="N4Environment.DefaultCredentialReference"/> est autorisé
    ///     (s'il est lui-même renseigné).
    ///   - Sinon, la référence effective (celle du serveur OU à défaut celle
    ///     par défaut de l'environnement) doit apparaître dans la liste.
    ///
    /// Une étape destructive qui échoue à ce contrôle est bloquée et reste en
    /// attente de validation humaine (même garde que RG-039).
    /// </summary>
    /// <param name="environmentId">Environnement cible.</param>
    /// <param name="serverCredentialReference">
    /// Référence declarée sur le serveur. Null = utiliser la valeur par défaut
    /// de l'environnement.
    /// </param>
    /// <param name="stepName">Nom de l'étape, pour les journaux.</param>
    /// <param name="actor">Acteur à l'origine de l'exécution (pour audit).</param>
    /// <param name="ct">Jeton d'annulation.</param>
    /// <returns>true si autorisé, false si bloqué.</returns>
    public async Task<bool> ValidateConnectorAllowedAsync(
        Guid environmentId,
        string? serverCredentialReference,
        string stepName,
        string? actor,
        CancellationToken ct = default)
    {
        if (environmentId == Guid.Empty) return false;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var env = await db.Environments
            .AsNoTracking()
            .Where(e => e.Id == environmentId)
            .Select(e => new
            {
                e.Id,
                e.Code,
                e.Name,
                e.Palier2ApprovedBy,
                e.Palier2ApprovedAt,
                e.DefaultCredentialReference,
                e.Palier2AuthorizedConnectorReferences
            })
            .FirstOrDefaultAsync(ct);

        if (env is null) return false;

        var estPalier2 = env.Palier2ApprovedBy is not null && env.Palier2ApprovedAt is not null;
        if (!estPalier2) return true;

        var referenceEffective =
            !string.IsNullOrWhiteSpace(serverCredentialReference)
                ? serverCredentialReference.Trim()
                : env.DefaultCredentialReference?.Trim();

        if (string.IsNullOrWhiteSpace(referenceEffective))
        {
            await auditWriter.WriteAsync(
                AuditAction.Palier2ConnecteurInterdit, AuditOutcome.Refuse,
                actor: actor ?? "systeme",
                entityType: nameof(N4Environment), entityId: environmentId.ToString(),
                entityLabel: $"{env.Code} — {env.Name}",
                environmentId: environmentId,
                reason:
                    $"PB-23 : étape destructive « {stepName} » — aucune référence de credential " +
                    "n'est disponible (ni sur le serveur, ni par défaut sur l'environnement).",
                ct: ct);
            return false;
        }

        var liste = env.Palier2AuthorizedConnectorReferences
            ?.Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .ToList();

        bool autorise;
        if (liste is null || liste.Count == 0)
        {
            autorise = string.Equals(
                referenceEffective,
                env.DefaultCredentialReference?.Trim(),
                StringComparison.Ordinal);
        }
        else
        {
            autorise = liste.Contains(referenceEffective, StringComparer.Ordinal);
        }

        if (autorise)
        {
            await auditWriter.WriteAsync(
                AuditAction.Palier2ConnecteurValide, AuditOutcome.Succes,
                actor: actor ?? "systeme",
                entityType: nameof(N4Environment), entityId: environmentId.ToString(),
                entityLabel: $"{env.Code} — {env.Name}",
                environmentId: environmentId,
                reason:
                    $"PB-23 : étape destructive « {stepName} » — connecteur « {referenceEffective} » autorisé.",
                detail:
                    liste is null || liste.Count == 0
                        ? "Mode liste vide : seule la référence par défaut de l'environnement est acceptée."
                        : $"Liste autorisée : {string.Join(", ", liste)}.",
                ct: ct);
            return true;
        }

        await auditWriter.WriteAsync(
            AuditAction.Palier2ConnecteurInterdit, AuditOutcome.Refuse,
            actor: actor ?? "systeme",
            entityType: nameof(N4Environment), entityId: environmentId.ToString(),
            entityLabel: $"{env.Code} — {env.Name}",
            environmentId: environmentId,
            reason:
                $"PB-23 : étape destructive « {stepName} » — connecteur « {referenceEffective} » " +
                "absent de la liste des références autorisées sur l'environnement Palier 2.",
            detail:
                liste is null || liste.Count == 0
                    ? $"Référence par défaut attendue : {env.DefaultCredentialReference ?? "<aucune>"}."
                    : $"Liste autorisée : {string.Join(", ", liste)}.",
            ct: ct);
        return false;
    }
}

/// <summary>
/// État d'approbation Palier 2 d'un environnement, projeté pour affichage UI.
/// </summary>
public sealed record Palier2ApprovalState(
    Guid EnvironmentId,
    string Code,
    string Name,
    string? ApprovedBy,
    DateTime? ApprovedAt)
{
    public bool IsApproved => ApprovedBy is not null && ApprovedAt is not null;
}
