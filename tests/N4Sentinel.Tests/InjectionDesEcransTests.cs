using Xunit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using N4Sentinel.Infrastructure;
using System.Reflection;
using System.Text.RegularExpressions;

namespace N4Sentinel.Tests;

/// <summary>
/// Tout service injecté dans un écran doit être enregistré au conteneur.
///
/// POURQUOI CE TEST EXISTE. Un `@inject` d'un type jamais enregistré compile
/// sans rien dire : la faute n'apparaît qu'au RENDU de la page, sous forme
/// d'exception jetée à l'opérateur. Deux cas sont passés ainsi :
///
///   — SlaService, injecté par /admin/rapports, n'a jamais été enregistré ;
///   — VoiceCopilotService, enregistré en singleton, consommait un service à
///     portée de requête et empêchait l'application entière de démarrer.
///
/// Les tests unitaires ne les voyaient pas : ils construisent les services à
/// la main, sans passer par l'injection. Ce test-ci relit les écrans et
/// confronte ce qu'ils demandent à ce que le conteneur sait fournir.
/// </summary>
public sealed class InjectionDesEcransTests
{
    private const string ChaineDeConnexionFictive =
        "Server=localhost;Database=n4sentinel_inexistante;Trusted_Connection=True;TrustServerCertificate=True";

    /// <summary>`@inject Type Nom` en début de ligne.</summary>
    private static readonly Regex MotifInjection = new(
        @"^\s*@inject\s+(?<type>[^\s<>]+)\s+\w+\s*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static IConfiguration Configuration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            // Aucune connexion n'est ouverte à l'enregistrement : une chaîne
            // syntaxiquement valide suffit.
            ["ConnectionStrings:N4Sentinel"] = ChaineDeConnexionFictive
        })
        .Build();

    /// <summary>Nom court d'un type, générique compris : la trace complète est illisible.</summary>
    private static string Court(string typeComplet)
    {
        var sansAssemblage = typeComplet.Split(',')[0];
        var generique = sansAssemblage.IndexOf('[');

        if (generique < 0) return sansAssemblage.Split('.').Last();

        var racine = sansAssemblage[..generique].Split('.').Last();
        var argument = sansAssemblage[(generique + 1)..].Trim('[', ']').Split(',')[0].Split('.').Last();
        return $"{racine}<{argument}>";
    }

    private static string RacineWeb()
    {
        var dossier = new DirectoryInfo(AppContext.BaseDirectory);

        while (dossier is not null && !Directory.Exists(Path.Combine(dossier.FullName, "src")))
            dossier = dossier.Parent;

        Assert.NotNull(dossier);
        return Path.Combine(dossier!.FullName, "src", "N4Sentinel.Web");
    }

    [Fact(DisplayName = "Chaque service N4 Sentinel injecté dans un écran est enregistré")]
    public void Tout_Service_Injecte_Est_Enregistre()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddN4SentinelInfrastructure(Configuration());
        var enregistres = services.Select(d => d.ServiceType).ToHashSet();

        // On ne cherche que NOS types : les services du framework
        // (NavigationManager, UserManager, IDbContextFactory…) sont enregistrés
        // ailleurs, par ASP.NET et par Identity.
        var nosTypes = new[] { "N4Sentinel.Infrastructure", "N4Sentinel.Domain" }
            .SelectMany(nom => Assembly.Load(nom).GetTypes())
            .Where(t => t.IsClass && !t.IsAbstract)
            .GroupBy(t => t.Name)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single());

        var manquants = new List<string>();

        foreach (var fichier in Directory.EnumerateFiles(RacineWeb(), "*.razor", SearchOption.AllDirectories))
        {
            foreach (Match m in MotifInjection.Matches(File.ReadAllText(fichier)))
            {
                var nomType = m.Groups["type"].Value.Split('.').Last();

                if (!nosTypes.TryGetValue(nomType, out var type)) continue;
                if (enregistres.Contains(type)) continue;

                manquants.Add($"{Path.GetFileName(fichier)} injecte {nomType}, jamais enregistré.");
            }
        }

        Assert.True(manquants.Count == 0,
            "Des écrans injectent des services absents du conteneur — la page lèvera une "
            + "exception à l'affichage :\n  " + string.Join("\n  ", manquants.Distinct()));
    }

    [Fact(DisplayName = "Le conteneur se construit sans dépendance manquante ni conflit de portée")]
    public void Le_Conteneur_Se_Construit()
    {
        var configuration = Configuration();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(configuration);

        // Program.cs enregistre aussi Identity, la protection des données et
        // les deux expéditeurs. Sans eux, le contrôle signalerait des manques
        // qui n'existent pas dans l'application réelle — un test qui crie à
        // tort finit par ne plus être lu.
        services.AddDataProtection();
        services.AddN4SentinelInfrastructure(configuration);

        services.AddIdentityCore<N4Sentinel.Infrastructure.Identity.ApplicationUser>()
            .AddRoles<Microsoft.AspNetCore.Identity.IdentityRole>()
            .AddEntityFrameworkStores<N4Sentinel.Infrastructure.Persistence.N4SentinelDbContext>();

        // Les deux expéditeurs sont posés en DOUBLURE, et non en vrai
        // SmtpEmailSender : celui-ci réclame IWebHostEnvironment, qui n'existe
        // que dans un hôte web. Reconstituer l'hôte entier ferait de ce test
        // une copie de Program.cs, qui dériverait sans qu'on s'en aperçoive.
        //
        // Ce qu'on veut vérifier est ailleurs : que chaque service
        // d'Infrastructure trouve ses dépendances et respecte les portées.
        services.AddSingleton(
            new Moq.Mock<Microsoft.AspNetCore.Identity.IEmailSender<
                N4Sentinel.Infrastructure.Identity.ApplicationUser>>().Object);
        services.AddSingleton(
            new Moq.Mock<N4Sentinel.Infrastructure.Notifications.INotificationSender>().Object);

        // ValidateScopes + ValidateOnBuild reproduisent le contrôle que fait
        // l'hôte au démarrage. C'est exactement ce qui a refusé de démarrer
        // quand VoiceCopilotService, singleton, s'est mis à consommer
        // IDbContextFactory, à portée de requête.
        var exception = Record.Exception(() => services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }));

        if (exception is null) return;

        // On extrait la substance : la trace brute fait des dizaines de
        // milliers de caractères et se fait tronquer à l'affichage, donc
        // personne ne saurait quoi corriger.
        var texte = exception.ToString();

        var manques = Regex.Matches(texte,
                @"Unable to resolve service for type '(?<manquant>[^']+)' while attempting to activate '(?<service>[^']+)'")
            .Select(m => $"{Court(m.Groups["service"].Value)} exige {Court(m.Groups["manquant"].Value)}")
            .Distinct()
            .ToList();

        var portees = Regex.Matches(texte,
                @"Cannot consume (?<portee>\w+) service '(?<manquant>[^']+)' from (?<hote>\w+) '(?<service>[^']+)'")
            .Select(m => $"{Court(m.Groups["service"].Value)} ({m.Groups["hote"].Value}) consomme "
                       + $"{Court(m.Groups["manquant"].Value)} ({m.Groups["portee"].Value})")
            .Distinct()
            .ToList();

        Assert.Fail(
            "Le conteneur ne se construit pas — l'application ne démarrerait pas :\n  "
            + string.Join("\n  ", manques.Concat(portees).DefaultIfEmpty(texte[..Math.Min(600, texte.Length)])));
    }
}
