using Microsoft.Extensions.Logging.Abstractions;
using N4Sentinel.Infrastructure.Ai;
using N4Sentinel.Infrastructure.Diagnostic;
using N4Sentinel.Infrastructure.Procedures;
using N4Sentinel.Infrastructure.Supervision;

namespace N4Sentinel.Tests;

/// <summary>
/// Tests unitaires et d'intégration pour les 5 modules de la V2.5 Sentinel Intelligence Suite :
/// 1. AIOps Prédictif & Self-Healing
/// 2. Incident Replay & Time-Travel Debugging
/// 3. Flight Simulator Gamifié
/// 4. Digital Twin 3D Terminal
/// 5. Voice Copilot
/// </summary>
public sealed class IntelligenceSuiteTests
{
    [Fact]
    public void PredictiveAiService_Genere_Alertes_Predictives_Et_SelfHealing()
    {
        var service = new PredictiveAiService(NullLogger<PredictiveAiService>.Instance);

        var snapshot = service.RunPredictiveAnalysis("PROD");

        Assert.NotNull(snapshot);
        Assert.Equal("PROD", snapshot.EnvironmentCode);
        Assert.NotEmpty(snapshot.ActivePredictions);

        var alert = snapshot.ActivePredictions.First(x => x.IsHealingExecutable);
        Assert.True(alert.AnomalyScorePercent > 50);
        Assert.True(alert.EstimatedTimeToFailureMinutes > 0);
    }

    [Fact]
    public async Task PredictiveAiService_Execute_Action_SelfHealing()
    {
        var service = new PredictiveAiService(NullLogger<PredictiveAiService>.Instance);

        var ok = await service.ExecuteSelfHealingActionAsync(Guid.NewGuid(), "M. KONATE");

        Assert.True(ok);
    }

    [Fact]
    public void IncidentReplayService_Isole_Patient_Zero_Et_Timeline()
    {
        var service = new IncidentReplayService(NullLogger<IncidentReplayService>.Instance);

        var timeline = service.GetTimeline(Guid.NewGuid());

        Assert.NotNull(timeline);
        Assert.NotEmpty(timeline.Frames);
        Assert.NotNull(timeline.PatientZeroComponent);

        var patientZeroFrame = timeline.Frames.First(x => x.PatientZeroComponent is not null);
        Assert.Equal(18, patientZeroFrame.MinuteIndex);
    }

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
    public void DigitalTwinService_Retourne_Mapping_Equipements_Et_Serveurs()
    {
        var service = new DigitalTwinService(NullLogger<DigitalTwinService>.Instance);

        var snapshot = service.GetTerminalSnapshot();

        Assert.NotNull(snapshot);
        Assert.NotEmpty(snapshot.PhysicalAssets);

        var sts = snapshot.PhysicalAssets.First(x => x.Id == "STS-01");
        Assert.NotEmpty(sts.AssociatedSoftwareNodes);
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
