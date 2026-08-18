using Microsoft.Data.SqlClient;

namespace N4Sentinel.Tests;

/// <summary>
/// Centralise la résolution de la chaîne de connexion SQL Server pour les tests
/// d'intégration (Phase I.3 — remédiation audit CIT-CIV-DSI-RFP-0010).
///
/// PRIORITÉ DE RÉSOLUTION :
///   1. Variable d'environnement N4SENTINEL_TEST_DB
///   2. Fallback vers localhost (Server=localhost;Database=master;...)
///
/// Si la chaîne résolue ne pointe pas vers un SQL Server joignable,
/// les tests doivent appeler <see cref="SkipIfUnavailable"/> pour
/// s'ignorer proprement au lieu de lever une exception de connexion qui
/// masquerait les vrais échecs de test.
///
/// CONVENTION :
///   - Les tests qui EXIGENT SQL Server → [SkippableFact] + appeler
///     SkipIfUnavailable() en début de test.
///   - Les tests purement unitaires (aucune BDD) → [Fact] standard.
/// </summary>
internal static class TestConnectionHelper
{
    /// <summary>Nom de la variable d'environnement contenant la chaîne de connexion SQL Server de test.</summary>
    public const string EnvVarName = "N4SENTINEL_TEST_DB";

    /// <summary>
    /// Chaîne de connexion vers la base <c>master</c> pour CREATE / DROP DATABASE.
    /// Lue depuis <see cref="EnvVarName"/>, ou fallback vers localhost.
    /// </summary>
    public static string MasterConnectionString
    {
        get
        {
            var env = Environment.GetEnvironmentVariable(EnvVarName);
            if (!string.IsNullOrWhiteSpace(env))
            {
                // Remplacer le nom de base par "master" pour pouvoir créer des bases
                var builder = new SqlConnectionStringBuilder(env)
                {
                    InitialCatalog = "master"
                };
                return builder.ConnectionString;
            }

            // Fallback développement local
            return "Server=localhost;Database=master;Trusted_Connection=True;TrustServerCertificate=True";
        }
    }

    /// <summary>
    /// Construit la chaîne de connexion pour une base de test isolée.
    /// </summary>
    public static string BuildDatabaseConnectionString(string databaseName)
    {
        var env = Environment.GetEnvironmentVariable(EnvVarName);
        if (!string.IsNullOrWhiteSpace(env))
        {
            var builder = new SqlConnectionStringBuilder(env)
            {
                InitialCatalog = databaseName,
                MultipleActiveResultSets = true
            };
            return builder.ConnectionString;
        }

        return $"Server=localhost;Database={databaseName};Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True";
    }

    /// <summary>
    /// Appeler en début de tout test [SkippableFact] qui nécessite SQL Server.
    /// Si le serveur n'est pas joignable, le test sera marqué Skip au lieu
    /// d'échouer avec une erreur de connexion.
    ///
    /// IMPORTANT : un Skip ne masque PAS une erreur de logique métier —
    /// il masque uniquement l'absence d'infrastructure SQL Server.
    /// En CI/CD, s'assurer que N4SENTINEL_TEST_DB est défini pour éviter
    /// que tous les tests SQL soient silencieusement ignorés.
    /// </summary>
    public static void SkipIfUnavailable()
    {
        try
        {
            using var conn = new SqlConnection(MasterConnectionString);
            conn.Open();
        }
        catch (Exception ex)
        {
            throw new SkipException(
                $"SQL Server non disponible à l'adresse configurée. " +
                $"Définir la variable d'environnement '{EnvVarName}' avec une chaîne de connexion valide, " +
                $"ou s'assurer que SQL Server est démarré localement. " +
                $"Détail : {ex.Message}");
        }
    }
}
