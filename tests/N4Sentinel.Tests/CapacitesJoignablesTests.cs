using Xunit;
using System.Reflection;
using System.Text.RegularExpressions;
using N4Sentinel.Infrastructure.Referential;
using N4Sentinel.Infrastructure.Security;

namespace N4Sentinel.Tests;

/// <summary>
/// Toute capacité de suppression doit être joignable depuis un écran.
///
/// POURQUOI CE TEST EXISTE. Le même défaut s'est produit deux fois, et deux
/// fois il a fallu un déploiement pour le voir :
///
///   — `CredentialStore.SaveAsync` n'avait aucun appelant : impossible de
///     déclarer un compte technique, donc impossible d'atteindre un serveur N4
///     depuis un service tournant sous LocalSystem ;
///   — `ReferentialService.DeleteComponentAsync` n'avait aucun appelant :
///     impossible de supprimer un composant, donc impossible de supprimer le
///     serveur qui le porte, puisque cette suppression-là exige que le serveur
///     soit vide. Deux règles saines composées en impasse.
///
/// Les tests unitaires de ces services passaient tous : ils les appelaient
/// directement, là où l'interface n'offrait aucune porte. **Une capacité que
/// rien n'appelle n'existe pas.**
///
/// Ce test relit les écrans et confronte ce que les services savent faire à ce
/// que l'application permet réellement de déclencher.
/// </summary>
public sealed class CapacitesJoignablesTests
{
    private static string RacineWeb()
    {
        var dossier = new DirectoryInfo(AppContext.BaseDirectory);

        while (dossier is not null && !Directory.Exists(Path.Combine(dossier.FullName, "src")))
            dossier = dossier.Parent;

        Assert.NotNull(dossier);
        return Path.Combine(dossier!.FullName, "src", "N4Sentinel.Web");
    }

    private static string TousLesEcrans()
    {
        var textes = Directory
            .EnumerateFiles(RacineWeb(), "*.razor", SearchOption.AllDirectories)
            .Select(File.ReadAllText);

        return string.Join("\n", textes);
    }

    private static string RacineInfrastructure()
    {
        var dossier = new DirectoryInfo(AppContext.BaseDirectory);

        while (dossier is not null && !Directory.Exists(Path.Combine(dossier.FullName, "src")))
            dossier = dossier.Parent;

        return Path.Combine(dossier!.FullName, "src", "N4Sentinel.Infrastructure");
    }

    /// <summary>
    /// Services d'Infrastructure qu'un ecran injecte : ce sont les points
    /// d'entree legitimes. Une capacite atteinte a travers l'un d'eux est
    /// joignable, meme si l'ecran ne l'appelle pas directement.
    /// </summary>
    private static HashSet<string> ServicesInjectesDansUnEcran()
    {
        var ecrans = TousLesEcrans();

        return Regex.Matches(ecrans, @"@inject\s+(?<type>[\w\.<>]+)\s+\w+")
            .Select(m => m.Groups["type"].Value.Split('.').Last())
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Vrai si un service d'Infrastructure joignable depuis un ecran appelle
    /// cette methode. C'est le cas d'un service de facade - par exemple
    /// OperatorCredentialService, qui delegue l'effacement au magasin.
    /// </summary>
    private static bool AppeleeParUnServiceJoignable(string methode)
    {
        var injectes = ServicesInjectesDansUnEcran();
        var motif = new Regex($@"\.{methode}\s*\(", RegexOptions.Compiled);

        foreach (var fichier in Directory.EnumerateFiles(
                     RacineInfrastructure(), "*.cs", SearchOption.AllDirectories))
        {
            if (fichier.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;

            var nomClasse = Path.GetFileNameWithoutExtension(fichier);
            if (!injectes.Contains(nomClasse)) continue;

            if (motif.IsMatch(File.ReadAllText(fichier))) return true;
        }

        return false;
    }

    /// <summary>
    /// Services dont la capacité de suppression doit être exposée, avec le nom
    /// sous lequel les écrans les injectent.
    /// </summary>
    public static TheoryData<string, string> ServicesDestructeurs() => new()
    {
        { nameof(ReferentialService), "Referential" },
        { nameof(CredentialStore),    "Credentials" }
    };

    [Theory(DisplayName = "Chaque suppression offerte par un service a une porte d'entrée dans l'interface")]
    [MemberData(nameof(ServicesDestructeurs))]
    public void Toute_Suppression_Est_Joignable(string nomService, string nomInjecte)
    {
        var type = typeof(ReferentialService).Assembly.GetTypes()
            .Single(t => t.Name == nomService);

        var suppressions = type
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .Where(n => n.StartsWith("Delete", StringComparison.Ordinal)
                     || n.StartsWith("Erase", StringComparison.Ordinal)
                     || n.StartsWith("Remove", StringComparison.Ordinal))
            .Distinct()
            .ToList();

        Assert.NotEmpty(suppressions);

        var ecrans = TousLesEcrans();

        // Joignable directement depuis un ecran, OU a travers un service de
        // facade lui-meme injecte dans un ecran. Les deux comptent : ce qui
        // importe est qu'un operateur puisse declencher le geste, pas par quel
        // chemin l'appel descend.
        var orphelines = suppressions
            .Where(nom => !Regex.IsMatch(ecrans, $@"\b{nomInjecte}\.{nom}\b")
                       && !AppeleeParUnServiceJoignable(nom))
            .ToList();

        Assert.True(orphelines.Count == 0,
            $"{nomService} sait supprimer, mais rien de joignable depuis l'interface ne l'appelle pour :\n  "
            + string.Join("\n  ", orphelines)
            + "\n\nUne capacité que rien n'appelle n'existe pas : l'opérateur se retrouve "
            + "devant un référentiel qu'il ne peut plus nettoyer.");
    }

    [Fact(DisplayName = "Supprimer un environnement exige une confirmation retapée")]
    public void La_Suppression_D_Environnement_Exige_Une_Confirmation()
    {
        // Le geste emporte serveurs, composants, comptes et workflows. La
        // signature elle-meme doit rendre l'appel sans confirmation impossible :
        // un garde-fou qu'on peut oublier d'appeler n'en est pas un.
        var methode = typeof(ReferentialService).GetMethod(nameof(ReferentialService.DeleteEnvironmentAsync));

        Assert.NotNull(methode);

        var parametres = methode!.GetParameters();
        Assert.Contains(parametres, p => p.ParameterType == typeof(string) && !p.HasDefaultValue);
    }
}
