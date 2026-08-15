using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Persistence;
using N4Sentinel.Infrastructure.Supervision;

namespace N4Sentinel.Tests;

/// <summary>
/// Tests des alertes de supervision - SUP-05.
///
/// L'essentiel n'est pas de verifier qu'une alerte se cree : c'est de verifier
/// qu'elle ne se recree PAS a chaque passe, et qu'elle se referme seule. Sans
/// ces deux comportements, la liste devient illisible en une heure et plus
/// personne ne la consulte - ce qui revient a n'avoir pas d'alertes du tout.
/// </summary>
public sealed class AlertesTests : IAsyncLifetime
{
    private const string MasterConnection =
        "Server=localhost;Database=master;Trusted_Connection=True;TrustServerCertificate=True";

    private readonly string _databaseName = $"n4sentinel_test_{Guid.NewGuid():N}";
    private TestDbContextFactory _factory = null!;
    private AlertService _alerts = null!;
    private Guid _envId;
    private Guid _componentId;

    public async Task InitializeAsync()
    {
        var cs = $"Server=localhost;Database={_databaseName};Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True";
        _factory = new TestDbContextFactory(TestDbContextOptions.Builder(cs).Options);
        _alerts = new AlertService(_factory, NullLogger<AlertService>.Instance);

        await using var db = _factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();

        var env = new N4Environment { Code = "UAT", Name = "Recette" };
        db.Environments.Add(env);
        await db.SaveChangesAsync();
        _envId = env.Id;

        var composant = new N4Component
        {
            EnvironmentId = _envId,
            LogicalName = "Center Node",
            Role = ComponentRole.CenterNode,
            WindowsServiceName = "Navis N4 Center Node",
            ControlMode = ControlMode.Pilotable
        };
        db.Components.Add(composant);
        await db.SaveChangesAsync();
        _componentId = composant.Id;
    }

    public async Task DisposeAsync()
    {
        Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
        await using var master = new Microsoft.Data.SqlClient.SqlConnection(MasterConnection);
        await master.OpenAsync();
        var cmd = master.CreateCommand();
        cmd.CommandText =
            $"IF DB_ID('{_databaseName}') IS NOT NULL BEGIN " +
            $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
            $"DROP DATABASE [{_databaseName}]; END";
        await cmd.ExecuteNonQueryAsync();
    }

    // -----------------------------------------------------------------------
    private ComponentHealthSnapshot Instantane(
        ComponentState etat = ComponentState.Disponible,
        LogProofState preuve = LogProofState.Proved,
        double? ecartHorloge = null,
        string serviceStatus = "Running",
        double? tempsReponseMs = null,
        bool? synchroEnRetard = null,
        double? diskFreePercent = null) => new()
    {
        ComponentId = _componentId,
        LogicalName = "Center Node",
        EnvironmentCode = "UAT",
        HostName = "N4CENTER01",
        Role = ComponentRole.CenterNode,
        WindowsServiceName = "Navis N4 Center Node",
        State = etat,
        ServiceStatus = serviceStatus,
        LogProofStatus = preuve,
        LogPathResolved = @"C:\ProgramData\Navis\center\logs\navis-apex.log",
        ClockSkewSeconds = ecartHorloge,
        ResponseTimeMs = tempsReponseMs,
        SyncDelayed = synchroEnRetard,
        DiskFreePercentMin = diskFreePercent,
        DiskCriticalDrive = diskFreePercent is not null ? "C:" : null,
        Verdict = "verdict de test"
    };

    private async Task<List<Alert>> ToutesAsync()
    {
        await using var db = _factory.CreateDbContext();
        return await db.Alerts.AsNoTracking().ToListAsync();
    }

    // -----------------------------------------------------------------------
    [Fact(DisplayName = "Un composant sain ne produit aucune alerte")]
    public async Task Composant_Sain_N_Alerte_Pas()
    {
        await _alerts.EvaluateAsync(Instantane(), _envId);

        Assert.Empty(await ToutesAsync());
    }

    [Fact(DisplayName = "Un composant indisponible ouvre une alerte critique avec conduite a tenir")]
    public async Task Indisponibilite_Ouvre_Une_Alerte()
    {
        await _alerts.EvaluateAsync(
            Instantane(ComponentState.Indisponible, LogProofState.Proved, serviceStatus: "Stopped"), _envId);

        var alerte = Assert.Single(await ToutesAsync());
        Assert.Equal(AlertKind.Indisponibilite, alerte.Kind);
        Assert.Equal(AlertSeverity.Critique, alerte.Severity);
        Assert.Equal(AlertStatus.Ouverte, alerte.Status);

        // Une alerte sans conduite a tenir reporte le travail sur celui qui la lit.
        Assert.False(string.IsNullOrWhiteSpace(alerte.Recommendation));
    }

    [Fact(DisplayName = "Une condition qui dure n'est PAS dupliquee : le compteur s'incremente")]
    public async Task La_Meme_Condition_Ne_Cree_Qu_Une_Alerte()
    {
        var instantane = Instantane(ComponentState.Indisponible, LogProofState.Proved, serviceStatus: "Stopped");

        // Dix passes de collecte, soit cinq minutes de panne au rythme reel.
        for (var i = 0; i < 10; i++)
            await _alerts.EvaluateAsync(instantane, _envId);

        var alerte = Assert.Single(await ToutesAsync());
        Assert.Equal(10, alerte.OccurrenceCount);
        Assert.True(alerte.LastOccurredAt >= alerte.FirstOccurredAt);
    }

    [Fact(DisplayName = "Une condition disparue resout l'alerte automatiquement")]
    public async Task La_Condition_Disparue_Resout_L_Alerte()
    {
        await _alerts.EvaluateAsync(
            Instantane(ComponentState.Indisponible, LogProofState.Proved, serviceStatus: "Stopped"), _envId);

        // Le composant repart.
        await _alerts.EvaluateAsync(Instantane(), _envId);

        var alerte = Assert.Single(await ToutesAsync());
        Assert.Equal(AlertStatus.Resolue, alerte.Status);
        Assert.NotNull(alerte.ResolvedAt);

        // Exiger un acquittement manuel pour refermer ce qui est deja regle
        // produirait une liste encombree d'alertes mortes.
        Assert.Empty(await _alerts.GetOpenAsync(_envId));
    }

    [Fact(DisplayName = "Service qui tourne mais journal en echec : incoherence d'etat, en critique")]
    public async Task L_Incoherence_D_Etat_Est_Detectee()
    {
        // Le cas que le cahier des charges vise en premier : le composant donne
        // l'illusion de fonctionner, ce qui est pire qu'une panne franche.
        await _alerts.EvaluateAsync(
            Instantane(ComponentState.Degrade, LogProofState.ErrorDetected), _envId);

        var alertes = await ToutesAsync();
        var incoherence = alertes.Single(a => a.Kind == AlertKind.IncoherenceEtat);

        Assert.Equal(AlertSeverity.Critique, incoherence.Severity);
        Assert.Contains("trompeur", incoherence.Detail);
    }

    [Fact(DisplayName = "Un ecart d'horloge au-dela d'une seconde declenche une alerte")]
    public async Task L_Ecart_D_Horloge_Declenche_Une_Alerte()
    {
        await _alerts.EvaluateAsync(Instantane(ecartHorloge: 2.4), _envId);

        var alerte = (await ToutesAsync()).Single(a => a.Kind == AlertKind.EcartHorloge);
        Assert.Equal(AlertSeverity.Avertissement, alerte.Severity);
        Assert.Contains("DISCONNECTED", alerte.Recommendation!);

        // Au-dela de cinq secondes, la gravite monte.
        await _alerts.EvaluateAsync(Instantane(ecartHorloge: 12.0), _envId);
        var apres = (await ToutesAsync()).Single(a => a.Kind == AlertKind.EcartHorloge);
        Assert.Equal(AlertSeverity.Critique, apres.Severity);

        // Toujours la meme alerte : la gravite a change, pas l'identite.
        Assert.Equal(alerte.Id, apres.Id);
    }

    [Fact(DisplayName = "Un ecart d'horloge negligeable ne declenche rien")]
    public async Task Un_Ecart_Negligeable_Est_Ignore()
    {
        await _alerts.EvaluateAsync(Instantane(ecartHorloge: 0.3), _envId);

        Assert.DoesNotContain(await ToutesAsync(), a => a.Kind == AlertKind.EcartHorloge);
    }

    [Fact(DisplayName = "Un temps de reponse au-dela du seuil declenche une alerte de noeud lent (FR-058)")]
    public async Task Un_Temps_De_Reponse_Eleve_Declenche_Une_Alerte()
    {
        await _alerts.EvaluateAsync(Instantane(tempsReponseMs: 8000), _envId);

        var alerte = (await ToutesAsync()).Single(a => a.Kind == AlertKind.NoeudLent);
        Assert.Equal(AlertSeverity.Avertissement, alerte.Severity);
        Assert.Contains("8000", alerte.Detail);
    }

    [Fact(DisplayName = "Un temps de reponse normal ne declenche rien")]
    public async Task Un_Temps_De_Reponse_Normal_N_Alerte_Pas()
    {
        await _alerts.EvaluateAsync(Instantane(tempsReponseMs: 120), _envId);

        Assert.DoesNotContain(await ToutesAsync(), a => a.Kind == AlertKind.NoeudLent);
    }

    [Fact(DisplayName = "Un disque sous 10% libre declenche une alerte de ressource critique (FR-054)")]
    public async Task Un_Disque_Presque_Plein_Declenche_Une_Alerte()
    {
        await _alerts.EvaluateAsync(Instantane(diskFreePercent: 8.0), _envId);

        var alerte = (await ToutesAsync()).Single(a => a.Kind == AlertKind.RessourceCritique);
        Assert.Equal(AlertSeverity.Avertissement, alerte.Severity);
        Assert.Contains("C:", alerte.Detail);
        Assert.Contains("8", alerte.Detail);

        // Sous 5%, la gravite monte a Critique.
        await _alerts.EvaluateAsync(Instantane(diskFreePercent: 3.0), _envId);
        var apres = (await ToutesAsync()).Single(a => a.Kind == AlertKind.RessourceCritique);
        Assert.Equal(AlertSeverity.Critique, apres.Severity);
        Assert.Equal(alerte.Id, apres.Id);
    }

    [Fact(DisplayName = "Un disque avec assez d'espace libre ne declenche rien")]
    public async Task Un_Disque_Avec_Assez_D_Espace_N_Alerte_Pas()
    {
        await _alerts.EvaluateAsync(Instantane(diskFreePercent: 42.0), _envId);

        Assert.DoesNotContain(await ToutesAsync(), a => a.Kind == AlertKind.RessourceCritique);
    }

    [Fact(DisplayName = "Une synchronisation N4-XPS en retard declenche une alerte critique (FR-056)")]
    public async Task Une_Synchronisation_En_Retard_Declenche_Une_Alerte()
    {
        await _alerts.EvaluateAsync(Instantane(synchroEnRetard: true), _envId);

        var alerte = (await ToutesAsync()).Single(a => a.Kind == AlertKind.SynchronisationXpsRetardee);
        Assert.Equal(AlertSeverity.Critique, alerte.Severity);
    }

    [Fact(DisplayName = "Une synchronisation N4-XPS a jour ne declenche rien")]
    public async Task Une_Synchronisation_A_Jour_N_Alerte_Pas()
    {
        await _alerts.EvaluateAsync(Instantane(synchroEnRetard: false), _envId);

        Assert.DoesNotContain(await ToutesAsync(), a => a.Kind == AlertKind.SynchronisationXpsRetardee);
    }

    [Fact(DisplayName = "Plusieurs conditions simultanees produisent plusieurs alertes distinctes")]
    public async Task Des_Conditions_Distinctes_Coexistent()
    {
        await _alerts.EvaluateAsync(
            Instantane(ComponentState.Indisponible, LogProofState.LogNotFound, ecartHorloge: 3.0,
                       serviceStatus: "Stopped"),
            _envId);

        var alertes = await ToutesAsync();

        Assert.Contains(alertes, a => a.Kind == AlertKind.Indisponibilite);
        Assert.Contains(alertes, a => a.Kind == AlertKind.PreuveIndisponible);
        Assert.Contains(alertes, a => a.Kind == AlertKind.EcartHorloge);
        Assert.Equal(3, alertes.Count);
    }

    [Fact(DisplayName = "Acquitter ne resout pas : l'alerte reste ouverte mais sort du decompte")]
    public async Task Acquitter_N_Est_Pas_Resoudre()
    {
        await _alerts.EvaluateAsync(
            Instantane(ComponentState.Indisponible, LogProofState.Proved, serviceStatus: "Stopped"), _envId);

        var alerte = Assert.Single(await ToutesAsync());
        await _alerts.AcknowledgeAsync(alerte.Id, "cit\\m.konate", "Maintenance planifiee, coupure attendue");

        var apres = Assert.Single(await ToutesAsync());
        Assert.Equal(AlertStatus.Acquittee, apres.Status);
        Assert.Null(apres.ResolvedAt);
        Assert.Equal("cit\\m.konate", apres.AcknowledgedBy);
        Assert.True(apres.IsOpen);

        // Une alerte acquittee reste suivie tant que sa condition dure.
        Assert.Single(await _alerts.GetOpenAsync(_envId));
    }

    [Fact(DisplayName = "Une alerte acquittee dont la condition cesse se resout quand meme")]
    public async Task Une_Alerte_Acquittee_Se_Resout()
    {
        await _alerts.EvaluateAsync(
            Instantane(ComponentState.Indisponible, LogProofState.Proved, serviceStatus: "Stopped"), _envId);

        var alerte = Assert.Single(await ToutesAsync());
        await _alerts.AcknowledgeAsync(alerte.Id, "cit\\m.konate", null);

        await _alerts.EvaluateAsync(Instantane(), _envId);

        Assert.Equal(AlertStatus.Resolue, (await ToutesAsync()).Single().Status);
    }

    [Fact(DisplayName = "Un etat inconnu est signale comme absence d'information, pas comme panne")]
    public async Task L_Etat_Inconnu_Est_Signale_Comme_Tel()
    {
        await _alerts.EvaluateAsync(
            Instantane(ComponentState.Inconnu, LogProofState.Unprovable), _envId);

        var alerte = (await ToutesAsync()).Single(a => a.Kind == AlertKind.CollecteImpossible);
        Assert.Equal(AlertSeverity.Avertissement, alerte.Severity);
        Assert.Contains("absence de problème", alerte.Recommendation!);
    }

    // -----------------------------------------------------------------------
    // Conflit de role Center/Standby (FR-032/033)
    // -----------------------------------------------------------------------
    private static ComponentHealthSnapshot InstantaneRole(ComponentRole role, string nom, bool? actif) => new()
    {
        ComponentId = Guid.NewGuid(),
        LogicalName = nom,
        EnvironmentCode = "UAT",
        HostName = nom,
        Role = role,
        State = ComponentState.Disponible,
        LogProofStatus = LogProofState.Proved,
        HoldsActiveRole = actif,
        Verdict = "verdict de test"
    };

    [Fact(DisplayName = "Center et Standby actifs en meme temps ouvrent une alerte critique de portee environnement")]
    public async Task Center_Et_Standby_Actifs_Ouvrent_Une_Alerte()
    {
        var center = InstantaneRole(ComponentRole.CenterNode, "Center Node", actif: true);
        var standby = InstantaneRole(ComponentRole.StandbyCenterNode, "Standby Node", actif: true);

        await _alerts.EvaluateCenterConflictAsync(_envId, center, standby);

        var alerte = Assert.Single(await ToutesAsync());
        Assert.Equal(AlertKind.ConflitRoleActifCenter, alerte.Kind);
        Assert.Equal(AlertSeverity.Critique, alerte.Severity);
        Assert.Null(alerte.ComponentId);
        Assert.Contains("Center Node", alerte.Detail);
        Assert.Contains("Standby Node", alerte.Detail);
    }

    [Fact(DisplayName = "Un seul des deux actif ne declenche rien")]
    public async Task Un_Seul_Actif_N_Alerte_Pas()
    {
        var center = InstantaneRole(ComponentRole.CenterNode, "Center Node", actif: true);
        var standby = InstantaneRole(ComponentRole.StandbyCenterNode, "Standby Node", actif: false);

        await _alerts.EvaluateCenterConflictAsync(_envId, center, standby);

        Assert.Empty(await ToutesAsync());
    }

    [Fact(DisplayName = "Role non prouvable (marqueur non configure) ne declenche pas de faux conflit")]
    public async Task Role_Non_Prouvable_N_Alerte_Pas()
    {
        var center = InstantaneRole(ComponentRole.CenterNode, "Center Node", actif: null);
        var standby = InstantaneRole(ComponentRole.StandbyCenterNode, "Standby Node", actif: null);

        await _alerts.EvaluateCenterConflictAsync(_envId, center, standby);

        Assert.Empty(await ToutesAsync());
    }

    [Fact(DisplayName = "Le conflit se resout automatiquement des que la bascule est faite")]
    public async Task Le_Conflit_Se_Resout_Quand_La_Bascule_Est_Faite()
    {
        var center = InstantaneRole(ComponentRole.CenterNode, "Center Node", actif: true);
        var standby = InstantaneRole(ComponentRole.StandbyCenterNode, "Standby Node", actif: true);
        await _alerts.EvaluateCenterConflictAsync(_envId, center, standby);
        Assert.Single(await ToutesAsync());

        // Le Standby redescend : la bascule s'est resolue d'elle-meme.
        var standbyResolu = InstantaneRole(ComponentRole.StandbyCenterNode, "Standby Node", actif: false);
        await _alerts.EvaluateCenterConflictAsync(_envId, center, standbyResolu);

        var alerte = Assert.Single(await ToutesAsync());
        Assert.Equal(AlertStatus.Resolue, alerte.Status);
    }

    private sealed class TestDbContextFactory(DbContextOptions<N4SentinelDbContext> options)
        : IDbContextFactory<N4SentinelDbContext>
    {
        public N4SentinelDbContext CreateDbContext() => new(options);
    }
}
