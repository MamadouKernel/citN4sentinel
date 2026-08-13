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

    /// <summary>Construit la cible d'un serveur deja charge.</summary>
    public async Task<TargetResolution> CreateAsync(N4Server server, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(server.HostName))
            return TargetResolution.Failed("Le serveur n'a pas de nom d'hote.");

        // Resolution du compte : la fiche serveur l'emporte sur le defaut de
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
