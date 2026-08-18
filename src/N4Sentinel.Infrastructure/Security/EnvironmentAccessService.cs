using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Security;

/// <summary>
/// Cloisonnement des environnements (SEC-004, audit SEC-A1).
///
/// LA VÉRIFICATION VIT DANS LES SERVICES, PAS DANS LES ÉCRANS. Un contrôle
/// posé uniquement sur les pages se contourne en appelant directement par
/// identifiant : c'est la première chose que fait quiconque cherche à passer
/// outre. Le refus doit donc tomber là où l'action se décide — au pré-check
/// pour l'exécution, à la lecture pour la consultation.
///
/// DEUX PRINCIPES GOUVERNENT LES CAS LIMITES.
///
/// 1. L'administrateur de la solution n'est jamais cloisonné. Il administre le
///    référentiel : lui interdire un environnement l'empêcherait de le créer.
///
/// 2. Tant qu'AUCUNE habilitation n'est déclarée, le cloisonnement ne
///    s'applique pas. Activer une restriction sur une base existante
///    verrouillerait tout le monde hors de tous les environnements du jour au
///    lendemain, y compris pendant un incident. Le cloisonnement entre en
///    vigueur dès la première habilitation posée — ce qui en fait une décision
///    explicite, et non un effet de bord d'une mise à jour.
/// </summary>
public sealed class EnvironmentAccessService(
    IDbContextFactory<N4SentinelDbContext> dbFactory,
    IConfiguration configuration,
    ILogger<EnvironmentAccessService> logger)
{
    /// <summary>
    /// Permet d'exiger le cloisonnement même sans habilitation déclarée —
    /// pour un site qui veut fermer d'emblée plutôt que d'ouvrir puis
    /// restreindre.
    /// </summary>
    public const string CleCloisonnementStrict = "N4Sentinel:Securite:CloisonnementStrict";

    /// <summary>Rôles jamais cloisonnés : ils administrent le référentiel lui-même.</summary>
    private static readonly string[] RolesNonCloisonnes = [N4Roles.AdministrateurSolution];

    // -----------------------------------------------------------------------
    // Décision
    // -----------------------------------------------------------------------
    /// <summary>Le porteur peut-il CONSULTER cet environnement ?</summary>
    public Task<AccessDecision> CanViewAsync(
        ClaimsPrincipal utilisateur, Guid environmentId, CancellationToken ct = default) =>
        DeciderAsync(utilisateur, environmentId, actionRequise: false, ct);

    /// <summary>Le porteur peut-il AGIR sur cet environnement ?</summary>
    public Task<AccessDecision> CanActAsync(
        ClaimsPrincipal utilisateur, Guid environmentId, CancellationToken ct = default) =>
        DeciderAsync(utilisateur, environmentId, actionRequise: true, ct);

    private async Task<AccessDecision> DeciderAsync(
        ClaimsPrincipal utilisateur, Guid environmentId, bool actionRequise, CancellationToken ct)
    {
        if (utilisateur.Identity?.IsAuthenticated != true)
            return AccessDecision.Refuse("Aucune session authentifiée.");

        if (RolesNonCloisonnes.Any(utilisateur.IsInRole))
            return AccessDecision.Autorise();

        var identifiant = utilisateur.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(identifiant))
            return AccessDecision.Refuse("Session sans identifiant d'utilisateur.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Aucune habilitation nulle part : le cloisonnement n'est pas encore en
        // service. On ne verrouille pas un site qui vient de mettre a jour.
        var strict = configuration.GetValue(CleCloisonnementStrict, false);

        if (!strict && !await db.EnvironmentGrants.AnyAsync(ct))
            return AccessDecision.Autorise();

        var habilitation = await db.EnvironmentGrants
            .AsNoTracking()
            .FirstOrDefaultAsync(g => g.UserId == identifiant && g.EnvironmentId == environmentId, ct);

        var environnement = await db.Environments
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == environmentId, ct);

        var nom = environnement?.Code ?? "cet environnement";

        if (habilitation is null)
            return AccessDecision.Refuse(
                $"Vous n'êtes pas habilité sur « {nom} ». "
                + "Un rôle dit ce que vous savez faire ; l'habilitation dit où vous avez le droit de le faire. "
                + "Demandez l'accès à l'administrateur de la solution.");

        if (habilitation.IsExpired)
            return AccessDecision.Refuse(
                $"Votre habilitation sur « {nom} » a expiré le "
                + $"{habilitation.ExpiresAt!.Value.ToLocalTime():dd/MM/yyyy}.");

        if (actionRequise && habilitation.Level != EnvironmentGrantLevel.Action)
            return AccessDecision.Refuse(
                $"Vous êtes habilité à CONSULTER « {nom} », pas à y agir. "
                + "Consulter et agir sont deux habilitations distinctes — "
                + "c'est ce qui permet de suivre la Production sans pouvoir l'arrêter.");

        return AccessDecision.Autorise();
    }

    /// <summary>Environnements que le porteur peut voir. Sert à filtrer les listes.</summary>
    public async Task<List<Guid>> VisibleEnvironmentsAsync(
        ClaimsPrincipal utilisateur, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var tous = await db.Environments.AsNoTracking().Select(e => e.Id).ToListAsync(ct);

        if (RolesNonCloisonnes.Any(utilisateur.IsInRole)) return tous;

        var strict = configuration.GetValue(CleCloisonnementStrict, false);
        if (!strict && !await db.EnvironmentGrants.AnyAsync(ct)) return tous;

        var identifiant = utilisateur.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(identifiant)) return [];

        return await db.EnvironmentGrants
            .AsNoTracking()
            .Where(g => g.UserId == identifiant
                        && (g.ExpiresAt == null || g.ExpiresAt > DateTimeOffset.UtcNow))
            .Select(g => g.EnvironmentId)
            .Distinct()
            .ToListAsync(ct);
    }

    // -----------------------------------------------------------------------
    // Administration des habilitations
    // -----------------------------------------------------------------------
    public async Task<List<EnvironmentGrant>> GetGrantsAsync(
        Guid? environmentId = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var requete = db.EnvironmentGrants.AsNoTracking().Include(g => g.Environment).AsQueryable();
        if (environmentId is not null) requete = requete.Where(g => g.EnvironmentId == environmentId);

        return await requete.OrderBy(g => g.UserName).ToListAsync(ct);
    }

    public async Task<string?> GrantAsync(
        string userId, string userName, Guid environmentId, EnvironmentGrantLevel niveau,
        string? motif, DateTimeOffset? expiration, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId)) return "Aucun utilisateur désigné.";

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var existante = await db.EnvironmentGrants
            .FirstOrDefaultAsync(g => g.UserId == userId && g.EnvironmentId == environmentId, ct);

        if (existante is null)
        {
            db.EnvironmentGrants.Add(new EnvironmentGrant
            {
                UserId = userId,
                UserName = userName,
                EnvironmentId = environmentId,
                Level = niveau,
                Reason = motif,
                ExpiresAt = expiration
            });
        }
        else
        {
            existante.Level = niveau;
            existante.Reason = motif;
            existante.ExpiresAt = expiration;
            existante.UserName = userName;
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Habilitation {Niveau} accordée à {Utilisateur} sur l'environnement {Env}.",
            niveau, userName, environmentId);

        return null;
    }

    public async Task RevokeAsync(Guid grantId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var habilitation = await db.EnvironmentGrants.FirstOrDefaultAsync(g => g.Id == grantId, ct);
        if (habilitation is null) return;

        db.EnvironmentGrants.Remove(habilitation);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Habilitation retirée à {Utilisateur} sur l'environnement {Env}.",
            habilitation.UserName, habilitation.EnvironmentId);
    }

    /// <summary>Vrai si le cloisonnement est effectivement en vigueur.</summary>
    public async Task<bool> IsEnforcedAsync(CancellationToken ct = default)
    {
        if (configuration.GetValue(CleCloisonnementStrict, false)) return true;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.EnvironmentGrants.AnyAsync(ct);
    }
}

public sealed record AccessDecision
{
    public bool Allowed { get; init; }
    public string? Reason { get; init; }

    public static AccessDecision Autorise() => new() { Allowed = true };
    public static AccessDecision Refuse(string motif) => new() { Allowed = false, Reason = motif };
}
