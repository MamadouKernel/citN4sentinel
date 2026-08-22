using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Connectors;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Security;

/// <summary>
/// Comptes d'exploitation nominatifs : un compte d'administration par
/// operateur, saisi par lui, employe pour les actions qu'il declenche.
///
/// POURQUOI CE SERVICE EXISTE. Un compte de service partage rend toutes les
/// actions anonymes du point de vue des serveurs N4 : leur journal de securite
/// nomme le compte, jamais la personne. En ouvrant la session WinRM sous le
/// compte de celui qui a demande l'action, la piste applicative et la piste
/// systeme designent le meme individu, et deviennent recoupables.
///
/// CE QUE CE SERVICE NE COUVRE PAS. Le travail non surveille - la supervision
/// de fond, qui balaie les composants sans que personne ne l'ait demande.
/// Celui-la garde l'identite du processus ou un compte partage : lui preter le
/// compte du dernier connecte ferait porter a cet operateur des releves faits
/// par la machine la nuit, ce qui detruirait l'attribution au lieu de la
/// servir.
/// </summary>
public sealed class OperatorCredentialService(
    IDbContextFactory<N4SentinelDbContext> dbFactory,
    CredentialStore credentials,
    ConnectorTargetFactory targetFactory,
    IN4Connector connector,
    ILogger<OperatorCredentialService> logger)
{
    /// <summary>
    /// Au-dela de ce delai, le compte est reverifie a la connexion. Assez court
    /// pour attraper une expiration le jour meme, assez long pour ne pas
    /// imposer un aller-retour WinRM a chaque rafraichissement de page.
    /// </summary>
    private static readonly TimeSpan FraicheurVerification = TimeSpan.FromHours(12);

    // -----------------------------------------------------------------------
    // Etat vu par les ecrans
    // -----------------------------------------------------------------------
    public async Task<OperatorCredentialState> GetStateAsync(
        string userId, CancellationToken ct = default)
    {
        var compte = await credentials.GetForOwnerAsync(userId, ct);

        if (compte is null)
            return new OperatorCredentialState { Situation = OperatorCredentialSituation.Absent };

        if (compte.RequiresReentry)
            return new OperatorCredentialState
            {
                Situation = OperatorCredentialSituation.ARessaisir,
                UserName = compte.UserName,
                Motif = compte.InvalidationReason,
                LastVerifiedAt = compte.LastVerifiedAt
            };

        return new OperatorCredentialState
        {
            Situation = OperatorCredentialSituation.Actif,
            UserName = compte.UserName,
            PasswordSetAt = compte.PasswordSetAt,
            LastVerifiedAt = compte.LastVerifiedAt,
            LastVerificationResult = compte.LastVerificationResult
        };
    }

    // -----------------------------------------------------------------------
    // Enregistrement
    // -----------------------------------------------------------------------
    /// <summary>
    /// Enregistre le compte de l'operateur, APRES l'avoir eprouve.
    ///
    /// L'ordre compte : on ecrit le compte, on le teste, et un echec
    /// d'authentification le remet aussitot en ressaisie. Enregistrer un compte
    /// non eprouve reviendrait a decouvrir la faute de frappe pendant
    /// l'incident, ce que ce projet existe precisement pour eviter.
    /// </summary>
    public async Task<EnrolmentResult> EnrolAsync(
        string userId, string login, string displayName,
        string userName, string password, CancellationToken ct = default)
    {
        var erreur = await credentials.SaveOwnAsync(userId, login, displayName, userName, password, ct);
        if (erreur is not null) return EnrolmentResult.Refuse(erreur);

        var verification = await VerifyAsync(userId, ct);

        // Un mot de passe que le domaine vient de refuser n'a aucune raison de
        // rester en base : il est faux, et le conserver chiffre n'apporte que
        // du risque. On efface plutot que de marquer « a ressaisir ».
        if (verification.Situation == VerificationSituation.AuthentificationRefusee)
            await credentials.EraseForOwnerAsync(userId, ct);

        return verification.Situation switch
        {
            VerificationSituation.Reussie => EnrolmentResult.Ok(
                $"Compte {userName} enregistré et vérifié sur {verification.HostName}. "
                + $"Le serveur a répondu sous l'identité « {verification.RemoteIdentity} »."),

            VerificationSituation.AuthentificationRefusee => EnrolmentResult.Refuse(
                "Le domaine a refusé ce compte ou ce mot de passe. Rien n'a été conservé : "
                + "vérifiez la saisie, en particulier le domaine devant le nom d'utilisateur."),

            VerificationSituation.NonVerifiable => EnrolmentResult.OkNonVerifie(
                $"Compte {userName} enregistré, mais NON vérifié : {verification.Message} "
                + "Il sera éprouvé à la première action réelle."),

            _ => EnrolmentResult.Ok($"Compte {userName} enregistré.")
        };
    }

    // -----------------------------------------------------------------------
    // Verification
    // -----------------------------------------------------------------------
    /// <summary>
    /// Verifie le compte a la connexion, si le dernier controle date.
    ///
    /// C'est ce qui detecte une expiration ou un changement de mot de passe
    /// dans le domaine : personne ne nous previendra, il faut aller voir.
    /// </summary>
    public async Task<VerificationOutcome?> VerifyIfStaleAsync(
        string userId, CancellationToken ct = default)
    {
        var compte = await credentials.GetForOwnerAsync(userId, ct);
        if (compte is null || compte.RequiresReentry) return null;

        var recent = compte.LastVerifiedAt is { } quand
                     && DateTimeOffset.UtcNow - quand < FraicheurVerification;

        return recent ? null : await VerifyAsync(userId, ct);
    }

    public async Task<VerificationOutcome> VerifyAsync(
        string userId, CancellationToken ct = default)
    {
        var compte = await credentials.GetForOwnerAsync(userId, ct);
        if (compte is null)
            return VerificationOutcome.NonVerifiable("aucun compte d'exploitation enregistré.");

        var (serveur, resolution) = await ChoisirServeurTemoinAsync(compte, ct);

        if (serveur is null || resolution?.Target is null)
            return VerificationOutcome.NonVerifiable(
                "aucun serveur DISTANT validé ne permet d'éprouver un compte pour l'instant. "
                + "Déclarez et validez au moins un serveur N4 autre que celui qui héberge l'application.");

        var ping = await connector.PingAsync(resolution.Target, ct);

        if (ping.Succeeded)
        {
            await credentials.RecordVerificationAsync(
                compte.Id, true,
                $"Verifie sur {serveur.HostName} : session ouverte sous {ping.Value}.", ct);

            return VerificationOutcome.Reussie(serveur.HostName, ping.Value ?? compte.UserName ?? "");
        }

        if (ping.Failure == ConnectorFailure.AuthentificationRefusee)
        {
            // Le compte sort du jeu immediatement. Ne pas reessayer est la
            // regle : c'est ce qui evite de verrouiller le compte de domaine.
            await credentials.InvalidateAsync(
                compte.Id,
                $"Authentification refusee sur {serveur.HostName} le {DateTimeOffset.Now:dd/MM/yyyy a HH:mm}. "
                + "Mot de passe expire ou modifie dans le domaine.",
                ct);

            logger.LogWarning(
                "Compte d'exploitation de {Proprietaire} refuse par le domaine : ressaisie demandee.",
                compte.OwnerDisplayName);

            return VerificationOutcome.AuthentificationRefusee(serveur.HostName);
        }

        // Serveur injoignable, WinRM ferme, delai depasse : ce n'est PAS la
        // faute du compte. L'invalider ici obligerait tout le monde a ressaisir
        // des qu'un serveur redemarre.
        await credentials.RecordVerificationAsync(
            compte.Id, false,
            $"Non verifiable sur {serveur.HostName} : {ping.Error}", ct);

        return VerificationOutcome.NonVerifiable(
            $"{serveur.HostName} n'a pas répondu ({ping.Error}).");
    }

    /// <summary>
    /// Serveur sur lequel eprouver un compte. N'importe quel serveur valide
    /// fait l'affaire - on ne cherche pas le « bon », seulement a savoir si le
    /// couple compte/mot de passe est accepte par le domaine.
    ///
    /// UNE SEULE EXCLUSION, ET ELLE EST CAPITALE : la machine qui heberge
    /// l'application. Le connecteur y execute en local, SANS WinRM et donc SANS
    /// employer le compte fourni. Une verification menee la aurait repondu
    /// « compte verifie » sur un mot de passe entierement faux - exactement le
    /// genre de preuve creuse que ce projet existe pour eliminer.
    /// </summary>
    private async Task<(N4Server?, TargetResolution?)> ChoisirServeurTemoinAsync(
        TechnicalCredential compte, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var candidats = await db.Servers
            .AsNoTracking()
            .Include(s => s.Environment)
            .Where(s => s.Status == LifecycleStatus.Valide || s.Status == LifecycleStatus.Actif)
            .OrderBy(s => s.HostName)
            .ToListAsync(ct);

        foreach (var serveur in candidats)
        {
            var resolution = targetFactory.CreateWithCredential(serveur, compte);
            if (!resolution.Succeeded || resolution.Target is null) continue;

            if (resolution.Target.IsLocal)
            {
                logger.LogDebug(
                    "{Hote} heberge l'application : ecarte de la verification, une session locale "
                    + "n'emploierait pas le compte a eprouver.", serveur.HostName);
                continue;
            }

            return (serveur, resolution);
        }

        return (null, null);
    }

    // -----------------------------------------------------------------------
    // Cycle de vie
    // -----------------------------------------------------------------------
    /// <summary>
    /// Efface le compte d'un operateur. Appele a sa desactivation : un secret
    /// qui survit a son proprietaire finira par servir a quelqu'un d'autre.
    /// </summary>
    public Task<bool> EraseAsync(string userId, CancellationToken ct = default)
        => credentials.EraseForOwnerAsync(userId, ct);
}

// ---------------------------------------------------------------------------
public enum OperatorCredentialSituation { Absent, Actif, ARessaisir }

public sealed record OperatorCredentialState
{
    public OperatorCredentialSituation Situation { get; init; }
    public string? UserName { get; init; }
    public string? Motif { get; init; }
    public DateTimeOffset? PasswordSetAt { get; init; }
    public DateTimeOffset? LastVerifiedAt { get; init; }
    public string? LastVerificationResult { get; init; }

    public bool DemandeUneAction => Situation != OperatorCredentialSituation.Actif;
}

public enum VerificationSituation { Reussie, AuthentificationRefusee, NonVerifiable }

public sealed record VerificationOutcome
{
    public VerificationSituation Situation { get; init; }
    public string HostName { get; init; } = string.Empty;
    public string RemoteIdentity { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;

    public static VerificationOutcome Reussie(string host, string identity) =>
        new() { Situation = VerificationSituation.Reussie, HostName = host, RemoteIdentity = identity };

    public static VerificationOutcome AuthentificationRefusee(string host) =>
        new() { Situation = VerificationSituation.AuthentificationRefusee, HostName = host };

    public static VerificationOutcome NonVerifiable(string message) =>
        new() { Situation = VerificationSituation.NonVerifiable, Message = message };
}

public sealed record EnrolmentResult
{
    public bool Succeeded { get; init; }
    public bool Verified { get; init; }
    public string Message { get; init; } = string.Empty;

    public static EnrolmentResult Ok(string message) =>
        new() { Succeeded = true, Verified = true, Message = message };

    public static EnrolmentResult OkNonVerifie(string message) =>
        new() { Succeeded = true, Verified = false, Message = message };

    public static EnrolmentResult Refuse(string message) =>
        new() { Succeeded = false, Message = message };
}
