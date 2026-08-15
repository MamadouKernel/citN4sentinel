using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using N4Sentinel.Domain;

namespace N4Sentinel.Infrastructure.Supervision;

/// <summary>
/// FR-057 : disponibilité, latence et requêtes lentes/bloquées d'un
/// composant de rôle BaseDeDonnees, « selon les accès autorisés » — le texte
/// du cahier des charges anticipe explicitement que cet accès puisse
/// manquer. Cette classe ne demande donc jamais un compte dédié : elle se
/// connecte sous l'identité du processus (déjà la voie recommandée pour les
/// connecteurs WinRM ailleurs dans l'application — un secret qui n'existe
/// pas ne peut pas fuir), et rapporte honnêtement l'absence d'accès plutôt
/// que d'échouer silencieusement ou de fabriquer un résultat.
/// </summary>
public sealed class DatabaseHealthService(ILogger<DatabaseHealthService> logger)
{
    public async Task<DatabaseHealthSnapshot> EvaluateAsync(N4Component composant, CancellationToken ct = default)
    {
        if (composant.Server is null)
            return DatabaseHealthSnapshot.Indisponible(composant.Id, composant.LogicalName,
                "Aucun serveur associé à ce composant.");

        // Le port n'est ajoute que s'il a ete explicitement declare : le
        // laisser absent permet au pilote de resoudre l'instance par defaut,
        // exactement comme partout ailleurs dans l'application ou aucune
        // chaine de connexion ne force de port SQL.
        var serveur = composant.Port is { } port
            ? $"{composant.Server.HostName},{port}"
            : composant.Server.HostName;
        var connectionString =
            $"Server={serveur};Trusted_Connection=True;"
            + "TrustServerCertificate=True;Connect Timeout=5;MultipleActiveResultSets=True";

        var chrono = Stopwatch.StartNew();
        try
        {
            await using var connexion = new SqlConnection(connectionString);
            await connexion.OpenAsync(ct);

            await using (var ping = connexion.CreateCommand())
            {
                ping.CommandText = "SELECT 1";
                await ping.ExecuteScalarAsync(ct);
            }
            chrono.Stop();

            var (requetes, droitsInsuffisants) = await RelireRequetesLentesAsync(connexion, ct);

            return new DatabaseHealthSnapshot
            {
                ComponentId = composant.Id,
                ComponentName = composant.LogicalName,
                Reachable = true,
                LatencyMs = chrono.Elapsed.TotalMilliseconds,
                SlowOrBlockedQueries = requetes,
                QueryVisibilityDenied = droitsInsuffisants
            };
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or TimeoutException)
        {
            logger.LogDebug(ex, "Contrôle base de données en échec pour {Composant}", composant.LogicalName);

            return DatabaseHealthSnapshot.Indisponible(composant.Id, composant.LogicalName,
                $"Connexion impossible sous l'identité du processus : {ex.Message} "
                + "Vérifiez que le compte de service dispose d'un accès à cette instance — ou déclarez-en un "
                + "dédié si l'architecture l'exige — pour que la latence et les requêtes lentes soient visibles.");
        }
    }

    /// <summary>
    /// Requêtes en cours dont l'attente ou la durée dépasse le seuil, ou qui
    /// bloquent une autre session. Sans VIEW SERVER STATE, SQL Server ne
    /// renvoie tout simplement aucune ligne pour les sessions d'autrui — ce
    /// n'est pas une erreur, mais l'écran doit le dire plutôt que de laisser
    /// croire à une base sans aucune requête lente.
    /// </summary>
    private static async Task<(List<SlowQueryInfo> Requetes, bool DroitsInsuffisants)> RelireRequetesLentesAsync(
        SqlConnection connexion, CancellationToken ct)
    {
        var resultat = new List<SlowQueryInfo>();

        await using var cmd = connexion.CreateCommand();
        cmd.CommandText = """
            SELECT TOP 20
                r.session_id,
                r.status,
                r.blocking_session_id,
                r.wait_type,
                r.total_elapsed_time,
                SUBSTRING(t.text, 1, 500) AS requete
            FROM sys.dm_exec_requests r
            CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) t
            WHERE r.session_id <> @@SPID
              AND (r.blocking_session_id <> 0 OR r.total_elapsed_time > 5000)
            ORDER BY r.total_elapsed_time DESC
            """;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            resultat.Add(new SlowQueryInfo(
                SessionId: reader.GetInt16(0),
                Status: reader.GetString(1),
                BlockingSessionId: reader.IsDBNull(2) || reader.GetInt16(2) == 0 ? null : reader.GetInt16(2),
                WaitType: reader.IsDBNull(3) ? null : reader.GetString(3),
                ElapsedMs: reader.GetInt32(4),
                QueryText: reader.IsDBNull(5) ? string.Empty : reader.GetString(5)));
        }

        // VIEW SERVER STATE absent : la vue ne remonte que la session
        // courante, jamais une erreur — sans ce controle, "aucune requete
        // lente" et "droits insuffisants" seraient indistinguables.
        var droitsInsuffisants = false;
        try
        {
            await using var verif = connexion.CreateCommand();
            verif.CommandText = "SELECT COUNT(*) FROM sys.dm_exec_requests WHERE session_id <> @@SPID";
            var autres = (int)(await verif.ExecuteScalarAsync(ct))!;
            if (autres == 0 && resultat.Count == 0) droitsInsuffisants = true;
        }
        catch (SqlException)
        {
            droitsInsuffisants = true;
        }

        return (resultat, droitsInsuffisants);
    }
}

public sealed record SlowQueryInfo(
    short SessionId, string Status, short? BlockingSessionId,
    string? WaitType, int ElapsedMs, string QueryText)
{
    public bool IsBlocked => BlockingSessionId is not null;
}

public sealed class DatabaseHealthSnapshot
{
    public Guid ComponentId { get; init; }
    public string ComponentName { get; init; } = string.Empty;
    public bool Reachable { get; init; }
    public double? LatencyMs { get; init; }
    public List<SlowQueryInfo> SlowOrBlockedQueries { get; init; } = [];

    /// <summary>
    /// Vrai si la connexion a réussi mais que les droits ne permettent pas
    /// de voir les requêtes des autres sessions (VIEW SERVER STATE absent) —
    /// distinct d'une base réellement sans requête lente.
    /// </summary>
    public bool QueryVisibilityDenied { get; init; }

    public string? Error { get; init; }
    public DateTimeOffset EvaluatedAt { get; init; } = DateTimeOffset.UtcNow;

    public static DatabaseHealthSnapshot Indisponible(Guid componentId, string nom, string erreur) => new()
    {
        ComponentId = componentId,
        ComponentName = nom,
        Reachable = false,
        Error = erreur
    };
}
