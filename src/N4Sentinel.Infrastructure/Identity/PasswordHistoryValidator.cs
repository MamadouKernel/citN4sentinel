using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Identity;

/// <summary>
/// Valide qu'un nouveau mot de passe n'a pas été utilisé récemment (SEC-009).
/// </summary>
public class PasswordHistoryValidator(IDbContextFactory<N4SentinelDbContext> dbFactory) : IPasswordValidator<ApplicationUser>
{
    // Nombre de mots de passe à conserver dans l'historique (SEC-009)
    private const int PasswordHistoryLimit = 5;

    public async Task<IdentityResult> ValidateAsync(UserManager<ApplicationUser> manager, ApplicationUser user, string? password)
    {
        if (string.IsNullOrEmpty(password))
            return IdentityResult.Success;

        await using var db = await dbFactory.CreateDbContextAsync();
        
        var recentPasswords = await db.PasswordHistoryRecords
            .AsNoTracking()
            .Where(h => h.UserId == user.Id)
            .OrderByDescending(h => h.CreatedAt)
            .Take(PasswordHistoryLimit)
            .Select(h => h.PasswordHash)
            .ToListAsync();

        foreach (var hash in recentPasswords)
        {
            var verificationResult = manager.PasswordHasher.VerifyHashedPassword(user, hash, password);
            if (verificationResult == PasswordVerificationResult.Success || verificationResult == PasswordVerificationResult.SuccessRehashNeeded)
            {
                return IdentityResult.Failed(new IdentityError
                {
                    Code = "PasswordHistoryViolation",
                    Description = $"Le mot de passe ne peut pas être identique à l'un des {PasswordHistoryLimit} derniers mots de passe utilisés."
                });
            }
        }

        return IdentityResult.Success;
    }
}
