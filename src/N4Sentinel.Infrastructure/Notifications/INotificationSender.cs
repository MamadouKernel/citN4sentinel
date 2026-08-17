namespace N4Sentinel.Infrastructure.Notifications;

/// <summary>
/// Canal de sortie des notifications (FR-095). Abstraction volontairement
/// minimale : envoyer un courriel HTML à une adresse. L'implémentation réelle
/// (SmtpEmailSender, côté Web) est la même que celle déjà utilisée pour les
/// courriels de compte — pas un second canal à maintenir.
/// </summary>
public interface INotificationSender
{
    Task SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default);
}
