using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Connectors;
using N4Sentinel.Infrastructure.Supervision;

namespace N4Sentinel.Tests;

/// <summary>
/// Tests de la conformité horaire — la réponse OK/KO attendue sur chaque nœud.
///
/// Le point qui compte : l'application distingue TROIS réponses, pas deux.
/// Quand la source de temps attendue n'est pas renseignée au référentiel, elle
/// sait encore détecter les cas franchement anormaux — service arrêté, horloge
/// locale, écart excessif — mais elle ne peut pas affirmer que la source EST
/// celle du domaine. Répondre « OK » dans ce cas serait affirmer ce qui n'a pas
/// été vérifié, et c'est exactement le genre d'affirmation qui fait chercher
/// une panne du mauvais côté pendant une heure.
/// </summary>
public sealed class VitaliteTests
{
    private static N4Environment Environnement(string? sourceAttendue, int tolerance = 5) => new()
    {
        Code = "PRD",
        Name = "Production",
        ExpectedTimeSource = sourceAttendue,
        ClockToleranceSeconds = tolerance
    };

    private static TimeSyncSnapshot Horloge(
        string service = "Running",
        string? type = "NT5DS",
        string? source = "DC01.cit.local",
        string? pairs = null,
        bool domaine = true,
        string? nomDomaine = "cit.local") => new()
    {
        HostName = "CIT-N4-APP01",
        ServiceStatus = service,
        SyncType = type,
        ActualSource = source,
        ConfiguredPeers = pairs,
        IsDomainMember = domaine,
        DomainName = nomDomaine
    };

    // =======================================================================
    // Cas non conformes — détectables sans connaître la source attendue
    // =======================================================================
    [Fact]
    public void Le_Service_De_Temps_Arrete_Est_Non_Conforme()
    {
        var r = NodeVitalsService.EvaluerHorloge(
            Horloge(service: "Stopped"), 0.2, Environnement("cit.local"), null);

        Assert.Equal(ClockState.NonConforme, r.State);
        Assert.Equal("KO", r.ShortVerdict);
        Assert.Contains("W32Time", r.Detail);
        Assert.NotNull(r.Remedy);
    }

    [Theory]
    [InlineData("NoSync", "DC01.cit.local")]
    [InlineData("NT5DS", "Local CMOS Clock")]
    [InlineData("NTP", "Free-running System Clock")]
    public void Une_Machine_Qui_Ne_Se_Synchronise_Sur_Rien_Est_Non_Conforme(string type, string source)
    {
        var r = NodeVitalsService.EvaluerHorloge(
            Horloge(type: type, source: source), 0.1, Environnement("cit.local"), null);

        Assert.Equal(ClockState.NonConforme, r.State);
        Assert.Contains("ne se synchronise sur rien", r.Detail);
    }

    [Fact]
    public void Un_Ecart_Superieur_A_La_Tolerance_Est_Non_Conforme()
    {
        // 7 s pour une tolerance de 5 : au-dela de ce seuil, N4 produit des
        // statuts DISCONNECTED sans cause apparente.
        var r = NodeVitalsService.EvaluerHorloge(
            Horloge(), 7.4, Environnement("cit.local"), null);

        Assert.Equal(ClockState.NonConforme, r.State);
        Assert.Equal(7.4, r.SkewSeconds);
        Assert.Contains("DISCONNECTED", r.Detail);
    }

    [Fact]
    public void Un_Ecart_Negatif_Est_Traite_Comme_Un_Ecart()
    {
        // Une horloge en retard est aussi fausse qu'une horloge en avance.
        var r = NodeVitalsService.EvaluerHorloge(
            Horloge(), -12.0, Environnement("cit.local"), null);

        Assert.Equal(ClockState.NonConforme, r.State);
        Assert.Contains("12", r.Detail);
    }

    [Fact]
    public void La_Tolerance_Est_Celle_De_L_Environnement()
    {
        var large = NodeVitalsService.EvaluerHorloge(
            Horloge(), 8.0, Environnement("cit.local", tolerance: 30), null);

        Assert.Equal(ClockState.Conforme, large.State);

        var stricte = NodeVitalsService.EvaluerHorloge(
            Horloge(), 8.0, Environnement("cit.local", tolerance: 2), null);

        Assert.Equal(ClockState.NonConforme, stricte.State);
    }

    [Fact]
    public void Une_Source_Etrangere_Au_Domaine_Est_Non_Conforme()
    {
        var r = NodeVitalsService.EvaluerHorloge(
            Horloge(type: "NTP", source: "pool.ntp.org", nomDomaine: "cit.local"),
            0.3, Environnement("cit.local"), null);

        Assert.Equal(ClockState.NonConforme, r.State);
        Assert.Contains("pool.ntp.org", r.Detail);
        Assert.Contains("cit.local", r.Detail);
        Assert.Contains("finissent par diverger", r.Detail);
    }

    // =======================================================================
    // Cas conformes
    // =======================================================================
    [Fact]
    public void Une_Source_Correspondant_A_L_Attendu_Est_Conforme()
    {
        var r = NodeVitalsService.EvaluerHorloge(
            Horloge(source: "DC01.cit.local"), 0.4, Environnement("cit.local"), null);

        Assert.Equal(ClockState.Conforme, r.State);
        Assert.Equal("OK", r.ShortVerdict);
    }

    [Fact]
    public void La_Hierarchie_Du_Domaine_Vaut_Conformite()
    {
        // NT5DS ne nomme aucun serveur : il suit le controleur de domaine.
        // Exiger une correspondance de chaine ferait declarer non conforme la
        // configuration la plus courante et la plus saine.
        var r = NodeVitalsService.EvaluerHorloge(
            Horloge(type: "NT5DS", source: "VM IC Time Synchronization Provider", nomDomaine: "cit.local"),
            0.2, Environnement("cit.local"), null);

        Assert.Equal(ClockState.Conforme, r.State);
    }

    [Fact]
    public void La_Source_Peut_Etre_Reconnue_Dans_Les_Pairs_Configures()
    {
        var r = NodeVitalsService.EvaluerHorloge(
            Horloge(type: "NTP", source: null, pairs: "DC01.cit.local,0x9", nomDomaine: "cit.local"),
            0.1, Environnement("DC01.cit.local"), null);

        Assert.Equal(ClockState.Conforme, r.State);
    }

    // =======================================================================
    // Le troisième état : à confirmer
    // =======================================================================
    [Fact]
    public void Sans_Source_Attendue_La_Conformite_Reste_A_Confirmer()
    {
        var r = NodeVitalsService.EvaluerHorloge(
            Horloge(), 0.3, Environnement(sourceAttendue: null), null);

        Assert.Equal(ClockState.AConfirmer, r.State);
        Assert.Equal("?", r.ShortVerdict);
        Assert.Contains("ne peut pas confirmer", r.Detail);
        Assert.Contains("Renseigner la source de temps attendue", r.Remedy!);
    }

    [Fact]
    public void Sans_Source_Attendue_Les_Cas_Anormaux_Restent_Detectes()
    {
        // L'absence de configuration ne doit pas rendre l'application aveugle :
        // un service arrete reste un service arrete.
        var arrete = NodeVitalsService.EvaluerHorloge(
            Horloge(service: "Stopped"), 0.1, Environnement(null), null);
        Assert.Equal(ClockState.NonConforme, arrete.State);

        var locale = NodeVitalsService.EvaluerHorloge(
            Horloge(source: "Local CMOS Clock"), 0.1, Environnement(null), null);
        Assert.Equal(ClockState.NonConforme, locale.State);

        var derive = NodeVitalsService.EvaluerHorloge(
            Horloge(), 42.0, Environnement(null), null);
        Assert.Equal(ClockState.NonConforme, derive.State);
    }

    [Fact]
    public void Un_Releve_Impossible_Ne_Se_Lit_Pas_Comme_Une_Conformite()
    {
        // Ne pas avoir pu regarder n'est pas avoir constate que tout va bien.
        var r = NodeVitalsService.EvaluerHorloge(
            null, null, Environnement("cit.local"), "Accès refusé par CIT-N4-APP01.");

        Assert.Equal(ClockState.Inconnu, r.State);
        Assert.Equal("—", r.ShortVerdict);
        Assert.Contains("Accès refusé", r.Detail);
    }

    [Fact]
    public void Sans_Environnement_Le_Comportement_Reste_Prudent()
    {
        var r = NodeVitalsService.EvaluerHorloge(Horloge(), 0.2, null, null);

        Assert.Equal(ClockState.AConfirmer, r.State);
    }

    // =======================================================================
    // Métriques
    // =======================================================================
    [Fact]
    public void Les_Pourcentages_Derives_Sont_Coherents()
    {
        var m = new LiveMetrics
        {
            HostName = "CIT-N4-APP01",
            TotalMemoryBytes = 16L * 1024 * 1024 * 1024,
            FreeMemoryBytes = 4L * 1024 * 1024 * 1024
        };

        Assert.Equal(75.0, m.MemoryUsedPercent);
        Assert.Equal(12L * 1024 * 1024 * 1024, m.UsedMemoryBytes);
    }

    [Fact]
    public void Une_Memoire_Non_Relevee_Ne_Produit_Pas_Un_Faux_Zero()
    {
        // Afficher 0 % d'utilisation sur une mesure absente ferait croire a une
        // machine au repos.
        var m = new LiveMetrics { HostName = "CIT-N4-APP01" };

        Assert.Null(m.MemoryUsedPercent);
        Assert.Null(m.UsedMemoryBytes);
        Assert.Null(m.Uptime);
    }

    [Fact]
    public void Un_Disque_Presque_Plein_Est_Mesurable()
    {
        var d = new DiskSnapshot
        {
            Drive = "C:",
            TotalBytes = 200L * 1024 * 1024 * 1024,
            FreeBytes = 10L * 1024 * 1024 * 1024
        };

        Assert.Equal(5.0, d.FreePercent);
    }

    // =======================================================================
    // Mises à jour Windows — fraîcheur
    // =======================================================================
    [Fact]
    public void Une_Mesure_De_Mises_A_Jour_Porte_Son_Age()
    {
        var lecture = UpdateReading.Reussi(new UpdateSnapshot
        {
            HostName = "CIT-N4-APP01",
            PendingCount = 12,
            SecurityCount = 5
        });

        Assert.True(lecture.Succeeded);
        Assert.False(lecture.IsStale);
        Assert.True(lecture.Age < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Une_Mesure_Ancienne_Est_Declaree_Perimee()
    {
        // Une mesure de mises a jour vieille de huit heures ne doit pas etre
        // presentee comme l'etat courant.
        var lecture = UpdateReading.Reussi(new UpdateSnapshot { HostName = "X" })
            with { MeasuredAt = DateTimeOffset.UtcNow.AddHours(-8) };

        Assert.True(lecture.IsStale);
    }

    [Fact]
    public void Un_Echec_De_Releve_N_Est_Pas_Un_Zero_Mise_A_Jour()
    {
        // Zero mise a jour en attente et "je n'ai pas pu regarder" sont deux
        // affirmations differentes.
        var lecture = UpdateReading.EnEchec("L'agent Windows Update n'a pas pu être interrogé.");

        Assert.False(lecture.Succeeded);
        Assert.Null(lecture.Snapshot);
        Assert.NotNull(lecture.Error);
    }

    [Fact]
    public void Le_Cache_Retient_La_Derniere_Mesure_Par_Serveur()
    {
        var cache = new UpdateReadingCache();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        Assert.Null(cache.Get(a));

        cache.Set(a, UpdateReading.Reussi(new UpdateSnapshot { HostName = "A", PendingCount = 3 }));
        cache.Set(b, UpdateReading.EnEchec("injoignable"));

        Assert.Equal(3, cache.Get(a)!.Snapshot!.PendingCount);
        Assert.False(cache.Get(b)!.Succeeded);
    }
}
