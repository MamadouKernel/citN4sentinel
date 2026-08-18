using Xunit;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Diagnostic;

namespace N4Sentinel.Tests;

/// <summary>
/// FR-071 — deviner l'origine d'un journal versé manuellement.
///
/// Ce qui est testé ici n'est pas « sait-on deviner », mais « sait-on se
/// taire ». Une attribution erronée est bien pire qu'une absence
/// d'attribution : elle oriente la chronologie, la corrélation et
/// l'intervention vers le mauvais serveur, sans que rien ne signale que
/// c'était une supposition. La majorité des cas ci-dessous vérifient donc que
/// l'heuristique REFUSE de conclure.
/// </summary>
public sealed class OrigineJournalTests
{
    private static readonly Guid Env = Guid.NewGuid();

    private static N4Component Composant(
        string nom, ComponentRole role, string? service = null, N4Server? serveur = null) => new()
        {
            Id = Guid.NewGuid(),
            EnvironmentId = Env,
            LogicalName = nom,
            Role = role,
            WindowsServiceName = service,
            Server = serveur,
            ServerId = serveur?.Id ?? Guid.Empty,
            ControlMode = ControlMode.Pilotable,
            Status = LifecycleStatus.Valide
        };

    private static N4Server Serveur(string hote) => new()
    {
        Id = Guid.NewGuid(),
        EnvironmentId = Env,
        HostName = hote,
        Status = LifecycleStatus.Valide
    };

    private static DiagnosticSignature Signature(
        string code, string motif, ComponentRole? role, bool active = true) => new()
        {
            Id = Guid.NewGuid(),
            Code = code,
            Name = code,
            Pattern = motif,
            AppliesToRole = role,
            IsEnabled = active
        };

    private static readonly DiagnosticSignature[] SansSignature = [];

    // -----------------------------------------------------------------------
    // Cas où l'heuristique peut conclure
    // -----------------------------------------------------------------------

    [Fact]
    public void Un_Nom_D_Hote_Declare_Portant_Un_Seul_Composant_Suffit_A_Suggerer()
    {
        var serveur = Serveur("SRV-N4-CENTER");
        var catalogue = new[] { Composant("Center Node", ComponentRole.CenterNode, serveur: serveur) };

        var indice = OriginHeuristic.Deviner(catalogue, SansSignature,
            "2026-08-18 10:00:00 INFO [SRV-N4-CENTER] démarrage du service");

        Assert.Equal(catalogue[0].Id, indice.ComponentId);
        Assert.False(indice.Ambiguous);
        Assert.Contains("SRV-N4-CENTER", indice.Evidence);
    }

    [Fact]
    public void Le_Nom_Logique_D_Un_Composant_Declare_Suffit_A_Suggerer()
    {
        var catalogue = new[]
        {
            Composant("Bridge", ComponentRole.BridgeDaemon),
            Composant("Center Node", ComponentRole.CenterNode)
        };

        var indice = OriginHeuristic.Deviner(catalogue, SansSignature,
            "2026-08-18 10:00:00 INFO Bridge: connexion établie");

        Assert.Equal(catalogue[0].Id, indice.ComponentId);
        Assert.False(indice.Ambiguous);
    }

    [Fact]
    public void Un_Hote_Portant_Plusieurs_Composants_Est_Departage_Par_Le_Nom_Du_Composant()
    {
        // Cas réel : XPS et Bridge cohabitent sur la même machine. L'hôte seul
        // ne tranche pas, mais le nom du composant, lui, tranche.
        var serveur = Serveur("SRV-N4-XPS");
        var catalogue = new[]
        {
            Composant("XPS", ComponentRole.Xps, serveur: serveur),
            Composant("Bridge", ComponentRole.BridgeDaemon, serveur: serveur)
        };

        var indice = OriginHeuristic.Deviner(catalogue, SansSignature,
            "2026-08-18 10:00:00 INFO [SRV-N4-XPS] Bridge: heartbeat");

        Assert.Equal(catalogue[1].Id, indice.ComponentId);
        Assert.False(indice.Ambiguous);
    }

    [Fact]
    public void Une_Signature_Du_Catalogue_Rattachee_A_Un_Role_Unique_Suffit_A_Suggerer()
    {
        // Le point de FR-071 : les motifs viennent du catalogue du site, pas
        // d'une liste codée en dur. Ce que l'exploitation a appris sert aussi
        // à reconnaître ses journaux.
        var catalogue = new[]
        {
            Composant("XPS", ComponentRole.Xps),
            Composant("Center Node", ComponentRole.CenterNode)
        };
        var signatures = new[] { Signature("XPS-001", @"com\.navis\.xps\.\w+", ComponentRole.Xps) };

        var indice = OriginHeuristic.Deviner(catalogue, signatures,
            "2026-08-18 10:00:00 ERROR com.navis.xps.Dispatcher échec");

        Assert.Equal(catalogue[0].Id, indice.ComponentId);
        Assert.Contains("XPS-001", indice.Evidence);
    }

    // -----------------------------------------------------------------------
    // Cas où l'heuristique DOIT se taire
    // -----------------------------------------------------------------------

    [Fact]
    public void Deux_Composants_Nommes_Dans_Le_Meme_Journal_Ne_Suggerent_Rien()
    {
        // Un composant en cite un autre : c'est banal, et strictement non
        // concluant. Choisir le premier trouvé attribuerait le journal au
        // hasard.
        var catalogue = new[]
        {
            Composant("Bridge", ComponentRole.BridgeDaemon),
            Composant("XPS", ComponentRole.Xps)
        };

        var indice = OriginHeuristic.Deviner(catalogue, SansSignature,
            "10:00:00 INFO Bridge: perte de contact avec XPS");

        Assert.Null(indice.ComponentId);
        Assert.True(indice.Ambiguous);
        Assert.Contains("Bridge", indice.Evidence);
        Assert.Contains("XPS", indice.Evidence);
    }

    [Fact]
    public void Un_Hote_Portant_Plusieurs_Composants_Sans_Autre_Indice_Reste_Ambigu()
    {
        var serveur = Serveur("SRV-N4-XPS");
        var catalogue = new[]
        {
            Composant("XPS", ComponentRole.Xps, serveur: serveur),
            Composant("Bridge", ComponentRole.BridgeDaemon, serveur: serveur)
        };

        var indice = OriginHeuristic.Deviner(catalogue, SansSignature,
            "2026-08-18 10:00:00 INFO [SRV-N4-XPS] démarrage");

        Assert.Null(indice.ComponentId);
        Assert.True(indice.Ambiguous);
    }

    [Fact]
    public void Une_Signature_De_Role_Ne_Conclut_Pas_Si_Plusieurs_Composants_Portent_Ce_Role()
    {
        var catalogue = new[]
        {
            Composant("Cluster 1", ComponentRole.ClusterNode),
            Composant("Cluster 2", ComponentRole.ClusterNode)
        };
        var signatures = new[] { Signature("CLU-001", "cluster member evicted", ComponentRole.ClusterNode) };

        var indice = OriginHeuristic.Deviner(catalogue, signatures,
            "10:00:00 WARN cluster member evicted");

        Assert.Null(indice.ComponentId);
        // Le rôle reconnu reste une information utile même sans conclusion.
        Assert.Contains("ClusterNode", indice.Evidence);
    }

    [Fact]
    public void Deux_Signatures_Designant_Des_Roles_Differents_Ne_Suggerent_Rien()
    {
        var catalogue = new[]
        {
            Composant("XPS", ComponentRole.Xps),
            Composant("ECN4", ComponentRole.Ecn4)
        };
        var signatures = new[]
        {
            Signature("XPS-001", "dispatcher saturé", ComponentRole.Xps),
            Signature("ECN-001", "passerelle refusée", ComponentRole.Ecn4)
        };

        // Aucun nom de composant en clair : ce sont bien les signatures, et
        // elles seules, qui doivent décider — ici, ne pas décider.
        var indice = OriginHeuristic.Deviner(catalogue, signatures,
            "10:00 dispatcher saturé\n10:01 passerelle refusée");

        Assert.Null(indice.ComponentId);
        Assert.True(indice.Ambiguous);
    }

    [Fact]
    public void Un_Journal_Sans_Aucun_Indice_Ne_Suggere_Rien_Et_N_Est_Pas_Ambigu()
    {
        var catalogue = new[] { Composant("Center Node", ComponentRole.CenterNode) };

        var indice = OriginHeuristic.Deviner(catalogue, SansSignature,
            "10:00:00 INFO traitement terminé");

        Assert.Null(indice.ComponentId);
        Assert.Null(indice.Evidence);
        // « Aucun indice » et « plusieurs candidats » sont deux situations
        // différentes pour l'opérateur : elles ne doivent pas se confondre.
        Assert.False(indice.Ambiguous);
    }

    // -----------------------------------------------------------------------
    // Frontières de mots : le piège ECN4 / ECN4Web
    // -----------------------------------------------------------------------

    [Fact]
    public void ECN4_Ne_S_Attribue_Pas_Un_Journal_Qui_Ne_Parle_Que_D_ECN4Web()
    {
        var catalogue = new[] { Composant("ECN4", ComponentRole.Ecn4) };

        var indice = OriginHeuristic.Deviner(catalogue, SansSignature,
            "10:00:00 INFO ecn4web démarré sur le port 8443");

        Assert.Null(indice.ComponentId);
    }

    [Fact]
    public void ECN4_Est_Reconnu_Meme_Si_ECN4Web_Apparait_D_Abord()
    {
        // Régression : ne tester que la PREMIÈRE occurrence faisait manquer le
        // nom isolé lorsqu'un nom plus long l'avait précédé — cas courant sur
        // un journal entier plutôt que sur un nom de fichier.
        var catalogue = new[] { Composant("ECN4", ComponentRole.Ecn4) };

        var indice = OriginHeuristic.Deviner(catalogue, SansSignature,
            "10:00:00 INFO ecn4web démarré\n10:00:05 INFO ECN4 prêt");

        Assert.Equal(catalogue[0].Id, indice.ComponentId);
    }

    [Fact]
    public void Le_Nom_Le_Plus_Long_L_Emporte_Quand_Les_Deux_Apparaissent()
    {
        var catalogue = new[] { Composant("ECN4Web", ComponentRole.Ecn4Web) };

        var indice = OriginHeuristic.Deviner(catalogue, SansSignature,
            "10:00:00 INFO ECN4Web prêt");

        Assert.Equal(catalogue[0].Id, indice.ComponentId);
    }

    // -----------------------------------------------------------------------
    // Robustesse : le référentiel est saisi par des humains
    // -----------------------------------------------------------------------

    [Fact]
    public void Une_Signature_Desactivee_Est_Ignoree()
    {
        // Le journal ne doit PAS nommer le composant, sinon c'est l'autre
        // signal qui conclut et le test ne prouve rien sur les signatures.
        var catalogue = new[] { Composant("XPS", ComponentRole.Xps) };
        var signatures = new[] { Signature("XPS-001", "dispatcher indisponible", ComponentRole.Xps, active: false) };

        var indice = OriginHeuristic.Deviner(catalogue, signatures, "10:00 dispatcher indisponible");

        Assert.Null(indice.ComponentId);
    }

    [Fact]
    public void Un_Motif_De_Signature_Invalide_Ne_Fait_Pas_Echouer_L_Import()
    {
        // La suggestion est un confort ; l'import est la fonction. Une regex
        // mal saisie au référentiel ne doit pas empêcher de verser un journal
        // au moment précis où l'on en a besoin.
        var catalogue = new[] { Composant("XPS", ComponentRole.Xps) };
        var signatures = new[] { Signature("BAD-001", "[non fermé(", ComponentRole.Xps) };

        var indice = OriginHeuristic.Deviner(catalogue, signatures, "10:00 quelque chose");

        Assert.Null(indice.ComponentId);
        Assert.False(indice.Ambiguous);
    }

    [Fact]
    public void Un_Catalogue_Vide_Ou_Un_Contenu_Vide_Ne_Suggerent_Rien()
    {
        Assert.Null(OriginHeuristic.Deviner([], SansSignature, "du contenu").ComponentId);
        Assert.Null(OriginHeuristic.Deviner(
            [Composant("XPS", ComponentRole.Xps)], SansSignature, "   ").ComponentId);
    }
}
