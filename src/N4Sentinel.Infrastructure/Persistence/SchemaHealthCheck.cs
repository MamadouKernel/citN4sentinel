using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace N4Sentinel.Infrastructure.Persistence;

/// <summary>
/// Contrôle de santé du SCHÉMA, et non de la simple connexion.
///
/// POURQUOI IL REMPLACE `AddDbContextCheck`. Celui-ci se contente d'un
/// <c>CanConnect</c>. Sur SQL Server, atteindre la base suppose déjà qu'elle
/// existe et qu'on y a accès — c'est une preuve acceptable. Sur SQLite, ouvrir
/// un fichier réussit TOUJOURS : un fichier vide, un fichier tronqué, un
/// fichier créé à l'instant par une faute de frappe dans le chemin répondent
/// tous « connexion réussie ».
///
/// L'application aurait donc annoncé « Healthy » avec une base sans la moindre
/// table, et le script de mise en service aurait déclaré le déploiement
/// réussi. C'est exactement la confusion que ce produit combat ailleurs :
/// prendre un signal faible pour une preuve.
///
/// Ce contrôle demande deux choses de plus, vraies pour les deux fournisseurs :
///   — la base répond ;
///   — AUCUNE migration n'est en attente, donc le schéma est à jour.
/// </summary>
public sealed class SchemaHealthCheck(IDbContextFactory<N4SentinelDbContext> dbFactory)
    : IHealthCheck
{
    public const string Nom = "base-de-donnees";

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

            if (!await db.Database.CanConnectAsync(cancellationToken))
                return HealthCheckResult.Unhealthy(
                    "La base de données ne répond pas.");

            var enAttente = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();

            if (enAttente.Count > 0)
                return HealthCheckResult.Unhealthy(
                    $"{enAttente.Count} migration(s) en attente : le schéma n'est pas à jour. "
                    + $"Première : {enAttente[0]}. L'application ne doit pas être mise en service "
                    + "dans cet état — redémarrez-la pour que l'amorçage les applique.");

            return HealthCheckResult.Healthy("Base joignable, schéma à jour.");
        }
        catch (Exception ex)
        {
            // Le motif est remonté tel quel : sur /health/detail il est le seul
            // élément qui dise quoi corriger.
            return HealthCheckResult.Unhealthy(
                $"Contrôle de la base impossible : {ex.Message}", ex);
        }
    }
}
