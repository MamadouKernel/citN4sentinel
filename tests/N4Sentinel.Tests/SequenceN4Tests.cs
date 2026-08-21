using Xunit;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Orchestration;

namespace N4Sentinel.Tests;

/// <summary>
/// Ordres de démarrage et d'arrêt N4, tels que les prescrivent les guides
/// Kaleris — « N4 IT Administrator, Day 1 : Installation, Startup » (2024) et
/// « N4 Setup, Maintenance, and System Diagnostics », module 1.8.
///
/// POURQUOI CES TESTS EXISTENT. Ces ordres ne tenaient jusqu'ici que par un
/// commentaire. Une refonte de la génération de séquences pouvait les inverser
/// sans que rien ne le signale : la suite serait restée verte, et le défaut ne
/// se serait vu qu'un dimanche, sur un terminal réel, au milieu d'un arrêt
/// complet.
///
/// L'éditeur classe « improper shutdown and restart process » parmi les dix
/// premières causes d'incident critique. Ce n'est pas une préférence de style.
/// </summary>
public sealed class SequenceN4Tests
{
    private static int Demarrage(ComponentRole r) => WorkflowService.RangDemarrage(r);
    private static int Arret(ComponentRole r) => WorkflowService.RangArret(r);

    // =======================================================================
    // Démarrage — Day 1, module « N4 Startup Process »
    // =======================================================================

    [Fact(DisplayName = "Démarrage : Cluster AVANT Center, Standby APRÈS Center")]
    public void L_Ordre_De_Demarrage_Suit_Le_Guide_Day_1()
    {
        // Cluster → Center → Standby → Bridge → XPS → ECN4 → ECN4Web
        Assert.True(Demarrage(ComponentRole.ClusterNode) < Demarrage(ComponentRole.CenterNode),
            "Les nœuds Cluster démarrent avant le Center.");

        Assert.True(Demarrage(ComponentRole.CenterNode) < Demarrage(ComponentRole.StandbyCenterNode),
            "Le Standby démarre après le Center.");

        Assert.True(Demarrage(ComponentRole.StandbyCenterNode) < Demarrage(ComponentRole.BridgeDaemon));
        Assert.True(Demarrage(ComponentRole.BridgeDaemon) < Demarrage(ComponentRole.Xps),
            "XPS ne démarre pas sans son Bridge.");
        Assert.True(Demarrage(ComponentRole.Xps) < Demarrage(ComponentRole.Ecn4));
        Assert.True(Demarrage(ComponentRole.Ecn4) < Demarrage(ComponentRole.Ecn4Web));
    }

    [Fact(DisplayName = "Démarrage : l'infrastructure précède tout composant N4")]
    public void L_Infrastructure_Demarre_En_Premier()
    {
        // Une base ou une file de messages absente fait échouer le premier
        // nœud, et l'échec ne dit pas que la cause est ailleurs.
        foreach (var socle in new[]
                 {
                     ComponentRole.BaseDeDonnees,
                     ComponentRole.ActiveMq,
                     ComponentRole.DossierPartage
                 })
        {
            Assert.True(Demarrage(socle) < Demarrage(ComponentRole.ClusterNode),
                $"{socle} doit démarrer avant les nœuds Cluster.");
        }
    }

    // =======================================================================
    // Arrêt — module 1.8, « N4 Shutdown Process »
    // =======================================================================

    [Fact(DisplayName = "Arrêt : ECN4Web → ECN4 → XPS → Bridge → Standby → Cluster → Center")]
    public void L_Ordre_D_Arret_Suit_Le_Module_1_8()
    {
        Assert.True(Arret(ComponentRole.Ecn4Web) < Arret(ComponentRole.Ecn4));
        Assert.True(Arret(ComponentRole.Ecn4) < Arret(ComponentRole.Xps));
        Assert.True(Arret(ComponentRole.Xps) < Arret(ComponentRole.BridgeDaemon));
        Assert.True(Arret(ComponentRole.BridgeDaemon) < Arret(ComponentRole.StandbyCenterNode));
        Assert.True(Arret(ComponentRole.StandbyCenterNode) < Arret(ComponentRole.ClusterNode));
        Assert.True(Arret(ComponentRole.ClusterNode) < Arret(ComponentRole.CenterNode));
    }

    [Fact(DisplayName = "L'arrêt n'est PAS l'inverse du démarrage : les deux inversions du guide")]
    public void L_Arret_N_Est_Pas_L_Inverse_Du_Demarrage()
    {
        // C'est le piège que le guide signale, et la faute qu'un développeur
        // pressé commettrait en écrivant « ordre inverse ».

        // 1. Le Standby s'arrête AVANT le Center, alors qu'il démarre APRÈS.
        Assert.True(Demarrage(ComponentRole.CenterNode) < Demarrage(ComponentRole.StandbyCenterNode));
        Assert.True(Arret(ComponentRole.StandbyCenterNode) < Arret(ComponentRole.CenterNode));

        // 2. Les Cluster s'arrêtent AVANT le Center, alors qu'ils démarrent
        //    AVANT lui également. Un simple renversement les ferait partir
        //    après.
        Assert.True(Demarrage(ComponentRole.ClusterNode) < Demarrage(ComponentRole.CenterNode));
        Assert.True(Arret(ComponentRole.ClusterNode) < Arret(ComponentRole.CenterNode));
    }

    [Fact(DisplayName = "Le Center Node est le DERNIER composant N4 arrêté")]
    public void Le_Center_Part_En_Dernier()
    {
        // Il porte la file de travail et la connexion à la base : le couper
        // d'abord priverait les nœuds de ce dont ils ont besoin pour se fermer
        // proprement — sauver leurs données, fermer leurs connexions.
        foreach (var role in new[]
                 {
                     ComponentRole.Ecn4Web, ComponentRole.Ecn4, ComponentRole.Xps,
                     ComponentRole.BridgeDaemon, ComponentRole.StandbyCenterNode,
                     ComponentRole.ClusterNode
                 })
        {
            Assert.True(Arret(role) < Arret(ComponentRole.CenterNode),
                $"{role} doit s'arrêter avant le Center Node.");
        }
    }

    [Fact(DisplayName = "Arrêt : l'infrastructure s'éteint après tous les composants N4")]
    public void L_Infrastructure_S_Arrete_En_Dernier()
    {
        foreach (var socle in new[]
                 {
                     ComponentRole.ActiveMq,
                     ComponentRole.DossierPartage,
                     ComponentRole.BaseDeDonnees
                 })
        {
            Assert.True(Arret(ComponentRole.CenterNode) < Arret(socle),
                $"{socle} doit s'arrêter après le Center Node.");
        }
    }

    // =======================================================================
    // Cohérence de la table
    // =======================================================================

    [Fact(DisplayName = "Chaque rôle N4 a un rang explicite, aucun ne tombe dans le défaut")]
    public void Aucun_Role_N4_Ne_Tombe_Dans_Le_Defaut()
    {
        // Le cas par défaut vaut 50 : un rôle qui y tomberait se retrouverait
        // placé au hasard dans la séquence, sans que rien ne le signale.
        ComponentRole[] rolesN4 =
        [
            ComponentRole.BaseDeDonnees, ComponentRole.ActiveMq, ComponentRole.DossierPartage,
            ComponentRole.ClusterNode, ComponentRole.CenterNode, ComponentRole.StandbyCenterNode,
            ComponentRole.BridgeDaemon, ComponentRole.Xps, ComponentRole.Ecn4,
            ComponentRole.Ecn4Web, ComponentRole.InterfaceEdi
        ];

        foreach (var role in rolesN4)
        {
            Assert.True(Demarrage(role) < 50, $"{role} n'a pas de rang de démarrage explicite.");
            Assert.True(Arret(role) < 50, $"{role} n'a pas de rang d'arrêt explicite.");
        }
    }

    [Fact(DisplayName = "Aucun ex æquo : deux composants ne peuvent pas revendiquer le même rang")]
    public void Les_Rangs_Sont_Distincts()
    {
        ComponentRole[] rolesN4 =
        [
            ComponentRole.BaseDeDonnees, ComponentRole.ActiveMq, ComponentRole.DossierPartage,
            ComponentRole.ClusterNode, ComponentRole.CenterNode, ComponentRole.StandbyCenterNode,
            ComponentRole.BridgeDaemon, ComponentRole.Xps, ComponentRole.Ecn4,
            ComponentRole.Ecn4Web, ComponentRole.InterfaceEdi
        ];

        Assert.Equal(rolesN4.Length, rolesN4.Select(Demarrage).Distinct().Count());
        Assert.Equal(rolesN4.Length, rolesN4.Select(Arret).Distinct().Count());
    }
}
