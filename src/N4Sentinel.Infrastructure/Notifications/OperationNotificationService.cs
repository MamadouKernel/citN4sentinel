using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Identity;

namespace N4Sentinel.Infrastructure.Notifications;

/// <summary>
/// FR-095 : notifie les acteurs concernés au lancement, en cas de blocage et
/// à la fin d'une opération — demandeur et détenteurs du rôle Validateur
/// (PeutApprouver), les deux profils pour qui une opération en cours change
/// réellement quelque chose.
///
/// UN ÉCHEC D'ENVOI NE FAIT JAMAIS ÉCHOUER L'ORCHESTRATION. Chaque appelant
/// avale l'exception ici, jamais dans le moteur : le même principe que
/// <c>SmtpEmailSender</c> pour les courriels de compte.
/// </summary>
public sealed class OperationNotificationService(
    INotificationSender sender, UserManager<ApplicationUser> userManager, ILogger<OperationNotificationService> logger)
{
    public Task NotifierLancementAsync(WorkflowExecution x, CancellationToken ct = default) =>
        EnvoyerAsync(x, $"N4 Sentinel — Lancement : {x.WorkflowName} ({x.EnvironmentCode})",
            $"""
             <p>L'opération <strong>{Encoder(x.WorkflowName)}</strong> (v{x.WorkflowVersion}) vient d'être lancée
             sur <strong>{Encoder(x.EnvironmentCode)}</strong> par {Encoder(x.RequestedBy)}.</p>
             <p>Motif : {Encoder(x.Reason)}</p>
             <p>Corrélation : <code>{x.CorrelationId}</code></p>
             """, ct);

    public Task NotifierBlocageAsync(WorkflowExecution x, string raison, CancellationToken ct = default) =>
        EnvoyerAsync(x, $"N4 Sentinel — BLOCAGE, décision attendue : {x.WorkflowName} ({x.EnvironmentCode})",
            $"""
             <p><strong style="color:#b91c1c">L'opération est suspendue et attend une décision de l'opérateur.</strong></p>
             <p>{Encoder(raison)}</p>
             <p>Environnement : {Encoder(x.EnvironmentCode)} · Corrélation : <code>{x.CorrelationId}</code></p>
             """, ct);

    public Task NotifierFinAsync(WorkflowExecution x, CancellationToken ct = default) =>
        EnvoyerAsync(x, $"N4 Sentinel — {LibelleStatut(x.Status)} : {x.WorkflowName} ({x.EnvironmentCode})",
            $"""
             <p>L'opération <strong>{Encoder(x.WorkflowName)}</strong> sur <strong>{Encoder(x.EnvironmentCode)}</strong>
             s'est terminée : <strong>{LibelleStatut(x.Status)}</strong>.</p>
             <p>{Encoder(x.Outcome)}</p>
             <p>Corrélation : <code>{x.CorrelationId}</code></p>
             """, ct);

    private async Task EnvoyerAsync(WorkflowExecution x, string sujet, string corpsHtml, CancellationToken ct)
    {
        // Une simulation (FR-005) n'a rien produit sur l'ecosysteme : alerter
        // un Validateur pour un lancement ou un blocage qui n'a jamais eu lieu
        // reellement serait plus trompeur qu'utile.
        if (x.IsSimulation) return;

        try
        {
            var destinataires = await DestinatairesAsync(x, ct);
            foreach (var email in destinataires)
                await sender.SendAsync(email, sujet, corpsHtml, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Échec d'envoi de la notification '{Sujet}' pour l'exécution {Correlation}.", sujet, x.CorrelationId);
        }
    }

    private async Task<List<string>> DestinatairesAsync(WorkflowExecution x, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var emails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var demandeur = await userManager.FindByEmailAsync(x.RequestedBy);
        if (demandeur is { IsDisabled: false } && demandeur.Email is { Length: > 0 })
            emails.Add(demandeur.Email);

        foreach (var validateur in await userManager.GetUsersInRoleAsync(N4Roles.Validateur))
            if (!validateur.IsDisabled && validateur.Email is { Length: > 0 })
                emails.Add(validateur.Email);

        return [.. emails];
    }

    private static string Encoder(string? texte) => System.Net.WebUtility.HtmlEncode(texte ?? "—");

    private static string LibelleStatut(ExecutionStatus s) => s switch
    {
        ExecutionStatus.TermineSucces => "Réussie",
        ExecutionStatus.TermineAvecAvertissements => "Terminée avec réserves",
        ExecutionStatus.Echec => "En échec",
        ExecutionStatus.Annule => "Annulée",
        ExecutionStatus.ReconciliationRequise => "Réconciliation requise",
        _ => s.ToString()
    };
}
