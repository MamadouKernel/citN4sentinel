using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Observability;

namespace N4Sentinel.Tests;

/// <summary>Tests du service de métriques d'exploitation — NFR-008.</summary>
public sealed class MetricsServiceTests
{
    [Fact(DisplayName = "Le compteur de sondages de supervision distingue succès et échecs")]
    public void RecordSupervisionPoll_Distingue_Succes_Et_Echecs()
    {
        var metrics = new MetricsService();

        metrics.RecordSupervisionPoll(TimeSpan.FromMilliseconds(100), succes: true);
        metrics.RecordSupervisionPoll(TimeSpan.FromMilliseconds(200), succes: true);
        metrics.RecordSupervisionPoll(TimeSpan.FromMilliseconds(300), succes: false);

        var snapshot = metrics.GetSnapshot();

        Assert.Equal(3, snapshot.SupervisionPollCount);
        Assert.Equal(1, snapshot.SupervisionPollFailureCount);
        Assert.Equal(200, snapshot.SupervisionPollAverageMs, 0.01);
    }

    [Fact(DisplayName = "Les issues d'étape sont comptées par état, sans se mélanger")]
    public void RecordStepOutcome_Compte_Par_Etat()
    {
        var metrics = new MetricsService();

        metrics.RecordStepOutcome(ExecutionStepState.Reussi);
        metrics.RecordStepOutcome(ExecutionStepState.Reussi);
        metrics.RecordStepOutcome(ExecutionStepState.Echec);

        var snapshot = metrics.GetSnapshot();

        Assert.Equal(2, snapshot.StepOutcomes[ExecutionStepState.Reussi]);
        Assert.Equal(1, snapshot.StepOutcomes[ExecutionStepState.Echec]);
    }

    [Fact(DisplayName = "Les verdicts de diagnostic sont comptés par type, sans se mélanger")]
    public void RecordDiagnosticVerdict_Compte_Par_Verdict()
    {
        var metrics = new MetricsService();

        metrics.RecordDiagnosticVerdict(DiagnosticVerdict.RienDeConcluant);
        metrics.RecordDiagnosticVerdict(DiagnosticVerdict.CauseCaracterisee);
        metrics.RecordDiagnosticVerdict(DiagnosticVerdict.CauseCaracterisee);

        var snapshot = metrics.GetSnapshot();

        Assert.Equal(1, snapshot.DiagnosticVerdicts[DiagnosticVerdict.RienDeConcluant]);
        Assert.Equal(2, snapshot.DiagnosticVerdicts[DiagnosticVerdict.CauseCaracterisee]);
    }

    [Fact(DisplayName = "Sans aucun sondage enregistré, la moyenne reste zéro plutôt qu'une division par zéro")]
    public void GetSnapshot_Sans_Sondage_Renvoie_Une_Moyenne_Nulle()
    {
        var metrics = new MetricsService();

        var snapshot = metrics.GetSnapshot();

        Assert.Equal(0, snapshot.SupervisionPollCount);
        Assert.Equal(0, snapshot.SupervisionPollAverageMs);
    }
}
