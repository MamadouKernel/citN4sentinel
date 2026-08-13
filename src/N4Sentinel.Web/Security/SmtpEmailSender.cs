using System.Net;
using System.Net.Mail;
using Microsoft.AspNetCore.Identity;
using N4Sentinel.Infrastructure.Identity;

namespace N4Sentinel.Web.Security;

/// <summary>Parametres du serveur de messagerie, lus dans la configuration.</summary>
public sealed class SmtpOptions
{
    public const string SectionName = "N4Sentinel:Smtp";

    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 25;
    public bool UseSsl { get; set; }
    public string From { get; set; } = "n4sentinel@cit.ci";
    public string FromDisplayName { get; set; } = "N4 Sentinel";

    /// <summary>Vide pour un relais interne anonyme, ce qui est le cas courant en entreprise.</summary>
    public string? UserName { get; set; }
    public string? Password { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host);
}

/// <summary>
/// Envoi des courriels de gestion de compte : confirmation d'adresse et
/// reinitialisation de mot de passe.
///
/// LE SECOND FACTEUR NE PASSE PAS PAR ICI. N4 Sentinel utilise un code TOTP
/// genere par une application d'authentification (Microsoft Authenticator,
/// Google Authenticator ou equivalent). Ce choix vaut mieux qu'un code envoye
/// par courriel : le code est calcule hors ligne, il ne transite sur aucun
/// reseau, et la compromission d'une boite aux lettres ne suffit plus a
/// contourner le second facteur. Il fonctionne aussi lorsque le serveur de
/// messagerie est indisponible - ce qui, pour un outil d'exploitation appele
/// a servir pendant un incident, n'est pas un detail.
///
/// Si aucun serveur SMTP n'est configure, l'expediteur ne bloque pas
/// l'application : en developpement il journalise le contenu pour recuperer
/// le lien, ailleurs il se contente de signaler l'echec sans recopier le
/// message dans les journaux.
/// </summary>
public sealed class SmtpEmailSender(
    SmtpOptions options,
    ILogger<SmtpEmailSender> logger,
    IWebHostEnvironment environment) : IEmailSender<ApplicationUser>
{
    public Task SendConfirmationLinkAsync(ApplicationUser user, string email, string confirmationLink) =>
        SendAsync(email, "N4 Sentinel - confirmation de votre compte",
            $"""
             <p>Bonjour {WebUtility.HtmlEncode(user.DisplayName ?? user.UserName)},</p>
             <p>Un compte N4 Sentinel a ete cree a votre nom. Confirmez-le pour pouvoir vous connecter :</p>
             <p><a href="{confirmationLink}">Confirmer mon compte</a></p>
             <p>Si vous n'etes pas a l'origine de cette demande, signalez-le a la DSI : ce message
             signifie que quelqu'un a cree un compte avec votre adresse.</p>
             """);

    public Task SendPasswordResetLinkAsync(ApplicationUser user, string email, string resetLink) =>
        SendAsync(email, "N4 Sentinel - reinitialisation de votre mot de passe",
            $"""
             <p>Une reinitialisation de mot de passe a ete demandee pour votre compte N4 Sentinel.</p>
             <p><a href="{resetLink}">Definir un nouveau mot de passe</a></p>
             <p>Si vous n'en etes pas a l'origine, ignorez ce message : votre mot de passe reste inchange.
             Signalez-le a la DSI si cela se reproduit.</p>
             """);

    public Task SendPasswordResetCodeAsync(ApplicationUser user, string email, string resetCode) =>
        SendAsync(email, "N4 Sentinel - code de reinitialisation",
            $"""
             <p>Votre code de reinitialisation : <strong style="font-size:1.4em">{resetCode}</strong></p>
             <p>Il expire rapidement. Ne le transmettez a personne, y compris a un membre de la DSI :
             aucune equipe interne ne vous demandera ce code.</p>
             """);

    private async Task SendAsync(string to, string subject, string htmlBody)
    {
        if (!options.IsConfigured)
        {
            if (environment.IsDevelopment())
            {
                logger.LogWarning(
                    "Aucun serveur SMTP configure. Message NON envoye a {Destinataire} - contenu reproduit " +
                    "ci-dessous pour le developpement uniquement.\nObjet : {Objet}\n{Corps}",
                    to, subject, htmlBody);
            }
            else
            {
                // En dehors du developpement, on ne recopie jamais un code
                // d'authentification dans un journal.
                logger.LogError(
                    "Aucun serveur SMTP configure : le message '{Objet}' destine a {Destinataire} n'a pas ete envoye. " +
                    "Renseignez la section {Section} de la configuration.",
                    subject, to, SmtpOptions.SectionName);
            }
            return;
        }

        try
        {
            using var client = new SmtpClient(options.Host, options.Port) { EnableSsl = options.UseSsl };

            client.Credentials = string.IsNullOrWhiteSpace(options.UserName)
                ? CredentialCache.DefaultNetworkCredentials
                : new NetworkCredential(options.UserName, options.Password);

            using var message = new MailMessage
            {
                From = new MailAddress(options.From, options.FromDisplayName),
                Subject = subject,
                Body = htmlBody,
                IsBodyHtml = true
            };
            message.To.Add(to);

            await client.SendMailAsync(message);
            logger.LogInformation("Courriel '{Objet}' envoye a {Destinataire}.", subject, to);
        }
        catch (Exception ex)
        {
            // Un echec d'envoi ne doit pas faire tomber la page d'authentification :
            // l'utilisateur verra qu'il n'a rien recu, et le journal dit pourquoi.
            logger.LogError(ex, "Echec d'envoi du courriel '{Objet}' a {Destinataire}.", subject, to);
        }
    }
}
