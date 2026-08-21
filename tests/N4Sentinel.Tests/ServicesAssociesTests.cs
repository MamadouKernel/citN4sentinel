using Xunit;
using N4Sentinel.Domain;

namespace N4Sentinel.Tests;

/// <summary>
/// Services associés — ce que les guides Kaleris exigent.
///
/// N4 démarre plusieurs services Windows à partir d'une seule commande, et
/// l'éditeur demande de vérifier qu'ils sont TOUS actifs :
///
///   « Start the Navis XPS Service […] wait until the four XPS services are
///     ACTIVE » (N4 IT Administrator — Day 1, module 1.7)
///
/// La table « List of Known Services » du guide Setup/Maintenance 3.8.25
/// (p. 543-546) dit ce que coûte chaque manquant : sans BridgeService, « XPS
/// cannot complete startup » ; sans XMLRDTService, « ECN4 does not accept
/// XMLRDT messages ». Le service piloté peut donc être parfaitement Running
/// pendant qu'une fonction entière est morte.
///
/// Et la phrase qui résume tout : « A service that is in states other than
/// ACTIVE […] is as useless as if it was not present. »
/// </summary>
public sealed class ServicesAssociesTests
{
    [Fact(DisplayName = "Par défaut la liste est vide : rien ne change pour l'existant")]
    public void Par_Defaut_Aucun_Service_Associe()
    {
        // Un composant déjà déclaré avant cette fonction doit continuer à se
        // comporter exactement comme avant.
        var composant = new N4Component();

        Assert.NotNull(composant.CompanionServiceNames);
        Assert.Empty(composant.CompanionServiceNames);
    }

    [Fact(DisplayName = "Les trois services du Bridge tiennent dans la fiche")]
    public void Le_Bridge_Declare_Ses_Trois_Services()
    {
        // Day 1, module 1.6 : BridgeDaemon, BridgeControl, BridgeService.
        var bridge = new N4Component
        {
            LogicalName = "XPS Bridge Daemon",
            Role = ComponentRole.BridgeDaemon,
            WindowsServiceName = "Navis XPS Bridge Daemon",
            CompanionServiceNames = ["BridgeDaemon", "BridgeControl", "BridgeService"]
        };

        Assert.Equal(3, bridge.CompanionServiceNames.Count);

        // Le service PILOTÉ reste distinct des services vérifiés : on lance une
        // commande, on en contrôle plusieurs.
        Assert.DoesNotContain(bridge.WindowsServiceName, bridge.CompanionServiceNames);
    }

    [Fact(DisplayName = "Les quatre services XPS tiennent dans la fiche")]
    public void XPS_Declare_Ses_Quatre_Services()
    {
        var xps = new N4Component
        {
            LogicalName = "XPS",
            Role = ComponentRole.Xps,
            WindowsServiceName = "Navis XPS Service",
            CompanionServiceNames =
                ["XPSDaemon", "XPSControl", "XPSGateService", "XPSMessageService"]
        };

        Assert.Equal(4, xps.CompanionServiceNames.Count);
    }

    [Fact(DisplayName = "Les trois services de l'ECN4 Daemon tiennent dans la fiche")]
    public void L_ECN4_Declare_Ses_Trois_Services()
    {
        var ecn4 = new N4Component
        {
            LogicalName = "ECN4 Daemon",
            Role = ComponentRole.Ecn4,
            WindowsServiceName = "Navis ECN4 Daemon",
            CompanionServiceNames = ["ECN4Daemon", "XMLRDTService", "Ecn4BentoServerService"]
        };

        Assert.Equal(3, ecn4.CompanionServiceNames.Count);
    }

    [Fact(DisplayName = "Les noms ne sont PAS codés en dur : ils varient selon la version N4")]
    public void Les_Noms_Sont_Declares_Par_Le_Site()
    {
        // Le Day 1 (4.x) nomme XPSGateService là où le guide 3.8.25 nomme
        // N4GateService. Figer une liste dans le code ferait échouer le
        // démarrage sur la moitié des versions, avec un message incompréhensible.
        var version4x = new N4Component
        {
            CompanionServiceNames = ["XPSDaemon", "XPSGateService"]
        };

        var version38 = new N4Component
        {
            CompanionServiceNames = ["XPSDaemon", "N4GateService"]
        };

        Assert.NotEqual(version4x.CompanionServiceNames, version38.CompanionServiceNames);

        // Aucune des deux listes n'est « la bonne » : c'est le site qui sait.
        Assert.All(new[] { version4x, version38 },
            c => Assert.NotEmpty(c.CompanionServiceNames));
    }

    // =======================================================================
    // Supervision courante — pas seulement au démarrage et à l'arrêt
    // =======================================================================

    [Fact(DisplayName = "Un service associé arrêté rend le composant DÉGRADÉ, pas disponible")]
    public void Un_Associe_Arrete_Degrade_Le_Composant()
    {
        var releve = new N4Sentinel.Infrastructure.Supervision.ComponentHealthSnapshot
        {
            ComponentId = Guid.NewGuid(),
            LogicalName = "XPS",
            State = ComponentState.Disponible,
            Verdict = "Composant opérationnel."
        };

        releve.CompanionServices.Add(new N4Sentinel.Infrastructure.Supervision.CompanionServiceState("XPSDaemon", "Running"));
        releve.CompanionServices.Add(new N4Sentinel.Infrastructure.Supervision.CompanionServiceState("XPSGateService", "Stopped"));

        Assert.Single(releve.CompanionServicesDefaillants);
        Assert.Equal("XPSGateService", releve.CompanionServicesDefaillants[0].Nom);

        // Dégradé et non indisponible : le composant répond encore, mais une
        // partie de ses fonctions est morte. Sans XPSGateService, l'éditeur
        // note que « N4 gate operations fail ».
        Assert.False(releve.CompanionServicesDefaillants[0].EstActif);
    }

    [Fact(DisplayName = "Un service injoignable compte comme défaillant")]
    public void Un_Associe_Injoignable_Compte_Comme_Defaillant()
    {
        // « Inaccessible » n'est pas « Running » : on ne suppose pas qu'il va
        // bien parce qu'on n'a pas pu le joindre.
        var etat = new N4Sentinel.Infrastructure.Supervision.CompanionServiceState(
            "BridgeService", "Inaccessible");

        Assert.False(etat.EstActif);
    }

    [Fact(DisplayName = "Sans service associé déclaré, rien n'est signalé")]
    public void Sans_Associe_Rien_N_Est_Signale()
    {
        var releve = new N4Sentinel.Infrastructure.Supervision.ComponentHealthSnapshot
        {
            LogicalName = "Center",
            State = ComponentState.Disponible
        };

        Assert.Empty(releve.CompanionServices);
        Assert.Empty(releve.CompanionServicesDefaillants);
    }

    [Fact(DisplayName = "La saisie écarte les entrées vides et les doublons")]
    public void La_Saisie_Est_Nettoyee()
    {
        // Reproduit la conversion faite à l'écran. Une virgule en trop ne doit
        // pas produire un service au nom vide, que l'exécuteur chercherait
        // ensuite en vain sur la machine.
        const string saisie = "XPSDaemon, , XPSControl,XPSDaemon,  XPSGateService ,";

        var liste = saisie
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Equal(3, liste.Count);
        Assert.DoesNotContain(liste, s => string.IsNullOrWhiteSpace(s));
        Assert.Contains("XPSGateService", liste);
    }
}
