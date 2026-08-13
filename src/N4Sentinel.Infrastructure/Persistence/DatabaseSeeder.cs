using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Identity;

namespace N4Sentinel.Infrastructure.Persistence;

/// <summary>
/// Amorce la base : applique les migrations en attente, cree les huit roles
/// du cahier des charges, et cree le premier administrateur de la solution.
///
/// Le premier compte est un cas particulier assume : sans lui, personne ne
/// peut se connecter pour creer les autres. Son mot de passe initial est lu
/// dans la configuration - donc dans les secrets utilisateur, jamais dans le
/// depot - et doit etre change a la premiere connexion.
/// </summary>
public sealed class DatabaseSeeder(
    N4SentinelDbContext db,
    RoleManager<IdentityRole> roleManager,
    UserManager<ApplicationUser> userManager,
    IConfiguration configuration,
    ILogger<DatabaseSeeder> logger)
{
    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        if (pending.Count > 0)
        {
            logger.LogInformation("Application de {Count} migration(s) : {Migrations}",
                pending.Count, string.Join(", ", pending));
            await db.Database.MigrateAsync(cancellationToken);
        }

        await SeedRolesAsync();
        await SeedFirstAdministratorAsync();
    }

    private async Task SeedRolesAsync()
    {
        foreach (var role in N4Roles.All)
        {
            if (await roleManager.RoleExistsAsync(role)) continue;

            var result = await roleManager.CreateAsync(new IdentityRole(role));
            if (result.Succeeded)
                logger.LogInformation("Role cree : {Role}", role);
            else
                logger.LogError("Echec de creation du role {Role} : {Erreurs}",
                    role, string.Join(" | ", result.Errors.Select(e => e.Description)));
        }
    }

    private async Task SeedFirstAdministratorAsync()
    {
        var email = configuration["N4Sentinel:FirstAdmin:Email"];
        var password = configuration["N4Sentinel:FirstAdmin:Password"];

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            // Silence volontaire en dehors du premier demarrage : ce n'est pas
            // une anomalie de ne pas vouloir recreer un compte d'amorcage.
            logger.LogDebug("Aucun administrateur d'amorcage configure - etape ignoree.");
            return;
        }

        if (await userManager.FindByEmailAsync(email) is not null)
        {
            logger.LogDebug("L'administrateur d'amorcage existe deja - aucune action.");
            return;
        }

        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            // Confirme d'office : il n'y a pas encore de service d'envoi de
            // courriel au premier demarrage, et sans confirmation le compte
            // ne pourrait pas se connecter.
            EmailConfirmed = true,
            DisplayName = "Administrateur de la solution",
            Department = "DSI - Solutions IT et Projets"
        };

        var created = await userManager.CreateAsync(user, password);
        if (!created.Succeeded)
        {
            logger.LogError("Echec de creation de l'administrateur d'amorcage : {Erreurs}",
                string.Join(" | ", created.Errors.Select(e => e.Description)));
            return;
        }

        await userManager.AddToRolesAsync(user, [N4Roles.AdministrateurSolution, N4Roles.Auditeur]);

        logger.LogWarning(
            "Administrateur d'amorcage cree pour {Email}. Changez son mot de passe des la premiere connexion, " +
            "puis retirez la section N4Sentinel:FirstAdmin de la configuration.", email);
    }
}
