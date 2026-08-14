using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Diagnostic;

/// <summary>
/// Catalogue de signatures d'anomalies (FR-075, FR-065).
///
/// Il est AMORCÉ de la documentation éditeur, puis administrable. Chaque site
/// voit passer des erreurs que personne n'avait prévues ; un catalogue figé
/// cesserait d'être utile au bout de six mois.
///
/// LES SIGNATURES LIVRÉES NE SONT PAS ÉCRASÉES À CHAQUE DÉMARRAGE. Un
/// exploitant qui a corrigé une expression régulière parce qu'elle produisait
/// des faux positifs sur son site ne doit pas voir sa correction disparaître à
/// la prochaine mise à jour.
/// </summary>
public sealed class SignatureCatalogue(
    IDbContextFactory<N4SentinelDbContext> dbFactory,
    ILogger<SignatureCatalogue> logger)
{
    public async Task<List<DiagnosticSignature>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Signatures
            .AsNoTracking()
            .OrderBy(s => s.Domain).ThenByDescending(s => s.Severity).ThenBy(s => s.Code)
            .ToListAsync(ct);
    }

    public async Task<List<DiagnosticSignature>> GetActiveAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Signatures.AsNoTracking().Where(s => s.IsEnabled).ToListAsync(ct);
    }

    public async Task<DiagnosticSignature?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Signatures.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
    }

    public async Task<string?> SaveAsync(DiagnosticSignature signature, CancellationToken ct = default)
    {
        var erreur = Valider(signature);
        if (erreur is not null) return erreur;

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var existante = await db.Signatures.FirstOrDefaultAsync(s => s.Id == signature.Id, ct);

        if (existante is null)
        {
            if (await db.Signatures.AnyAsync(s => s.Code == signature.Code, ct))
                return $"Le code « {signature.Code} » est déjà utilisé.";

            db.Signatures.Add(signature);
        }
        else
        {
            existante.Name = signature.Name;
            existante.Pattern = signature.Pattern;
            existante.Domain = signature.Domain;
            existante.Severity = signature.Severity;
            existante.Meaning = signature.Meaning;
            existante.Remediation = signature.Remediation;
            existante.DocumentReference = signature.DocumentReference;
            existante.ConfidenceWeight = signature.ConfidenceWeight;
            existante.AppliesToRole = signature.AppliesToRole;
            existante.IsEnabled = signature.IsEnabled;
        }

        await db.SaveChangesAsync(ct);
        return null;
    }

    public async Task<string?> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var signature = await db.Signatures.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (signature is null) return null;

        // Une signature deja citee par des constats ne se supprime pas : le
        // rapport d'un diagnostic passe designerait alors une signature qui
        // n'existe plus. On la desactive.
        if (await db.Findings.AnyAsync(f => f.SignatureId == id, ct))
        {
            signature.IsEnabled = false;
            await db.SaveChangesAsync(ct);
            return "Cette signature a déjà servi à des diagnostics : elle a été désactivée plutôt que "
                 + "supprimée, pour que les rapports existants restent lisibles.";
        }

        db.Signatures.Remove(signature);
        await db.SaveChangesAsync(ct);
        return null;
    }

    private static string? Valider(DiagnosticSignature s)
    {
        if (string.IsNullOrWhiteSpace(s.Code)) return "Le code est obligatoire.";
        if (string.IsNullOrWhiteSpace(s.Name)) return "Le nom est obligatoire.";
        if (string.IsNullOrWhiteSpace(s.Pattern)) return "L'expression régulière est obligatoire.";

        try
        {
            _ = System.Text.RegularExpressions.Regex.Match(
                string.Empty, s.Pattern, System.Text.RegularExpressions.RegexOptions.None,
                TimeSpan.FromMilliseconds(200));
        }
        catch (ArgumentException ex)
        {
            return $"Expression régulière invalide : {ex.Message}";
        }

        if (s.ConfidenceWeight is < 1 or > 100)
            return "Le poids de confiance doit être compris entre 1 et 100.";

        // Une signature qui nomme une cause sans expliquer ce qu'elle veut dire
        // ne rend aucun service : l'operateur lit un code et reste devant sa
        // pile d'exception.
        if (s.ConfidenceWeight >= 70 && string.IsNullOrWhiteSpace(s.Meaning))
            return "Une signature de poids 70 ou plus doit expliquer ce que la ligne signifie : "
                 + "c'est ce qui lui permet de nommer une cause.";

        return null;
    }

    /// <summary>Ajoute les signatures livrées absentes du catalogue. N'écrase rien.</summary>
    public async Task<int> SeedAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var codesExistants = await db.Signatures.Select(s => s.Code).ToListAsync(ct);
        var aAjouter = Livrees().Where(s => !codesExistants.Contains(s.Code)).ToList();

        if (aAjouter.Count == 0) return 0;

        db.Signatures.AddRange(aAjouter);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("{Nombre} signature(s) de diagnostic amorcée(s).", aAjouter.Count);
        return aAjouter.Count;
    }

    /// <summary>
    /// Signatures livrées avec le produit.
    ///
    /// Chacune porte ce qu'elle SIGNIFIE, pas seulement ce qu'elle reconnaît :
    /// une pile d'exception ne dit rien à qui ne la connaît pas, et c'est
    /// précisément là qu'un outil de diagnostic apporte quelque chose.
    ///
    /// Les poids sont donnés avec prudence. Une signature générique dit qu'il y
    /// a un problème, pas lequel : elle contribue au faisceau sans autoriser à
    /// nommer une cause.
    /// </summary>
    public static List<DiagnosticSignature> Livrees() =>
    [
        // --- Base de données -------------------------------------------------
        new()
        {
            Code = "DB-CONN-REFUSED",
            Name = "Connexion à la base refusée",
            Pattern = @"(?:Connection refused|Cannot open database|Login failed for user|could not establish connection|The TCP/IP connection to the host .{0,120} has failed)",
            Domain = DiagnosticDomain.BaseDeDonnees,
            Severity = SignatureSeverity.Critique,
            Origin = SignatureOrigin.Editeur,
            ConfidenceWeight = 90,
            Meaning = "Le composant N4 n'atteint pas son instance SQL Server, ou celle-ci rejette le compte. "
                    + "Aucune initialisation applicative ne peut aboutir sans base : le composant restera "
                    + "indéfiniment en cours de démarrage.",
            Remediation = "Vérifier que le service SQL Server est démarré, que le port 1433 est joignable "
                        + "depuis ce serveur, et que le compte applicatif N4 n'est ni verrouillé ni expiré."
        },
        new()
        {
            Code = "DB-POOL-EXHAUSTED",
            Name = "Pool de connexions épuisé",
            Pattern = @"(?:connection pool|pool exhausted|Timeout expired.{0,80}pool|maximum pool size|no available connection)",
            Domain = DiagnosticDomain.BaseDeDonnees,
            Severity = SignatureSeverity.Erreur,
            Origin = SignatureOrigin.Editeur,
            ConfidenceWeight = 75,
            Meaning = "Toutes les connexions à la base sont occupées. Le symptôme se manifeste comme une "
                    + "lenteur générale ou des délais d'attente, rarement comme une panne franche — "
                    + "ce qui le rend difficile à rattacher à sa cause.",
            Remediation = "Rechercher une requête longue ou une transaction non refermée, puis examiner "
                        + "le dimensionnement du pool. Augmenter la taille sans comprendre déplace le problème."
        },
        new()
        {
            Code = "DB-DEADLOCK",
            Name = "Interblocage SQL",
            Pattern = @"(?:deadlock|was deadlocked on lock resources|transaction \(Process ID \d+\) was deadlocked)",
            Domain = DiagnosticDomain.BaseDeDonnees,
            Severity = SignatureSeverity.Erreur,
            Origin = SignatureOrigin.Editeur,
            ConfidenceWeight = 80,
            Meaning = "Deux transactions se sont bloquées mutuellement ; SQL Server en a sacrifié une. "
                    + "L'opération correspondante a échoué côté N4.",
            Remediation = "Un interblocage isolé est bénin. Répété, il signale une contention réelle : "
                        + "relever l'heure et rapprocher des traitements concurrents en cours."
        },

        // --- Cluster ---------------------------------------------------------
        new()
        {
            Code = "CLUSTER-SPLIT-BRAIN",
            Name = "Partition du cluster Hazelcast",
            Pattern = @"(?:split.?brain|Merging nodes|cluster partition|This node is not the master|master changed)",
            Domain = DiagnosticDomain.Cluster,
            Severity = SignatureSeverity.Critique,
            Origin = SignatureOrigin.Editeur,
            ConfidenceWeight = 85,
            Meaning = "Les nœuds du cluster ne se voient plus et ont formé des groupes séparés. "
                    + "Chacun se croit seul légitime. C'est l'une des rares situations où l'état affiché "
                    + "par un nœud peut être franchement faux.",
            Remediation = "Ne pas redémarrer les nœuds au hasard : cela peut aggraver la divergence. "
                        + "Identifier d'abord la coupure réseau ou l'écart d'horloge qui a provoqué la partition."
        },
        new()
        {
            Code = "CLUSTER-MEMBER-LOST",
            Name = "Perte d'un membre du cluster",
            Pattern = @"(?:Member .{0,80} left|Removing member|member removed|Connection.{0,40}lost to member)",
            Domain = DiagnosticDomain.Cluster,
            Severity = SignatureSeverity.Avertissement,
            Origin = SignatureOrigin.Editeur,
            ConfidenceWeight = 55,
            Meaning = "Un nœud a quitté le cluster. Normal lors d'un arrêt volontaire, anormal sinon.",
            Remediation = "Rapprocher de l'heure : si aucune opération n'était en cours, chercher du côté "
                        + "du réseau ou d'une pause longue du ramasse-miettes."
        },

        // --- Mémoire ---------------------------------------------------------
        new()
        {
            Code = "MEM-OUT-OF-MEMORY",
            Name = "Mémoire de la JVM épuisée",
            Pattern = @"(?:java\.lang\.OutOfMemoryError|OutOfMemoryError|GC overhead limit exceeded|Java heap space|Metaspace)",
            Domain = DiagnosticDomain.Memoire,
            Severity = SignatureSeverity.Critique,
            Origin = SignatureOrigin.Editeur,
            ConfidenceWeight = 65,
            Meaning = "La JVM n'a plus de mémoire. À partir de là, tout comportement du composant devient "
                    + "suspect : les erreurs qui suivent dans le journal sont souvent des conséquences, "
                    + "pas des causes.",
            Remediation = "Relever l'heure exacte et ne pas interpréter les messages postérieurs comme "
                        + "des pannes indépendantes. Le dimensionnement du tas se revoit après avoir compris "
                        + "ce qui l'a saturé."
        },
        new()
        {
            Code = "MEM-GC-PAUSE",
            Name = "Pause longue du ramasse-miettes",
            Pattern = @"(?:Full GC.{0,60}\d{4,} ?ms|GC pause.{0,40}\d{4,}|Total time for which application threads were stopped: [1-9]\d\.\d+ seconds)",
            Domain = DiagnosticDomain.Memoire,
            Severity = SignatureSeverity.Avertissement,
            Origin = SignatureOrigin.Editeur,
            ConfidenceWeight = 50,
            Meaning = "La JVM s'est figée plusieurs secondes. Vue de l'extérieur, la pause est indiscernable "
                    + "d'une panne réseau — et elle peut faire sortir le nœud de son cluster.",
            Remediation = "Rapprocher des pertes de membre du cluster survenues à la même heure."
        },

        // --- Réseau ----------------------------------------------------------
        new()
        {
            Code = "NET-TIMEOUT",
            Name = "Délai d'attente réseau dépassé",
            Pattern = @"(?:SocketTimeoutException|Read timed out|Connect timed out|java\.net\.ConnectException|No route to host|Network is unreachable)",
            Domain = DiagnosticDomain.Reseau,
            Severity = SignatureSeverity.Erreur,
            Origin = SignatureOrigin.Editeur,
            ConfidenceWeight = 60,
            Meaning = "Une communication réseau n'a pas abouti dans le délai imparti.",
            Remediation = "Identifier le correspondant visé dans la ligne. Un délai isolé n'est pas "
                        + "significatif ; un délai répété vers la même destination l'est."
        },
        new()
        {
            Code = "NET-UNKNOWN-HOST",
            Name = "Nom d'hôte non résolu",
            Pattern = @"(?:UnknownHostException|Name or service not known|Temporary failure in name resolution|could not resolve host)",
            Domain = DiagnosticDomain.Reseau,
            Severity = SignatureSeverity.Erreur,
            Origin = SignatureOrigin.Editeur,
            ConfidenceWeight = 85,
            Meaning = "Un nom de machine n'a pas pu être résolu. La configuration désigne un serveur "
                    + "que le DNS ne connaît pas, ou plus.",
            Remediation = "Vérifier l'orthographe du nom dans la configuration N4, le suffixe de domaine, "
                        + "et l'état du DNS depuis ce serveur précis."
        },

        // --- Licence ---------------------------------------------------------
        new()
        {
            Code = "LIC-EXPIRED",
            Name = "Licence expirée ou invalide",
            Pattern = @"(?:[Ll]icense.{0,60}(?:expired|invalid|not valid|missing)|LicenseException|no valid license)",
            Domain = DiagnosticDomain.Licence,
            Severity = SignatureSeverity.Critique,
            Origin = SignatureOrigin.Editeur,
            ConfidenceWeight = 95,
            Meaning = "N4 refuse de démarrer faute de licence valide. Le service Windows peut rester "
                    + "à l'état Running sans qu'aucune fonction applicative ne soit disponible — "
                    + "c'est exactement le cas où le statut du service induit en erreur.",
            Remediation = "Aucune action technique ne contourne ce blocage : la licence doit être renouvelée "
                        + "auprès de l'éditeur."
        },

        // --- Configuration ---------------------------------------------------
        new()
        {
            Code = "CFG-MISSING-PROPERTY",
            Name = "Propriété de configuration absente",
            Pattern = @"(?:property .{0,60} (?:not found|is required|missing)|Could not resolve placeholder|MissingResourceException|FileNotFoundException.{0,80}\.properties)",
            Domain = DiagnosticDomain.Configuration,
            Severity = SignatureSeverity.Erreur,
            Origin = SignatureOrigin.Editeur,
            ConfidenceWeight = 75,
            Meaning = "Une valeur attendue manque dans la configuration. Fréquent après une migration "
                    + "ou une montée de version, quand un fichier n'a pas été repris.",
            Remediation = "Comparer le fichier de configuration avec celui d'un environnement qui fonctionne, "
                        + "en prêtant attention aux propriétés ajoutées par la nouvelle version."
        },

        // --- Horloge ---------------------------------------------------------
        new()
        {
            Code = "CLOCK-SKEW",
            Name = "Écart d'horloge entre serveurs",
            Pattern = @"(?:clock skew|time difference|timestamp.{0,40}(?:future|past)|Clock moved backwards)",
            Domain = DiagnosticDomain.Horloge,
            Severity = SignatureSeverity.Erreur,
            Origin = SignatureOrigin.Editeur,
            ConfidenceWeight = 80,
            Meaning = "Les horloges des serveurs divergent. C'est une cause documentée de statuts N4 "
                    + "trompeurs, et une cause fréquente d'incident majeur — d'autant plus difficile à "
                    + "trouver qu'elle ne ressemble à rien de connu.",
            Remediation = "Vérifier la synchronisation NTP sur tous les serveurs de l'écosystème, "
                        + "pas seulement sur celui qui se plaint."
        },

        // --- Stockage --------------------------------------------------------
        new()
        {
            Code = "DISK-FULL",
            Name = "Espace disque insuffisant",
            Pattern = @"(?:No space left on device|disk full|not enough space|There is not enough space on the disk|IOException.{0,60}space)",
            Domain = DiagnosticDomain.Stockage,
            Severity = SignatureSeverity.Critique,
            Origin = SignatureOrigin.Editeur,
            ConfidenceWeight = 90,
            Meaning = "Le disque est plein. N4 ne peut plus écrire — y compris son propre journal, "
                    + "ce qui veut dire que la trace de l'incident peut être incomplète.",
            Remediation = "Libérer de l'espace avant toute autre action. Les erreurs suivantes dans le "
                        + "journal sont probablement des conséquences."
        },
        new()
        {
            Code = "KAHADB-CORRUPT",
            Name = "Corruption du magasin KahaDB",
            Pattern = @"(?:KahaDB|kahadb).{0,120}(?:corrupt|recover|checksum|Invalid location|unexpected)",
            Domain = DiagnosticDomain.Stockage,
            Severity = SignatureSeverity.Critique,
            Origin = SignatureOrigin.Editeur,
            ConfidenceWeight = 85,
            Meaning = "Le magasin de messages ActiveMQ est endommagé, généralement après un arrêt brutal. "
                    + "Le composant peut démarrer puis échouer à traiter les messages en attente.",
            Remediation = "Ne pas supprimer le magasin sans avoir mesuré ce qu'il contient : "
                        + "des messages métier non traités peuvent y être en attente."
        },

        // --- Intégration -----------------------------------------------------
        new()
        {
            Code = "EDI-PARSE-ERROR",
            Name = "Message EDI non interprétable",
            Pattern = @"(?:EDI.{0,60}(?:parse|invalid|malformed)|Unable to parse message|SAXParseException|Unmarshall(?:ing)? (?:error|failed))",
            Domain = DiagnosticDomain.Integration,
            Severity = SignatureSeverity.Avertissement,
            Origin = SignatureOrigin.Editeur,
            ConfidenceWeight = 60,
            Meaning = "Un message reçu d'un partenaire n'est pas conforme. Le flux concerné est bloqué, "
                    + "le reste du système fonctionne.",
            Remediation = "Identifier le partenaire et le message en cause. La correction est le plus "
                        + "souvent chez l'émetteur."
        },

        // --- Sécurité --------------------------------------------------------
        new()
        {
            Code = "SEC-AUTH-FAILED",
            Name = "Échec d'authentification répété",
            Pattern = @"(?:Authentication failed|Access denied|AccessDeniedException|Invalid credentials|401 Unauthorized|403 Forbidden)",
            Domain = DiagnosticDomain.Securite,
            Severity = SignatureSeverity.Avertissement,
            Origin = SignatureOrigin.Editeur,
            ConfidenceWeight = 55,
            Meaning = "Une authentification a été refusée. Isolé, c'est une faute de frappe ; "
                    + "répété au même instant, c'est un compte de service expiré ou verrouillé.",
            Remediation = "Compter les occurrences et relever le compte concerné. Un compte de service "
                        + "dont le mot de passe a expiré produit exactement ce motif."
        },

        // --- Applicatif ------------------------------------------------------
        new()
        {
            Code = "APP-STARTUP-FAILED",
            Name = "Échec d'initialisation applicative",
            Pattern = @"(?:Failed to start|Startup failed|Application startup aborted|Context initialization failed|SEVERE.{0,60}start)",
            Domain = DiagnosticDomain.Applicatif,
            Severity = SignatureSeverity.Critique,
            Origin = SignatureOrigin.Editeur,
            ConfidenceWeight = 70,
            Meaning = "Le composant a échoué pendant son initialisation. Le service Windows peut afficher "
                    + "Running : c'est le cas type où seul le journal dit la vérité.",
            Remediation = "Chercher la première erreur AVANT cette ligne : c'est elle qui porte la cause, "
                        + "celle-ci n'en est que la conclusion."
        },
        new()
        {
            Code = "APP-UNCAUGHT",
            Name = "Exception non rattrapée",
            Pattern = @"(?:Uncaught exception|Unhandled exception|FATAL|Exception in thread ""main"")",
            Domain = DiagnosticDomain.Applicatif,
            Severity = SignatureSeverity.Erreur,
            Origin = SignatureOrigin.Editeur,
            ConfidenceWeight = 45,
            Meaning = "Une erreur a échappé au traitement prévu. Trop générique pour désigner une cause.",
            Remediation = "Regarder le type d'exception et la pile : c'est là qu'est l'information utile."
        }
    ];
}
