using Xunit;
using Microsoft.EntityFrameworkCore;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Tests;

/// <summary>
/// Hébergement sur SQLite — un fichier, aucun serveur à installer.
///
/// Ce mode existe pour déployer sur une VM sans y installer SQL Server. Ce
/// n'est PAS un mode dégradé, et ces tests le prouvent sur les trois points
/// où l'on pourrait le croire :
///
///   — les migrations s'appliquent réellement sur un fichier neuf ;
///   — les index uniques sont tenus PAR LA BASE (le verrou d'environnement
///     en dépend : ORC-04 exige que l'unicité ne repose pas sur le code) ;
///   — la concurrence optimiste détecte bien deux écritures rivales, alors
///     que SQLite n'a pas de type rowversion.
/// </summary>
public sealed class SqliteTests : IDisposable
{
    private readonly string _fichier;
    private readonly DbContextOptions<N4SentinelDbContext> _options;

    public SqliteTests()
    {
        _fichier = Path.Combine(Path.GetTempPath(), $"n4-sqlite-{Guid.NewGuid():N}.db");

        _options = new DbContextOptionsBuilder<N4SentinelDbContext>()
            .UseSqlite($"Data Source={_fichier}",
                s => s.MigrationsAssembly("N4Sentinel.Migrations.Sqlite"))
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
            .Options;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(_fichier)) File.Delete(_fichier); } catch { /* verrou résiduel */ }
    }

    private N4SentinelDbContext Contexte() => new(_options);

    [Fact(DisplayName = "Les migrations s'appliquent sur un fichier neuf")]
    public async Task Les_Migrations_S_Appliquent()
    {
        await using var db = Contexte();
        await db.Database.MigrateAsync();

        Assert.True(File.Exists(_fichier), "Le fichier de base n'a pas été créé.");

        // Un échantillon des tables qui portent le produit : si le jeu de
        // migrations était celui de SQL Server, rien de tout cela n'existerait.
        Assert.Equal(0, await db.Environments.CountAsync());
        Assert.Equal(0, await db.Executions.CountAsync());
        Assert.Equal(0, await db.AuditEntries.CountAsync());
        Assert.Equal(0, await db.Sessions.CountAsync());
    }

    [Fact(DisplayName = "Le référentiel s'écrit et se relit")]
    public async Task Le_Referentiel_S_Ecrit_Et_Se_Relit()
    {
        await using (var db = Contexte())
        {
            await db.Database.MigrateAsync();

            var env = new N4Environment
            {
                Code = "UAT",
                Name = "Recette",
                Kind = EnvironmentKind.UAT,
                Status = LifecycleStatus.Actif,
                TimeZoneId = "Africa/Abidjan"
            };
            db.Environments.Add(env);
            await db.SaveChangesAsync();

            db.Servers.Add(new N4Server
            {
                EnvironmentId = env.Id,
                HostName = "SRV-UAT-01",
                Status = LifecycleStatus.Valide
            });
            await db.SaveChangesAsync();
        }

        await using var relecture = Contexte();

        var lu = await relecture.Environments.Include(e => e.Servers).SingleAsync();
        Assert.Equal("UAT", lu.Code);
        Assert.Equal("Africa/Abidjan", lu.TimeZoneId);
        Assert.Single(lu.Servers);
    }

    [Fact(DisplayName = "L'unicité du verrou d'environnement est tenue par la base")]
    public async Task L_Unicite_Du_Verrou_Est_Tenue_Par_La_Base()
    {
        await using var db = Contexte();
        await db.Database.MigrateAsync();

        // L'environnement doit exister : SQLite applique les clés étrangères,
        // et un identifiant inventé ferait échouer l'insertion pour une tout
        // autre raison que celle qu'on veut éprouver.
        var env = new N4Environment
        {
            Code = "PRD",
            Name = "Production",
            Kind = EnvironmentKind.Production,
            Status = LifecycleStatus.Actif
        };
        db.Environments.Add(env);
        await db.SaveChangesAsync();

        var envId = env.Id;

        db.EnvironmentLocks.Add(new EnvironmentLock
        {
            EnvironmentId = envId,
            ExecutionId = Guid.NewGuid(),
            HeldBy = "premier",
            Reason = "Arrêt complet",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10)
        });
        await db.SaveChangesAsync();

        db.EnvironmentLocks.Add(new EnvironmentLock
        {
            EnvironmentId = envId,
            ExecutionId = Guid.NewGuid(),
            HeldBy = "second",
            Reason = "Deuxième opération sur le même environnement",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10)
        });

        // ORC-04 : « une seule opération mutative par environnement, unicité
        // garantie par la base et non par le code ». C'est le point qu'un
        // fichier JSON n'aurait pas pu tenir.
        await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact(DisplayName = "Deux écritures concurrentes : la seconde est refusée")]
    public async Task La_Concurrence_Optimiste_Fonctionne_Sans_RowVersion_Natif()
    {
        Guid id;

        await using (var amorce = Contexte())
        {
            await amorce.Database.MigrateAsync();

            var env = new N4Environment
            {
                Code = "PRD",
                Name = "Production",
                Kind = EnvironmentKind.Production,
                Status = LifecycleStatus.Actif
            };
            amorce.Environments.Add(env);
            await amorce.SaveChangesAsync();
            id = env.Id;
        }

        // Deux contextes lisent la même ligne, puis écrivent tour à tour.
        await using var premier = Contexte();
        await using var second = Contexte();

        var vuParLePremier = await premier.Environments.SingleAsync(e => e.Id == id);
        var vuParLeSecond = await second.Environments.SingleAsync(e => e.Id == id);

        vuParLePremier.Name = "Production — renommée par le premier";
        await premier.SaveChangesAsync();

        vuParLeSecond.Name = "Production — renommée par le second";

        // SQLite n'a pas de rowversion : le jeton est estampillé par
        // l'application. La garantie doit être la même — la seconde écriture,
        // fondée sur une lecture périmée, doit échouer et non écraser.
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());

        await using var verification = Contexte();
        var final = await verification.Environments.SingleAsync(e => e.Id == id);
        Assert.Equal("Production — renommée par le premier", final.Name);
    }

    [Fact(DisplayName = "La sauvegarde produit une base complète, cohérente et relisible")]
    public async Task La_Sauvegarde_Produit_Une_Base_Relisible()
    {
        await using (var db = Contexte())
        {
            await db.Database.MigrateAsync();

            db.Environments.Add(new N4Environment
            {
                Code = "PRD",
                Name = "Production",
                Kind = EnvironmentKind.Production,
                Status = LifecycleStatus.Actif
            });
            await db.SaveChangesAsync();
        }

        var copie = Path.Combine(Path.GetTempPath(), $"n4-sauvegarde-{Guid.NewGuid():N}.db");

        try
        {
            await using (var db = Contexte())
            {
                // Ce que fait BackupService en mode SQLite. Copier le fichier
                // ne suffirait pas : une base en service a des écritures en
                // cours et un journal non replié.
                await db.Database.ExecuteSqlRawAsync($"VACUUM INTO '{copie.Replace("'", "''")}';");
            }

            Assert.True(File.Exists(copie), "La sauvegarde n'a pas été produite.");

            // Le contrôle d'intégrité, comme le fait VerifierBaseAsync.
            var chaineLecture = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = copie,
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly
            }.ToString();

            await using (var connexion = new Microsoft.Data.Sqlite.SqliteConnection(chaineLecture))
            {
                await connexion.OpenAsync();
                var commande = connexion.CreateCommand();
                commande.CommandText = "PRAGMA integrity_check;";
                Assert.Equal("ok", (await commande.ExecuteScalarAsync())?.ToString());
            }

            // Et surtout : la sauvegarde contient bien les DONNÉES. Un fichier
            // qui passe le contrôle d'intégrité mais serait vide se
            // restaurerait sans erreur, et sans rien dedans.
            var optionsCopie = new DbContextOptionsBuilder<N4SentinelDbContext>()
                .UseSqlite($"Data Source={copie}")
                .ConfigureWarnings(w => w.Ignore(
                    Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
                .Options;

            await using var relue = new N4SentinelDbContext(optionsCopie);
            var env = await relue.Environments.SingleAsync();
            Assert.Equal("PRD", env.Code);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (File.Exists(copie)) File.Delete(copie); } catch { /* verrou résiduel */ }
        }
    }

    // =======================================================================
    // Tri par date — le défaut qui a fait tomber le premier déploiement
    // =======================================================================
    //
    // SQLite stocke les DateTimeOffset en TEXTE et refuse tout ORDER BY
    // dessus : « SQLite does not support expressions of type 'DateTimeOffset'
    // in ORDER BY clauses ». L'exception survient À L'EXÉCUTION, tue le
    // circuit Blazor, et l'écran devient blanc.
    //
    // Mes premiers tests SQLite ne triaient rien : ils sont passés au vert sur
    // un support incomplet, et le défaut a été découvert sur la VM du client.
    // L'application trie par date presque partout — historique, audit,
    // alertes, exécutions.

    [Fact(DisplayName = "Trier par date fonctionne, dans les deux sens")]
    public async Task Le_Tri_Par_Date_Fonctionne()
    {
        await using var db = Contexte();
        await db.Database.MigrateAsync();

        var reference = DateTimeOffset.UtcNow;

        for (var i = 0; i < 5; i++)
        {
            db.AuditEntries.Add(new AuditEntry
            {
                OccurredAt = reference.AddMinutes(-i),
                Action = AuditAction.Connexion,
                Outcome = AuditOutcome.Succes,
                Actor = $"acteur-{i}",
                EntityType = "Essai"
            });
        }
        await db.SaveChangesAsync();

        // C'est cette requête qui levait l'exception sur la VM.
        var recentes = await db.AuditEntries
            .OrderByDescending(a => a.OccurredAt)
            .Take(3)
            .ToListAsync();

        Assert.Equal(3, recentes.Count);
        Assert.Equal("acteur-0", recentes[0].Actor);
        Assert.Equal("acteur-2", recentes[2].Actor);

        var anciennes = await db.AuditEntries.OrderBy(a => a.OccurredAt).ToListAsync();
        Assert.Equal("acteur-4", anciennes[0].Actor);
    }

    [Fact(DisplayName = "Filtrer sur une plage de dates fonctionne")]
    public async Task Le_Filtre_Par_Plage_De_Dates_Fonctionne()
    {
        await using var db = Contexte();
        await db.Database.MigrateAsync();

        var maintenant = DateTimeOffset.UtcNow;

        db.AuditEntries.Add(new AuditEntry
        {
            OccurredAt = maintenant.AddDays(-10),
            Action = AuditAction.Connexion, Outcome = AuditOutcome.Succes,
            Actor = "ancien", EntityType = "Essai"
        });
        db.AuditEntries.Add(new AuditEntry
        {
            OccurredAt = maintenant.AddHours(-1),
            Action = AuditAction.Connexion, Outcome = AuditOutcome.Succes,
            Actor = "recent", EntityType = "Essai"
        });
        await db.SaveChangesAsync();

        // Les rapports et l'historique filtrent tous sur une fenêtre.
        var depuis = maintenant.AddDays(-1);
        var dansLaFenetre = await db.AuditEntries
            .Where(a => a.OccurredAt >= depuis)
            .ToListAsync();

        Assert.Single(dansLaFenetre);
        Assert.Equal("recent", dansLaFenetre[0].Actor);
    }

    [Fact(DisplayName = "L'instant relu est bien celui qui a été écrit")]
    public async Task L_Instant_Relu_Est_Celui_Ecrit()
    {
        // La conversion en tics UTC ne doit pas décaler les horodatages : un
        // audit décalé d'une heure vaudrait mieux ne pas exister.
        var instant = new DateTimeOffset(2026, 8, 21, 11, 14, 17, TimeSpan.Zero);

        await using (var db = Contexte())
        {
            await db.Database.MigrateAsync();
            db.AuditEntries.Add(new AuditEntry
            {
                OccurredAt = instant,
                Action = AuditAction.Connexion, Outcome = AuditOutcome.Succes,
                Actor = "essai", EntityType = "Essai"
            });
            await db.SaveChangesAsync();
        }

        await using var relecture = Contexte();
        var lu = await relecture.AuditEntries.SingleAsync();

        Assert.Equal(instant.UtcDateTime, lu.OccurredAt.UtcDateTime);
    }

    [Fact(DisplayName = "Le jeton de concurrence change à chaque écriture")]
    public async Task Le_Jeton_Change_A_Chaque_Ecriture()
    {
        await using var db = Contexte();
        await db.Database.MigrateAsync();

        var env = new N4Environment
        {
            Code = "TEST",
            Name = "Essai",
            Kind = EnvironmentKind.Test,
            Status = LifecycleStatus.Brouillon
        };
        db.Environments.Add(env);
        await db.SaveChangesAsync();

        var apresCreation = env.RowVersion;
        Assert.NotNull(apresCreation);

        env.Name = "Essai modifié";
        await db.SaveChangesAsync();

        // Un jeton figé annulerait la détection sans que rien ne le signale.
        Assert.NotEqual(apresCreation, env.RowVersion);
    }
}
