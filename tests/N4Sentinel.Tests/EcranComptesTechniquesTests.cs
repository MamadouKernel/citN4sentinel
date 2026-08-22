using Xunit;
using System.Text.RegularExpressions;

namespace N4Sentinel.Tests;

/// <summary>
/// Le magasin des comptes techniques doit être joignable depuis un écran.
///
/// POURQUOI CE TEST EXISTE. `CredentialStore` était complet — chiffrement,
/// effacement, déchiffrement au dernier moment — et <see cref="ComptesTechniquesTests"/>
/// le vérifiait de bout en bout contre une vraie base. La fiche serveur
/// proposait même de choisir un compte. Mais AUCUN écran n'appelait
/// `SaveAsync` : la liste déroulante était vide sur toute installation, et la
/// seule identité possible restait celle du processus.
///
/// Le défaut n'a coûté qu'un déploiement pour se voir : le service, installé
/// sous LocalSystem, s'authentifiait comme le compte machine du serveur
/// applicatif, et les serveurs N4 refusaient la connexion — sans qu'aucun
/// écran permette d'y remédier.
///
/// Une capacité que rien n'appelle n'existe pas. Les tests du magasin
/// passaient tous : ils l'appelaient directement.
/// </summary>
public sealed class EcranComptesTechniquesTests
{
    private static string RacineWeb()
    {
        var dossier = new DirectoryInfo(AppContext.BaseDirectory);

        while (dossier is not null && !Directory.Exists(Path.Combine(dossier.FullName, "src")))
            dossier = dossier.Parent;

        Assert.NotNull(dossier);
        return Path.Combine(dossier!.FullName, "src", "N4Sentinel.Web");
    }

    private static IEnumerable<string> Ecrans()
        => Directory.EnumerateFiles(RacineWeb(), "*.razor", SearchOption.AllDirectories);

    [Theory(DisplayName = "Chaque écriture du magasin de comptes est appelée par un écran")]
    [InlineData("SaveAsync", "déclarer ou modifier un compte technique")]
    [InlineData("ClearPasswordAsync", "révoquer le mot de passe d'un compte")]
    public void Les_Ecritures_Du_Magasin_Sont_Joignables(string methode, string usage)
    {
        // On exige l'appel sur le service injecté sous le nom `Credentials`,
        // et non un `SaveAsync` quelconque : une dizaine d'écrans en ont un.
        var motif = new Regex($@"\bCredentials\.{methode}\b", RegexOptions.Compiled);

        var appelants = Ecrans()
            .Where(f => motif.IsMatch(File.ReadAllText(f)))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(appelants.Count > 0,
            $"Aucun écran n'appelle CredentialStore.{methode} : il est impossible de {usage} "
            + "depuis l'application. Le magasin fonctionne, mais personne ne peut s'en servir.");
    }

    [Fact(DisplayName = "L'écran des comptes techniques est routé sous son environnement")]
    public void L_Ecran_Est_Route()
    {
        var route = Ecrans().Any(f => File.ReadAllText(f)
            .Contains("@page \"/admin/environnements/{EnvironmentId:guid}/comptes\"", StringComparison.Ordinal));

        Assert.True(route,
            "L'écran des comptes techniques n'expose plus sa route. Les liens posés sur la fiche "
            + "environnement et sur la fiche serveur mèneraient à une page introuvable.");
    }

    [Fact(DisplayName = "Aucun écran ne restitue le mot de passe chiffré")]
    public void Le_Secret_N_Est_Jamais_Affiche()
    {
        // ProtectedPassword peut être LU pour savoir s'il existe — ce que fait
        // SecretState. Ce qui est interdit, c'est de le poser dans du balisage
        // ou dans un champ de saisie, d'où il repartirait vers le navigateur.
        var fautes = new List<string>();

        foreach (var fichier in Ecrans())
        {
            foreach (var ligne in File.ReadAllLines(fichier))
            {
                var estAffichage = Regex.IsMatch(ligne, @"@[\w.]*\.ProtectedPassword\b")
                                || Regex.IsMatch(ligne, @"@bind\s*=\s*""[^""]*ProtectedPassword");

                if (estAffichage)
                    fautes.Add($"{Path.GetFileName(fichier)} : {ligne.Trim()}");
            }
        }

        Assert.True(fautes.Count == 0,
            "Un écran expose le mot de passe chiffré au navigateur, ce que SEC-003 interdit :\n  "
            + string.Join("\n  ", fautes));
    }
}
