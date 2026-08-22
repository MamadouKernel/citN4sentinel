using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Persistence;
using N4Sentinel.Infrastructure.Security;

namespace N4Sentinel.Infrastructure.Connectors;

/// <summary>
/// Construit une cible de connecteur a partir du referentiel.
///
/// C'est le chainon qui manquait : le connecteur savait se connecter, le
/// referentiel savait ou, mais rien ne les reliait. Toute connexion a un
/// serveur N4 passe desormais par ici - ce qui garantit qu'aucun code ne peut
/// atteindre une machine qui n'a pas ete declaree et validee dans le
/// referentiel, comme l'exige le cahier des charges.
/// </summary>
public sealed class ConnectorTargetFactory(
    IDbContextFactory<N4SentinelDbContext> dbFactory,
    CredentialStore credentials,
    ILogger<ConnectorTargetFactory> logger)
{
    /// <summary>Construit la cible d'un serveur identifie par sa cle.</summary>
    public async Task<TargetResolution> CreateAsync(Guid serverId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var server = await db.Servers
            .AsNoTracking()
            .Include(s => s.Environment)
            .FirstOrDefaultAsync(s => s.Id == serverId, ct);

        return server is null
            ? TargetResolution.Failed("Serveur introuvable dans le referentiel.")
            : await CreateAsync(server, ct);
    }

    /// <summary>
    /// Construit la cible d'un serveur POUR UN OPERATEUR NOMME.
    ///
    /// C'est ce qui donne son sens a la tracabilite : la session WinRM s'ouvre
    /// sous le compte de la personne qui a demande l'action, de sorte que le
    /// journal de securite du serveur N4 la nomme elle, et pas un compte de
    /// service derriere lequel tout le monde se confond.
    ///
    /// A N'UTILISER QUE POUR DU TRAVAIL DEMANDE PAR UN HUMAIN. La supervision
    /// de fond, elle, n'emprunte l'identite de personne : lui preter le compte
    /// du dernier connecte ferait porter a cet operateur des releves faits par
    /// la machine a trois heures du matin - ce qui detruirait l'attribution au
    /// lieu de la servir.
    /// </summary>
    public async Task<TargetResolution> CreateForActorAsync(
        N4Server server, string? actorLogin, CancellationToken ct = default)
        => await CreateAsync(server, ct, actorLogin);

    /// <summary>Construit la cible d'un serveur deja charge.</summary>
    public async Task<TargetResolution> CreateAsync(N4Server server, CancellationToken ct = default)
        => await CreateAsync(server, ct, null);

    /// <summary>
    /// Construit une cible avec un compte IMPOSE, sans consulter le referentiel
    /// des comptes.
    ///
    /// Sert a eprouver un compte qu'on vient de saisir : le tester avant de
    /// s'en servir est la seule facon de ne pas decouvrir la faute de frappe
    /// pendant l'incident.
    /// </summary>
    public TargetResolution CreateWithCredential(N4Server server, TechnicalCredential credential)
    {
        if (string.IsNullOrWhiteSpace(server.HostName))
            return TargetResolution.Failed("Le serveur n'a pas de nom d'hote.");

        return Construire(server, credential, $"compte {credential.UserName}");
    }

    private async Task<TargetResolution> CreateAsync(
        N4Server server, CancellationToken ct, string? actorLogin)
    {
        if (string.IsNullOrWhiteSpace(server.HostName))
            return TargetResolution.Failed("Le serveur n'a pas de nom d'hote.");

        // --- 1. Compte nominatif de l'operateur, s'il en a un d'utilisable ---
        if (!string.IsNullOrWhiteSpace(actorLogin))
        {
            var propre = await credentials.GetForLoginAsync(actorLogin, ct);

            if (propre is not null && propre.IsUsable)
                return Construire(server, propre,
                    $"compte {propre.UserName} ({propre.OwnerDisplayName})");

            if (propre is { RequiresReentry: true })
                return TargetResolution.Failed(
                    $"Votre compte d'exploitation {propre.UserName} a ete ecarte : {propre.InvalidationReason} "
                    + "Ressaisissez-le depuis « Mon compte d'exploitation » avant de relancer.");

            // Aucun compte nominatif : on retombe sur le compte partage. Ce
            // n'est pas une erreur - c'est le cas d'un operateur qui n'a pas
            // encore fait sa saisie, ou d'un site qui n'en veut pas.
        }

        // --- 2. Compte partage : la fiche serveur l'emporte sur le defaut de
        // l'environnement. Un seul serveur hors domaine peut ainsi porter son
        // propre compte sans imposer d'exception a tous les autres.
        var reference = !string.IsNullOrWhiteSpace(server.CredentialReference)
            ? server.CredentialReference
            : server.Environment?.DefaultCredentialReference;

        TechnicalCredential? credential = null;
        var origine = "identite du processus";

        if (!string.IsNullOrWhiteSpace(reference))
        {
            credential = await credentials.GetByReferenceAsync(server.EnvironmentId, reference, ct);

            if (credential is null)
            {
                // Se rabattre en silence sur l'identite du processus serait le
                // pire des comportements : la connexion pourrait reussir avec
                // les mauvais droits, et personne ne saurait pourquoi.
                return TargetResolution.Failed(
                    $"Le compte technique '{reference}' est reference par {server.HostName} " +
                    "mais n'existe pas dans cet environnement. Corrigez la fiche serveur " +
                    "ou creez le compte manquant.");
            }

            if (!credential.IsUsable)
                return TargetResolution.Failed(
                    $"Le compte technique '{credential.Label}' est incomplet : {credential.SecretState}. " +
                    "Renseignez son mot de passe avant de l'utiliser.");

            origine = credential.Mode == CredentialMode.IdentiteDuProcessus
                ? $"identite du processus (via '{credential.Label}')"
                : $"compte {credential.UserName} (via '{credential.Label}')";
        }

        return Construire(server, credential, origine);
    }

    private TargetResolution Construire(
        N4Server server, TechnicalCredential? credential, string origine)
    {
        var target = new ConnectorTarget
        {
            HostName = server.HostName,
            WinRmPort = server.WinRmPort,
            UseSsl = server.UseSsl,
            IsLocal = EstMachineLocale(server.HostName),
            Credential = credential is null ? null : credentials.BuildPSCredential(credential),
            Timeout = TimeSpan.FromSeconds(30)
        };

        if (target.IsLocal)
            logger.LogDebug(
                "{Hote} est la machine hebergeant N4 Sentinel : execution locale, sans passer par WinRM.",
                server.HostName);

        return TargetResolution.Ok(target, credential, origine);
    }

    /// <summary>
    /// Determine si l'hote designe la machine qui execute N4 Sentinel.
    ///
    /// Passer par une session WinRM vers sa propre machine ajoute une
    /// dependance a WinRM, une negociation d'authentification et une contrainte
    /// d'elevation, pour executer ce qu'on peut executer directement. Sur un
    /// serveur ou N4 Sentinel cohabiterait avec un composant N4, cela ferait
    /// echouer la supervision de ce composant precis - sans raison.
    /// </summary>
    private static bool EstMachineLocale(string hostName)
    {
        if (string.Equals(hostName, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(hostName, "127.0.0.1", StringComparison.Ordinal)) return true;
        if (string.Equals(hostName, "::1", StringComparison.Ordinal)) return true;
        if (string.Equals(hostName, Environment.MachineName, StringComparison.OrdinalIgnoreCase)) return true;

        // Nom pleinement qualifie de la meme machine : SERVEUR.domaine.local
        var court = hostName.Split('.')[0];
        return string.Equals(court, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Resultat de la resolution d'une cible : la cible elle-meme, le compte
/// retenu, et de quoi expliquer a l'operateur sous quelle identite la
/// connexion sera tentee.
/// </summary>
public sealed record TargetResolution
{
    public bool Succeeded { get; init; }
    public ConnectorTarget? Target { get; init; }
    public TechnicalCredential? Credential { get; init; }

    /// <summary>Formulation lisible de l'identite employee, sans aucun secret.</summary>
    public string IdentityDescription { get; init; } = string.Empty;

    public string? Error { get; init; }

    public static TargetResolution Ok(ConnectorTarget target, TechnicalCredential? credential, string identity) =>
        new() { Succeeded = true, Target = target, Credential = credential, IdentityDescription = identity };

    public static TargetResolution Failed(string error) =>
        new() { Succeeded = false, Error = error };
}
