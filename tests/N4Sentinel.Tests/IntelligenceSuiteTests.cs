using Xunit;
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
    [SkippableFact]
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

    [SkippableFact]
    public void VoiceCopilotService_Interprete_Commandes_Vocales()
    {
        var service = new VoiceCopilotService(NullLogger<VoiceCopilotService>.Instance);

        var res = service.ProcessVoiceCommand("Vérifier la santé du cluster");

        Assert.NotNull(res);
        Assert.Equal("CHECK_HEALTH", res.RecognizedIntent);
        Assert.Equal("/supervision", res.TargetRoute);
    }

    [Theory]
    [InlineData("Ouvre le tableau de bord", "HOME", "/")]
    [InlineData("Affiche les diagnostics", "DIAGNOSTICS", "/diagnostics")]
    [InlineData("Ouvre les workflows", "ADMIN_WORKFLOWS", "/admin/workflows")]
    [InlineData("Ouvre l'historique et l'escalade", "HISTORY", "/historique")]
    [InlineData("Ouvre le journal d'audit", "AUDIT", "/admin/audit")]
    [InlineData("Comptes et droits utilisateurs", "USERS", "/admin/utilisateurs")]
    [InlineData("Ouvre Azure AD", "AZURE_AD", "/admin/azure-ad")]
    [InlineData("Lance une sauvegarde", "BACKUP", "/admin/sauvegarde")]
    [InlineData("Affiche les environnements", "ENVIRONMENTS", "/admin/environnements")]
    [InlineData("Ouvre les rapports SLA", "REPORTS", "/admin/rapports")]
    [InlineData("Ouvre la matrice de criticité", "APPROVAL_MATRIX", "/admin/matrice-approbation")]
    [InlineData("Ouvre les signatures d'anomalie", "ANOMALY_SIGNATURES", "/admin/signatures")]
    [InlineData("Démarre le service EDI", "START_SERVICE", "/operations")]
    [InlineData("Arrête le service de synchronisation", "STOP_SERVICE", "/operations")]
    public void VoiceCopilotService_Couvre_Toutes_Les_Vues_Du_Menu(string commande, string intentAttendu, string routeAttendue)
    {
        var service = new VoiceCopilotService(NullLogger<VoiceCopilotService>.Instance);

        var res = service.ProcessVoiceCommand(commande);

        Assert.Equal(intentAttendu, res.RecognizedIntent);
        Assert.Equal(routeAttendue, res.TargetRoute);
    }

    [SkippableFact]
    public void VoiceCopilotService_Ne_Confond_Pas_Lancer_Une_Sauvegarde_Avec_Demarrer_Un_Service()
    {
        var service = new VoiceCopilotService(NullLogger<VoiceCopilotService>.Instance);

        var res = service.ProcessVoiceCommand("Lance une sauvegarde");

        Assert.Equal("BACKUP", res.RecognizedIntent);
        Assert.Equal("/admin/sauvegarde", res.TargetRoute);
    }

    [SkippableFact]
    public void VoiceCopilotService_Ne_Redirige_Plus_Vers_Les_Modules_Retires()
    {
        var service = new VoiceCopilotService(NullLogger<VoiceCopilotService>.Instance);

        foreach (var commande in new[] { "Ouvre l'AIOps", "Lance un replay d'incident", "Ouvre le jumeau numérique" })
        {
            var res = service.ProcessVoiceCommand(commande);
            Assert.NotEqual("/aiops", res.TargetRoute);
            Assert.NotEqual("/incident-replay", res.TargetRoute);
            Assert.NotEqual("/digital-twin", res.TargetRoute);
        }
    }
}
