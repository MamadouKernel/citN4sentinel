using Xunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Ai;
using N4Sentinel.Infrastructure.Persistence;
using N4Sentinel.Infrastructure.Procedures;
using N4Sentinel.Infrastructure.Supervision;

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
///
/// Le Voice Copilot avait survécu à ce retrait avec exactement le même défaut,
/// et personne ne l'avait vu : les tests ci-dessous ne vérifiaient que
/// l'intention reconnue et la route visée, jamais LE TEXTE PRONONCÉ. Deux
/// réponses affirmaient donc des faits écrits en dur — « 4 nœuds sur 4 sont
/// actifs », « 342 messages BAPLIE intégrés aujourd'hui » — alors que le
/// service n'avait aucune dépendance de données et ne pouvait rien lire.
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

    // =======================================================================
    // Montage
    // =======================================================================

    /// <summary>
    /// Copilote monté sur un cache de supervision vide et une base vide : rien
    /// n'est pré-rempli, exactement comme une installation neuve.
    /// </summary>
    private static (VoiceCopilotService Service, SupervisionStateCache Cache) CreerCopilote()
    {
        var cache = new SupervisionStateCache();

        var options = new DbContextOptionsBuilder<N4SentinelDbContext>()
            .UseInMemoryDatabase($"voix-{Guid.NewGuid():N}")
            .Options;

        var service = new VoiceCopilotService(
            cache, new FabriqueContexteDeTest(options), NullLogger<VoiceCopilotService>.Instance);

        return (service, cache);
    }

    private static ComponentHealthSnapshot Releve(string nom, ComponentState etat) => new()
    {
        ComponentId = Guid.NewGuid(),
        LogicalName = nom,
        EnvironmentCode = "PRD",
        State = etat
    };

    private sealed class FabriqueContexteDeTest(DbContextOptions<N4SentinelDbContext> options)
        : IDbContextFactory<N4SentinelDbContext>
    {
        public N4SentinelDbContext CreateDbContext() => new(options);
    }

    // =======================================================================
    // Intentions et routes
    // =======================================================================

    [SkippableFact]
    public async Task VoiceCopilotService_Interprete_Commandes_Vocales()
    {
        var (service, _) = CreerCopilote();

        var res = await service.ProcessVoiceCommandAsync("Vérifier la santé du cluster");

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
    public async Task VoiceCopilotService_Couvre_Toutes_Les_Vues_Du_Menu(
        string commande, string intentAttendu, string routeAttendue)
    {
        var (service, _) = CreerCopilote();

        var res = await service.ProcessVoiceCommandAsync(commande);

        Assert.Equal(intentAttendu, res.RecognizedIntent);
        Assert.Equal(routeAttendue, res.TargetRoute);
    }

    [SkippableFact]
    public async Task VoiceCopilotService_Ne_Confond_Pas_Lancer_Une_Sauvegarde_Avec_Demarrer_Un_Service()
    {
        var (service, _) = CreerCopilote();

        var res = await service.ProcessVoiceCommandAsync("Lance une sauvegarde");

        Assert.Equal("BACKUP", res.RecognizedIntent);
        Assert.Equal("/admin/sauvegarde", res.TargetRoute);
    }

    [SkippableFact]
    public async Task VoiceCopilotService_Ne_Redirige_Plus_Vers_Les_Modules_Retires()
    {
        var (service, _) = CreerCopilote();

        foreach (var commande in new[] { "Ouvre l'AIOps", "Lance un replay d'incident", "Ouvre le jumeau numérique" })
        {
            var res = await service.ProcessVoiceCommandAsync(commande);
            Assert.NotEqual("/aiops", res.TargetRoute);
            Assert.NotEqual("/incident-replay", res.TargetRoute);
            Assert.NotEqual("/digital-twin", res.TargetRoute);
        }
    }

    // =======================================================================
    // Ce que le copilote DIT — et non seulement où il navigue
    // =======================================================================

    [SkippableFact]
    public async Task Sans_Aucun_Releve_Le_Copilote_Refuse_De_Donner_Un_Etat()
    {
        var (service, _) = CreerCopilote();

        var res = await service.ProcessVoiceCommandAsync("Vérifier la santé du cluster");

        // Le point qui compte : sur une installation neuve, il doit dire qu'il
        // ne sait pas — jamais que tout va bien.
        Assert.Contains("aucun relevé", res.SpeechSynthesisText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("opérationnel", res.SpeechSynthesisText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("4 nœuds", res.SpeechSynthesisText);
    }

    [SkippableFact]
    public async Task L_Etat_Enonce_Reprend_Les_Releves_Reels_Et_Leur_Age()
    {
        var (service, cache) = CreerCopilote();

        cache.UpdateSnapshot(Releve("Center", ComponentState.Disponible));
        cache.UpdateSnapshot(Releve("Cluster 1", ComponentState.Disponible));
        cache.UpdateSnapshot(Releve("Bridge", ComponentState.Indisponible));

        var res = await service.ProcessVoiceCommandAsync("Quel est l'état du cluster ?");
        var dit = res.SpeechSynthesisText;

        Assert.Contains("3 composants", dit);
        Assert.Contains("2 disponibles", dit);
        Assert.Contains("1 indisponible", dit);

        // L'âge du relevé fait partie de l'information : un « tout va bien »
        // vieux d'une heure ne dit rien de maintenant.
        Assert.Contains("Relevé", dit);

        // Aucun verdict global : c'est une mesure, pas une conclusion.
        Assert.DoesNotContain("opérationnel", dit, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Un_Etat_Inconnu_Est_Dit_Et_N_Est_Pas_Compte_Comme_Sain()
    {
        var (service, cache) = CreerCopilote();

        cache.UpdateSnapshot(Releve("Center", ComponentState.Disponible));
        cache.UpdateSnapshot(Releve("XPS", ComponentState.Inconnu));

        var res = await service.ProcessVoiceCommandAsync("santé du cluster");

        Assert.Contains("inconnu", res.SpeechSynthesisText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("n'est pas un état sain", res.SpeechSynthesisText);
    }

    [SkippableFact]
    public async Task Sans_Fichier_Suivi_Le_Copilote_N_Invente_Aucun_Chiffre_EDI()
    {
        var (service, _) = CreerCopilote();

        var res = await service.ProcessVoiceCommandAsync("Où en sont les flux EDI ?");

        Assert.Equal("CHECK_EDI", res.RecognizedIntent);
        Assert.DoesNotContain("342", res.SpeechSynthesisText);
        Assert.Contains("Aucun fichier EDI", res.SpeechSynthesisText, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Aucune_Reponse_Vocale_N_Affirme_Un_Chiffre_Ecrit_En_Dur()
    {
        // Garde-fou général : on balaie toutes les intentions et on vérifie
        // qu'aucune ne prononce de nombre sur une installation vierge. Un
        // chiffre sans donnée derrière est forcément inventé.
        var (service, _) = CreerCopilote();

        string[] commandes =
        [
            "tableau de bord", "supervision", "vitalité", "opérations", "workflows",
            "EDI", "alertes", "SOP", "diagnostics", "signatures", "historique",
            "journal d'audit", "utilisateurs", "azure ad", "sauvegarde",
            "environnements", "rapports", "matrice de criticité"
        ];

        foreach (var commande in commandes)
        {
            var res = await service.ProcessVoiceCommandAsync(commande);

            Assert.DoesNotMatch(@"\b\d+\b", res.SpeechSynthesisText);
        }
    }
}
