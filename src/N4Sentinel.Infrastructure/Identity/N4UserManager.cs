using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Identity;

/// <summary>
/// Redéfinition du UserManager standard pour intercepter les changements de mots de passe
/// et alimenter l'historique (SEC-009).
/// </summary>
public sealed class N4UserManager(
    IUserStore<ApplicationUser> store,
    IOptions<IdentityOptions> optionsAccessor,
    IPasswordHasher<ApplicationUser> passwordHasher,
    IEnumerable<IUserValidator<ApplicationUser>> userValidators,
    IEnumerable<IPasswordValidator<ApplicationUser>> passwordValidators,
    ILookupNormalizer keyNormalizer,
    IdentityErrorDescriber errors,
    IServiceProvider services,
    ILogger<UserManager<ApplicationUser>> logger,
    IDbContextFactory<N4SentinelDbContext> dbFactory)
    : UserManager<ApplicationUser>(store, optionsAccessor, passwordHasher, userValidators, passwordValidators, keyNormalizer, errors, services, logger)
{
    protected override async Task<IdentityResult> UpdatePasswordHash(ApplicationUser user, string newPassword, bool validatePassword)
    {
        var result = await base.UpdatePasswordHash(user, newPassword, validatePassword);
        if (result.Succeeded && !string.IsNullOrEmpty(user.PasswordHash))
        {
            user.PasswordChangedAt = DateTimeOffset.UtcNow;
            // Expiration par défaut dans 90 jours
            user.PasswordExpiresAt = user.PasswordChangedAt.Value.AddDays(90);

            await using var db = await dbFactory.CreateDbContextAsync();
            db.PasswordHistoryRecords.Add(new PasswordHistoryRecord
            {
                UserId = user.Id,
                PasswordHash = user.PasswordHash,
                CreatedAt = user.PasswordChangedAt.Value
            });
            await db.SaveChangesAsync();
        }
        return result;
    }
}
