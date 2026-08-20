using Xunit;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using N4Sentinel.Web.Security;

namespace N4Sentinel.Tests;

/// <summary>
/// §3.19 — réponse détaillée de la sonde de santé.
///
/// Ce point d'entrée n'est lisible qu'authentifié : le contrôler dans un
/// navigateur supposerait de saisir un mot de passe. La mise en forme a donc
/// été extraite en fonction pure, précisément pour être vérifiable ici.
/// </summary>
public sealed class SondeSanteTests
{
    private static HealthReport Rapport(params (string Nom, HealthStatus Etat, string? Motif)[] controles)
    {
        var entrees = controles.ToDictionary(
            c => c.Nom,
            c => new HealthReportEntry(
                c.Etat, c.Motif, TimeSpan.FromMilliseconds(5), exception: null, data: null));

        var pire = controles.Length == 0
            ? HealthStatus.Healthy
            : controles.Min(c => c.Etat);

        return new HealthReport(entrees, pire, TimeSpan.FromMilliseconds(10));
    }

    [Fact(DisplayName = "La réponse nomme chaque contrôle et son état")]
    public void Chaque_Controle_Est_Nomme()
    {
        var texte = HealthReportFormatter.Formater(
            Rapport(("database", HealthStatus.Healthy, null)));

        Assert.Contains("Statut global : Healthy", texte);
        Assert.Contains("database : Healthy", texte);
    }

    [Fact(DisplayName = "Un contrôle en échec expose son motif")]
    public void Un_Echec_Expose_Son_Motif()
    {
        var texte = HealthReportFormatter.Formater(
            Rapport(("database", HealthStatus.Unhealthy, "Connexion refusée par le serveur SQL")));

        Assert.Contains("database : Unhealthy", texte);
        Assert.Contains("Connexion refusée par le serveur SQL", texte);
        Assert.Contains("Statut global : Unhealthy", texte);
    }

    [Fact(DisplayName = "La réponse dit toujours ce qu'elle ne couvre PAS")]
    public void La_Portee_Est_Toujours_Enoncee()
    {
        // Le point qui compte : sans cette phrase, « Healthy » se lit comme
        // « tout va bien », alors que l'écosystème N4 supervisé peut être à
        // l'arrêt complet pendant que cette page répond Healthy.
        var sain = HealthReportFormatter.Formater(Rapport(("database", HealthStatus.Healthy, null)));
        var malade = HealthReportFormatter.Formater(Rapport(("database", HealthStatus.Unhealthy, "KO")));

        foreach (var texte in new[] { sain, malade })
        {
            Assert.Contains("PAS sur l'état de l'écosystème N4 supervisé", texte);
            Assert.Contains("Supervision", texte);
        }
    }
}
