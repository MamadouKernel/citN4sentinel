using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Connectivity;
using N4Sentinel.Infrastructure.Connectors;
using N4Sentinel.Infrastructure.Persistence;
using N4Sentinel.Infrastructure.Referential;
using N4Sentinel.Infrastructure.Security;

namespace N4Sentinel.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Nom de la chaine de connexion, lue dans appsettings.json.
    ///
    /// A REPRENDRE AVANT LE DEPLOIEMENT EN UAT ET EN PRODUCTION : le cahier
    /// des charges demande que les secrets soient references dans un coffre et
    /// jamais integres au code (SEC-003), et que la configuration
    /// d'installation soit livree sans secret en clair. La configuration par
    /// defaut d'ASP.NET Core permet de surcharger cette valeur sans toucher au
    /// fichier, par variable d'environnement :
    ///     ConnectionStrings__N4Sentinel=...
    /// C'est le chemin a retenir sur les serveurs, ou un compte de service
    /// Windows qui supprime purement et simplement le mot de passe.
    /// </summary>
    public const string ConnectionStringName = "N4Sentinel";

    public static IServiceCollection AddN4SentinelInfrastructure(
        this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException(
                $"Chaine de connexion '{ConnectionStringName}' introuvable dans appsettings.json.");

        // Message explicite plutot qu'un echec de connexion SQL obscur : le
        // gabarit livre contient un mot de passe a remplacer.
        if (connectionString.Contains("MOT_DE_PASSE_A_DEFINIR", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Le mot de passe de la chaine de connexion est encore le gabarit livre. " +
                "Executez db/01_activer_connexion_sql.sql puis remplacez MOT_DE_PASSE_A_DEFINIR " +
                "dans appsettings.json. Tant que l'instance SQL Server est en mode " +
                "d'authentification Windows uniquement, utilisez la chaine de repli " +
                "'N4Sentinel_AuthWindows' fournie dans le meme fichier.");

        // Sans utilisateur connecte - taches de fond, migrations - l'acteur
        // est "systeme". La couche Web substitue une implementation adossee
        // au HttpContext.
        services.AddScoped<ICurrentUserContext, SystemUserContext>();
        services.AddScoped<AuditingInterceptor>();

        // Fabrique plutot que contexte injecte directement : dans Blazor Server,
        // un composant peut declencher plusieurs operations concurrentes sur le
        // meme circuit, ce qu'un DbContext partage ne supporte pas. Chaque ecran
        // ouvre son propre contexte, le temps de sa requete.
        // Fabrique enregistree a PORTEE DE REQUETE, et non en singleton comme
        // par defaut : l'intercepteur d'audit doit connaitre l'utilisateur
        // courant, ce qu'un service singleton ne peut pas resoudre.
        services.AddDbContextFactory<N4SentinelDbContext>((sp, options) =>
        {
            options.UseSqlServer(connectionString, sql =>
            {
                sql.EnableRetryOnFailure(
                    maxRetryCount: 3,
                    maxRetryDelay: TimeSpan.FromSeconds(5),
                    errorNumbersToAdd: null);
                sql.CommandTimeout(60);
            });
            options.AddInterceptors(sp.GetRequiredService<AuditingInterceptor>());
        }, lifetime: ServiceLifetime.Scoped);

        // Identity attend un contexte a portee de requete : on le derive de la
        // fabrique, de sorte qu'il n'existe qu'une seule configuration.
        services.AddScoped<N4SentinelDbContext>(sp =>
            sp.GetRequiredService<IDbContextFactory<N4SentinelDbContext>>().CreateDbContext());

        services.AddScoped<DatabaseSeeder>();
        services.AddScoped<ReferentialService>();
        services.AddScoped<ConnectivityTester>();
        services.AddScoped<NavisConfigImporter>();

        // Connecteur technique (sprint 2). Sans etat, donc partageable :
        // chaque appel ouvre et referme sa propre session.
        services.AddSingleton<IN4Connector, PowerShellConnector>();

        // Magasin de secrets et fabrique de cibles : tout acces a un serveur
        // passe par la fabrique, qui n'accepte que des serveurs declares au
        // referentiel.
        services.AddScoped<CredentialStore>();
        services.AddScoped<ConnectorTargetFactory>();
        services.AddScoped<ServerProbe>();
        services.AddScoped<ReadinessDiscovery>();

        return services;
    }
}
