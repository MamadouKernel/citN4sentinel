using Microsoft.Extensions.Logging.Abstractions;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Supervision;

namespace N4Sentinel.Tests;

/// <summary>
/// FR-057 : disponibilité, latence et requêtes lentes/bloquées d'une base.
///
/// Contre le VRAI SQL Server local utilisé par toute la suite — c'est le
/// seul moyen de prouver que la connexion sous l'identité du processus
/// fonctionne réellement, pas seulement en théorie.
/// </summary>
public sealed class DatabaseHealthTests
{
    private readonly DatabaseHealthService _service = new(NullLogger<DatabaseHealthService>.Instance);

    [Fact(DisplayName = "Une base joignable sous l'identite du processus rapporte une latence reelle")]
    public async Task Base_Joignable_Rapporte_Une_Latence()
    {
        var composant = new N4Component
        {
            LogicalName = "Database Host",
            Role = ComponentRole.BaseDeDonnees,
            Server = new N4Server { HostName = "localhost" }
        };

        var resultat = await _service.EvaluateAsync(composant);

        Assert.True(resultat.Reachable, resultat.Error);
        Assert.NotNull(resultat.LatencyMs);
        Assert.True(resultat.LatencyMs >= 0);
        Assert.Null(resultat.Error);
    }

    [Fact(DisplayName = "Une base injoignable rapporte honnetement l'echec, sans lever d'exception")]
    public async Task Base_Injoignable_Rapporte_L_Echec()
    {
        var composant = new N4Component
        {
            LogicalName = "Database Host UAT",
            Role = ComponentRole.BaseDeDonnees,
            Server = new N4Server { HostName = "hote-inexistant-n4sentinel-test.invalid" }
        };

        var resultat = await _service.EvaluateAsync(composant);

        Assert.False(resultat.Reachable);
        Assert.Null(resultat.LatencyMs);
        Assert.False(string.IsNullOrWhiteSpace(resultat.Error));
    }

    [Fact(DisplayName = "Un composant sans serveur associe est dit indisponible, pas suppose disponible")]
    public async Task Composant_Sans_Serveur_Est_Indisponible()
    {
        var composant = new N4Component
        {
            LogicalName = "Database Host",
            Role = ComponentRole.BaseDeDonnees,
            Server = null
        };

        var resultat = await _service.EvaluateAsync(composant);

        Assert.False(resultat.Reachable);
        Assert.Contains("Aucun serveur", resultat.Error);
    }
}
