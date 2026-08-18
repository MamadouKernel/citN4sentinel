namespace N4Sentinel.Infrastructure.Identity;

/// <summary>
/// Historique des mots de passe d'un utilisateur (SEC-009).
/// </summary>
public class PasswordHistoryRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    
    public string UserId { get; set; } = string.Empty;
    public ApplicationUser User { get; set; } = default!;

    public string PasswordHash { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
