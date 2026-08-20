using System.Text;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace N4Sentinel.Web.Security;

/// <summary>
/// Mise en forme de la réponse détaillée de <c>/health/detail</c>.
///
/// Extraite du pipeline pour être VÉRIFIABLE : le contenu de ce point d'entrée
/// n'est lisible qu'authentifié, et le contrôler dans un navigateur suppose de
/// saisir un mot de passe. Une fonction pure se teste sans rien de tout cela.
/// </summary>
public static class HealthReportFormatter
{
    /// <summary>
    /// Ce que le contrôle NE couvre PAS doit être dit aussi bien que ce qu'il
    /// couvre. Sans cette phrase, « Healthy » se lit comme « tout va bien »,
    /// alors qu'il ne parle que de N4 Sentinel — jamais de l'écosystème N4
    /// supervisé, qui peut être à l'arrêt complet pendant que cette page
    /// répond Healthy.
    /// </summary>
    public const string Portee =
        "Ce contrôle porte sur N4 Sentinel lui-même (base, application),\n"
        + "PAS sur l'état de l'écosystème N4 supervisé. Voir l'écran Supervision.";

    public static string Formater(HealthReport rapport)
    {
        var sb = new StringBuilder();
        sb.Append("Statut global : ").Append(rapport.Status).Append('\n');

        foreach (var (nom, controle) in rapport.Entries)
        {
            sb.Append(nom).Append(" : ").Append(controle.Status);

            if (!string.IsNullOrWhiteSpace(controle.Description))
                sb.Append(" — ").Append(controle.Description);

            if (controle.Exception is not null)
                sb.Append(" — ").Append(controle.Exception.Message);

            sb.Append('\n');
        }

        sb.Append('\n').Append(Portee);
        return sb.ToString();
    }
}
