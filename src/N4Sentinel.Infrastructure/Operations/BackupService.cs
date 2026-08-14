using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Operations;

/// <summary>
/// Sauvegarde et restauration (NFR-010, DEP-05).
///
/// UNE SAUVEGARDE DE LA BASE SEULE NE SUFFIT PAS. Les mots de passe des comptes
/// techniques sont chiffrés par Data Protection, dont les clés vivent dans un
/// dossier séparé. Restaurer la base sans ce trousseau redonne des comptes dont
/// les secrets sont illisibles — et le constat se fait au pire moment, quand on
/// tente de redémarrer un environnement après un sinistre.
///
/// PIRE ENCORE, ET C'EST LE POINT QUE CE SERVICE EXISTE POUR RENDRE VISIBLE :
/// le trousseau est chiffré par DPAPI À L'ÉCHELLE DE LA MACHINE. Copié sur un
/// autre serveur, il est inutilisable. Une reprise sur une machine neuve
/// impose donc de ressaisir les mots de passe des comptes techniques, quelle
/// que soit la qualité de la sauvegarde. Le dire à froid coûte une phrase ;
/// le découvrir à chaud coûte une nuit.
/// </summary>
public sealed class BackupService(
    IDbContextFactory<N4SentinelDbContext> dbFactory,
    IConfiguration configuration,
    ILogger<BackupService> logger)
{
    private const string NomManifeste = "manifeste.json";

    /// <summary>Dossier du trousseau, tel que Program.cs le configure.</summary>
    public string KeyPath =>
        configuration["N4Sentinel:DataProtection:KeyPath"]
        ?? Path.Combine(AppContext.BaseDirectory, "cles-protection");

    // -----------------------------------------------------------------------
    // État
    // -----------------------------------------------------------------------
    /// <summary>Ce qu'une sauvegarde doit préserver, et ce qui serait perdu sans elle.</summary>
    public async Task<BackupStatus> GetStatusAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var comptes = await db.Credentials.CountAsync(ct);
        var comptesAvecSecret = await db.Credentials
            .CountAsync(c => c.ProtectedPassword != null && c.ProtectedPassword != "", ct);

        var fichiersCles = Directory.Exists(KeyPath)
            ? Directory.GetFiles(KeyPath, "*.xml").Length
            : 0;

        return new BackupStatus
        {
            DatabaseName = db.Database.GetDbConnection().Database,
            DataSource = db.Database.GetDbConnection().DataSource,
            KeyPath = KeyPath,
            KeyFileCount = fichiersCles,
            CredentialCount = comptes,
            CredentialsWithSecretCount = comptesAvecSecret,
            MachineName = Environment.MachineName,
            EnvironmentCount = await db.Environments.CountAsync(ct),
            ComponentCount = await db.Components.CountAsync(ct),
            ExecutionCount = await db.Executions.CountAsync(ct)
        };
    }

    // -----------------------------------------------------------------------
    // Sauvegarde
    // -----------------------------------------------------------------------
    /// <summary>
    /// Produit une sauvegarde complète : base, trousseau et manifeste.
    ///
    /// ATTENTION AU CHEMIN. La sauvegarde SQL est écrite par le moteur SQL
    /// Server, donc sur SA machine et avec SON compte de service. Un chemin qui
    /// existe sur le serveur applicatif n'existe pas forcément là-bas — et
    /// l'erreur renvoyée par SQL Server dans ce cas ne le dit pas clairement.
    /// </summary>
    public async Task<BackupResult> CreateAsync(
        string destination, string actor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(destination))
            return BackupResult.Failed("Aucun dossier de destination indiqué.");

        var etat = await GetStatusAsync(ct);
        var horodatage = DateTimeOffset.Now;
        var dossier = Path.Combine(destination, $"n4sentinel-{horodatage:yyyyMMdd-HHmmss}");

        try
        {
            Directory.CreateDirectory(dossier);
        }
        catch (Exception ex)
        {
            return BackupResult.Failed($"Le dossier « {dossier} » n'a pas pu être créé : {ex.Message}");
        }

        // --- 1. Trousseau de cles ------------------------------------------
        // Copie EN PREMIER : c'est la partie qu'on oublie, et si elle echoue,
        // mieux vaut ne pas produire une sauvegarde qui semble complete.
        var dossierCles = Path.Combine(dossier, "cles-protection");
        var clesCopiees = 0;

        try
        {
            if (!Directory.Exists(KeyPath))
                return BackupResult.Failed(
                    $"Le trousseau de clés est introuvable à « {KeyPath} ». "
                    + "Sans lui, les mots de passe des comptes techniques seraient irrécupérables : "
                    + "la sauvegarde est interrompue plutôt que produite incomplète.");

            Directory.CreateDirectory(dossierCles);

            foreach (var fichier in Directory.GetFiles(KeyPath, "*.xml"))
            {
                File.Copy(fichier, Path.Combine(dossierCles, Path.GetFileName(fichier)), overwrite: true);
                clesCopiees++;
            }
        }
        catch (Exception ex)
        {
            return BackupResult.Failed($"Copie du trousseau impossible : {ex.Message}");
        }

        if (clesCopiees == 0 && etat.CredentialsWithSecretCount > 0)
            return BackupResult.Failed(
                $"Aucun fichier de clé trouvé dans « {KeyPath} », alors que "
                + $"{etat.CredentialsWithSecretCount} compte(s) technique(s) portent un secret chiffré. "
                + "Sauvegarder la base seule les rendrait illisibles.");

        // --- 2. Base applicative -------------------------------------------
        var fichierBase = Path.Combine(dossier, $"{etat.DatabaseName}.bak");

        try
        {
            await SauvegarderBaseAsync(etat.DatabaseName, fichierBase, ct);
        }
        catch (SqlException ex)
        {
            return BackupResult.Failed(
                $"La sauvegarde SQL a échoué : {ex.Message} "
                + "Rappel : le fichier est écrit par le moteur SQL Server, sur SA machine et sous SON "
                + "compte de service. Si SQL Server est sur un autre serveur, indiquez un chemin qui "
                + "existe là-bas, ou un partage réseau auquel son compte a accès en écriture.");
        }
        catch (Exception ex)
        {
            return BackupResult.Failed($"La sauvegarde SQL a échoué : {ex.Message}");
        }

        // --- 3. Manifeste ---------------------------------------------------
        var manifeste = new BackupManifest
        {
            CreatedAt = horodatage,
            CreatedBy = actor,
            DatabaseName = etat.DatabaseName,
            DataSource = etat.DataSource,
            DatabaseFileName = Path.GetFileName(fichierBase),
            KeyFileCount = clesCopiees,
            CredentialCount = etat.CredentialCount,
            CredentialsWithSecretCount = etat.CredentialsWithSecretCount,
            MachineName = Environment.MachineName,
            EnvironmentCount = etat.EnvironmentCount,
            ComponentCount = etat.ComponentCount,
            ProductVersion = typeof(BackupService).Assembly.GetName().Version?.ToString() ?? "inconnue"
        };

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(dossier, NomManifeste),
                JsonSerializer.Serialize(manifeste, new JsonSerializerOptions { WriteIndented = true }),
                ct);

            await File.WriteAllTextAsync(Path.Combine(dossier, "LISEZ-MOI.txt"), Notice(manifeste), ct);
        }
        catch (Exception ex)
        {
            return BackupResult.Failed($"Le manifeste n'a pas pu être écrit : {ex.Message}");
        }

        logger.LogInformation(
            "Sauvegarde produite dans « {Dossier} » par {Acteur} : base {Base}, {Cles} fichier(s) de clé.",
            dossier, actor, etat.DatabaseName, clesCopiees);

        return BackupResult.Ok(dossier, manifeste, etat.CredentialsWithSecretCount > 0 && clesCopiees > 0);
    }

    private async Task SauvegarderBaseAsync(string baseDonnees, string chemin, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var chaine = db.Database.GetConnectionString();

        await using var connexion = new SqlConnection(chaine);
        await connexion.OpenAsync(ct);

        // COPY_ONLY : ne perturbe pas une chaine de sauvegardes differentielles
        // que l'exploitation aurait deja mise en place de son cote.
        // CHECKSUM : permet a RESTORE VERIFYONLY de detecter une alteration.
        var commande = connexion.CreateCommand();
        commande.CommandTimeout = 0;
        commande.CommandText =
            $"BACKUP DATABASE [{baseDonnees}] TO DISK = @chemin " +
            "WITH COPY_ONLY, INIT, CHECKSUM, FORMAT, " +
            "NAME = N'N4 Sentinel - sauvegarde applicative'";

        commande.Parameters.AddWithValue("@chemin", chemin);

        await commande.ExecuteNonQueryAsync(ct);
    }

    // -----------------------------------------------------------------------
    // Vérification
    // -----------------------------------------------------------------------
    /// <summary>
    /// Vérifie qu'une sauvegarde est restaurable.
    ///
    /// UNE SAUVEGARDE NON VÉRIFIÉE N'EST PAS UNE SAUVEGARDE. Le seul moment où
    /// l'on découvre qu'un fichier est illisible ne doit pas être celui où l'on
    /// en a besoin.
    /// </summary>
    public async Task<VerifyResult> VerifyAsync(string dossier, CancellationToken ct = default)
    {
        var controles = new List<BackupCheck>();

        if (!Directory.Exists(dossier))
            return VerifyResult.Impossible($"Le dossier « {dossier} » n'existe pas.");

        // --- Manifeste ------------------------------------------------------
        BackupManifest? manifeste = null;
        var cheminManifeste = Path.Combine(dossier, NomManifeste);

        if (!File.Exists(cheminManifeste))
        {
            controles.Add(BackupCheck.Echec("Manifeste",
                "Aucun manifeste dans ce dossier : il ne s'agit pas d'une sauvegarde produite par N4 Sentinel."));

            return new VerifyResult { Folder = dossier, Checks = controles };
        }

        try
        {
            manifeste = JsonSerializer.Deserialize<BackupManifest>(
                await File.ReadAllTextAsync(cheminManifeste, ct));

            controles.Add(BackupCheck.Reussi("Manifeste",
                $"Sauvegarde du {manifeste!.CreatedAt.ToLocalTime():dd/MM/yyyy HH:mm} "
                + $"par {manifeste.CreatedBy}, base « {manifeste.DatabaseName} »."));
        }
        catch (JsonException ex)
        {
            controles.Add(BackupCheck.Echec("Manifeste", $"Manifeste illisible : {ex.Message}"));
            return new VerifyResult { Folder = dossier, Checks = controles };
        }

        // --- Trousseau ------------------------------------------------------
        var dossierCles = Path.Combine(dossier, "cles-protection");
        var cles = Directory.Exists(dossierCles) ? Directory.GetFiles(dossierCles, "*.xml").Length : 0;

        if (cles == 0 && manifeste.CredentialsWithSecretCount > 0)
            controles.Add(BackupCheck.Echec("Trousseau de clés",
                $"Aucun fichier de clé, alors que {manifeste.CredentialsWithSecretCount} compte(s) "
                + "technique(s) portaient un secret. Ces secrets seraient irrécupérables."));
        else if (cles != manifeste.KeyFileCount)
            controles.Add(BackupCheck.Avertissement("Trousseau de clés",
                $"{cles} fichier(s) présent(s) pour {manifeste.KeyFileCount} annoncé(s) au manifeste."));
        else
            controles.Add(BackupCheck.Reussi("Trousseau de clés",
                $"{cles} fichier(s) de clé, conforme au manifeste."));

        // --- Base -----------------------------------------------------------
        var fichierBase = Path.Combine(dossier, manifeste.DatabaseFileName);

        if (!File.Exists(fichierBase))
        {
            controles.Add(BackupCheck.Echec("Sauvegarde de la base",
                $"Le fichier « {manifeste.DatabaseFileName} » est absent du dossier."));
        }
        else
        {
            var taille = new FileInfo(fichierBase).Length;

            try
            {
                await VerifierBaseAsync(fichierBase, ct);
                controles.Add(BackupCheck.Reussi("Sauvegarde de la base",
                    $"Fichier vérifié par SQL Server ({taille / 1024 / 1024} Mo), somme de contrôle valide."));
            }
            catch (SqlException ex)
            {
                controles.Add(BackupCheck.Echec("Sauvegarde de la base",
                    $"SQL Server ne peut pas lire ce fichier : {ex.Message} "
                    + "Vérifiez que le chemin est accessible depuis le moteur SQL Server."));
            }
        }

        // --- Machine d'origine ----------------------------------------------
        // LE POINT QUI COUTE UNE NUIT S'IL N'EST PAS DIT.
        if (!string.Equals(manifeste.MachineName, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            controles.Add(BackupCheck.Avertissement("Machine d'origine",
                $"Cette sauvegarde vient de « {manifeste.MachineName} », la machine courante est "
                + $"« {Environment.MachineName} ». Le trousseau est chiffré par DPAPI à l'échelle de la "
                + "machine : il ne sera PAS déchiffrable ici. La base se restaurera, mais les mots de "
                + "passe des comptes techniques devront être ressaisis."));
        else
            controles.Add(BackupCheck.Reussi("Machine d'origine",
                $"Produite sur cette même machine ({manifeste.MachineName}) : le trousseau est exploitable."));

        return new VerifyResult { Folder = dossier, Manifest = manifeste, Checks = controles };
    }

    private async Task VerifierBaseAsync(string chemin, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        await using var connexion = new SqlConnection(db.Database.GetConnectionString());
        await connexion.OpenAsync(ct);

        var commande = connexion.CreateCommand();
        commande.CommandTimeout = 0;
        commande.CommandText = "RESTORE VERIFYONLY FROM DISK = @chemin";
        commande.Parameters.AddWithValue("@chemin", chemin);

        await commande.ExecuteNonQueryAsync(ct);
    }

    // -----------------------------------------------------------------------
    private static string Notice(BackupManifest m) => $"""
        SAUVEGARDE N4 SENTINEL
        ======================

        Produite le    : {m.CreatedAt.ToLocalTime():dd/MM/yyyy à HH:mm}
        Par            : {m.CreatedBy}
        Machine        : {m.MachineName}
        Base           : {m.DatabaseName} sur {m.DataSource}
        Contenu        : {m.EnvironmentCount} environnement(s), {m.ComponentCount} composant(s)
        Comptes        : {m.CredentialCount}, dont {m.CredentialsWithSecretCount} avec secret chiffré
        Trousseau      : {m.KeyFileCount} fichier(s) de clé

        CE QUE CONTIENT CE DOSSIER
        --------------------------
        {m.DatabaseFileName}
            Sauvegarde complete de la base applicative.

        cles-protection\
            Trousseau Data Protection. Il dechiffre les mots de passe des
            comptes techniques enregistres dans la base.

        manifeste.json
            Metadonnees de controle, utilisees par la verification.

        A LIRE AVANT DE COMPTER SUR CETTE SAUVEGARDE
        --------------------------------------------
        1. LA BASE SEULE NE SUFFIT PAS. Restaurer {m.DatabaseFileName} sans le
           dossier cles-protection redonne des comptes techniques dont les mots
           de passe sont illisibles.

        2. LE TROUSSEAU EST LIE A LA MACHINE « {m.MachineName} ». Il est chiffre
           par DPAPI a l'echelle de cette machine. Sur un autre serveur, il ne
           sera pas dechiffrable : la base se restaurera normalement, mais les
           mots de passe des {m.CredentialsWithSecretCount} compte(s) technique(s)
           devront etre ressaisis depuis l'interface.

           Ce n'est pas un defaut de la sauvegarde : c'est la contrepartie du
           choix de ne stocker aucune cle en clair. Prevoyez la ressaisie dans
           votre procedure de reprise.

        3. VERIFIEZ CETTE SAUVEGARDE. L'ecran Sauvegarde de N4 Sentinel controle
           que le fichier est lisible par SQL Server et que le trousseau est
           complet. Une sauvegarde non verifiee n'est pas une sauvegarde.

        PROCEDURE DE RESTAURATION
        -------------------------
        1. Arreter le service N4 Sentinel.
        2. Restaurer la base :
           RESTORE DATABASE [{m.DatabaseName}] FROM DISK = '<chemin>\{m.DatabaseFileName}'
             WITH REPLACE, RECOVERY;
        3. Copier le contenu de cles-protection\ vers le dossier de cles
           configure (N4Sentinel:DataProtection:KeyPath).
        4. Redemarrer le service.
        5. Ouvrir l'ecran des comptes techniques et verifier qu'aucun ne porte
           l'etat « secret illisible ». Si c'est le cas, ressaisir le mot de
           passe concerne.
        """;
}

public sealed record BackupManifest
{
    public DateTimeOffset CreatedAt { get; init; }
    public string CreatedBy { get; init; } = string.Empty;
    public string DatabaseName { get; init; } = string.Empty;
    public string DataSource { get; init; } = string.Empty;
    public string DatabaseFileName { get; init; } = string.Empty;
    public int KeyFileCount { get; init; }
    public int CredentialCount { get; init; }
    public int CredentialsWithSecretCount { get; init; }
    public string MachineName { get; init; } = string.Empty;
    public int EnvironmentCount { get; init; }
    public int ComponentCount { get; init; }
    public string ProductVersion { get; init; } = string.Empty;
}

public sealed record BackupStatus
{
    public string DatabaseName { get; init; } = string.Empty;
    public string DataSource { get; init; } = string.Empty;
    public string KeyPath { get; init; } = string.Empty;
    public int KeyFileCount { get; init; }
    public int CredentialCount { get; init; }
    public int CredentialsWithSecretCount { get; init; }
    public string MachineName { get; init; } = string.Empty;
    public int EnvironmentCount { get; init; }
    public int ComponentCount { get; init; }
    public int ExecutionCount { get; init; }

    /// <summary>
    /// Vrai si le trousseau est absent alors que des secrets en dépendent.
    /// Cet état est grave et silencieux : rien ne le signale au quotidien.
    /// </summary>
    public bool KeyRingMissing => KeyFileCount == 0 && CredentialsWithSecretCount > 0;
}

public enum BackupOutcome { Reussi = 0, Avertissement = 1, Echec = 2 }

public sealed record BackupCheck
{
    public string Name { get; init; } = string.Empty;
    public BackupOutcome Outcome { get; init; }
    public string Detail { get; init; } = string.Empty;

    public static BackupCheck Reussi(string n, string d) =>
        new() { Name = n, Outcome = BackupOutcome.Reussi, Detail = d };

    public static BackupCheck Avertissement(string n, string d) =>
        new() { Name = n, Outcome = BackupOutcome.Avertissement, Detail = d };

    public static BackupCheck Echec(string n, string d) =>
        new() { Name = n, Outcome = BackupOutcome.Echec, Detail = d };
}

public sealed record BackupResult
{
    public bool Succeeded { get; init; }
    public string? Folder { get; init; }
    public BackupManifest? Manifest { get; init; }

    /// <summary>Vrai si des secrets sont sauvegardés ET leur trousseau avec.</summary>
    public bool SecretsPreserved { get; init; }

    public string? Error { get; init; }

    public static BackupResult Ok(string dossier, BackupManifest m, bool secrets) =>
        new() { Succeeded = true, Folder = dossier, Manifest = m, SecretsPreserved = secrets };

    public static BackupResult Failed(string error) => new() { Succeeded = false, Error = error };
}

public sealed record VerifyResult
{
    public string Folder { get; init; } = string.Empty;
    public BackupManifest? Manifest { get; init; }
    public List<BackupCheck> Checks { get; init; } = [];
    public string? Error { get; init; }

    public int Failures => Checks.Count(c => c.Outcome == BackupOutcome.Echec);
    public int Warnings => Checks.Count(c => c.Outcome == BackupOutcome.Avertissement);

    /// <summary>La sauvegarde est restaurable. Des réserves peuvent subsister.</summary>
    public bool IsRestorable => Error is null && Failures == 0;

    public static VerifyResult Impossible(string error) => new() { Error = error };
}
