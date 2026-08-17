using Microsoft.Extensions.Logging.Abstractions;
using N4Sentinel.Infrastructure.Ai;
using N4Sentinel.Infrastructure.Procedures;

namespace N4Sentinel.Tests;

/// <summary>
/// Tests unitaires pour les modules de la Sentinel Intelligence Suite adossés à
/// des données réelles ou à une logique déterministe vérifiable :
/// 1. Flight Simulator Gamifié
/// 2. Voice Copilot
///
/// AIOps Prédictif, Incident Replay et Digital Twin ont été retirés (donnée de
/// démonstration fixe sans lien avec l'état réel du système, cf. décision #12
/// du plan de remédiation de l'audit CIT-CIV-DSI-RFP-0010) : leurs tests ont
/// été supprimés avec le code qu'ils couvraient.
/// </summary>
public sealed class IntelligenceSuiteTests
{
    [Fact]
    public void FlightSimulatorService_Gere_Scenarios_Et_Calcul_Score()
    {
        var service = new FlightSimulatorService(NullLogger<FlightSimulatorService>.Instance);

        var scenarios = service.GetAvailableScenarios();
        Assert.NotEmpty(scenarios);

        var session = service.StartSession("SIM-CRASH-DB", "M. KONATE");
        Assert.NotNull(session);
        Assert.Equal(100.0, session.ScorePercent);

        var result = service.ExecuteAction(session.SessionId, "Preflight Check & Vérification Standby");
        Assert.True(result.IsSuccess);
        Assert.True(result.ScoreDelta > 0);
    }

    [Fact]
    public void VoiceCopilotService_Interprete_Commandes_Vocales()
    {
        var service = new VoiceCopilotService(NullLogger<VoiceCopilotService>.Instance);

        var res = service.ProcessVoiceCommand("Vérifier la santé du cluster");

        Assert.NotNull(res);
        Assert.Equal("CHECK_HEALTH", res.RecognizedIntent);
        Assert.Equal("/supervision", res.TargetRoute);
    }
}
