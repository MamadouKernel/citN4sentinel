using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Connectors;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Connectivity;

/// <summary>
/// Aide au relevé du marqueur de démarrage d'un composant, à partir de son
/// journal réel.
///
/// POURQUOI CETTE FONCTION EXISTE
/// N4 Sentinel se déploie sur n'importe quel site. Le marqueur qu'écrit un
/// composant quand il a fini de s'initialiser dépend de la version de N4, du
/// composant et de la configuration de journalisation : il ne peut pas être
/// livré d'avance. Chaque exploitant doit relever le sien.
///
/// Sans cet écran, ce relevé passe par une ligne de commande. Un produit qui
/// exige un script pour devenir configurable est un produit à moitié fini.
///
/// LECTURE SEULE. Rien n'est écrit sur le serveur interrogé.
/// </summary>
public sealed class ReadinessDiscovery(
    IDbContextFactory<N4SentinelDbContext> dbFactory,
    ConnectorTargetFactory targetFactory,
    IN4Connector connector)
{
    /// <summary>Volume de journal rapatrié : suffisant pour couvrir un démarrage complet.</summary>
    private const int TailleMaximale = 512 * 1024;

    public async Task<DiscoveryResult> AnalyzeAsync(Guid componentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var composant = await db.Components
            .AsNoTracking()
            .Include(c => c.Server)
            .FirstOrDefaultAsync(c => c.Id == componentId, ct);

        if (composant is null)
            return DiscoveryResult.Failed("Composant introuvable dans le référentiel.");

        if (composant.Server is null)
            return DiscoveryResult.Failed(
                "Aucun serveur n'est associé à ce composant. Rattachez-le à un serveur avant d'analyser son journal.");

        if (string.IsNullOrWhiteSpace(composant.Readiness.LogPath))
            return DiscoveryResult.Failed(
                "Aucun chemin de journal n'est renseigné pour ce composant. " +
                "Saisissez-le d'abord : c'est le fichier que le composant écrit sur son serveur, " +
                "vu depuis ce serveur — pas un chemin réseau.");

        var resolution = await targetFactory.CreateAsync(composant.Server, ct);
        if (!resolution.Succeeded)
            return DiscoveryResult.Failed(resolution.Error!);

        var motifChemin = composant.Readiness.LogPath!;

        // Résolution du fichier : plusieurs journaux N4 sont horodatés dans leur
        // nom et recréés à chaque démarrage.
        var fichier = await connector.ResolveLogAsync(resolution.Target!, motifChemin, ct);
        if (!fichier.Succeeded)
            return DiscoveryResult.Failed($"Lecture impossible : {fichier.Error}");

        if (!fichier.Value!.Exists)
            return DiscoveryResult.Failed(
                $"Aucun fichier ne correspond à « {motifChemin} » sur {composant.Server.HostName}. " +
                "Vérifiez le chemin, et que le compte d'accès a le droit de lire ce dossier.");

        var info = fichier.Value;
        var depart = Math.Max(0, info.Length - TailleMaximale);

        var contenu = await connector.ReadLogDeltaAsync(
            resolution.Target!, info.Path!, depart, TailleMaximale, ct);

        if (!contenu.Succeeded)
            return DiscoveryResult.Failed($"Lecture impossible : {contenu.Error}");

        var lignes = contenu.Value!.Text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        var resultat = new DiscoveryResult
        {
            ResolvedPath = info.Path,
            SizeBytes = info.Length,
            LastWriteTime = info.LastWriteTime,
            LinesAnalyzed = lignes.Count,
            Truncated = depart > 0
        };

        EvaluerMotifsConfigures(composant, lignes, resultat);
        ProposerCandidats(lignes, resultat);
        ReleverErreurs(lignes, resultat);

        return resultat;
    }

    /// <summary>
    /// Les motifs déjà configurés apparaissent-ils dans ce journal ? C'est la
    /// vérification la plus directe : un motif qui ne correspond à rien laissera
    /// le composant en « à confirmer » indéfiniment.
    /// </summary>
    private static void EvaluerMotifsConfigures(
        N4Component composant, List<string> lignes, DiscoveryResult resultat)
    {
        foreach (var motif in composant.Readiness.ReadyPatterns)
        {
            if (string.IsNullOrWhiteSpace(motif)) continue;

            try
            {
                var regex = new Regex(motif, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
                var correspondances = lignes.Where(l => regex.IsMatch(l)).ToList();

                resultat.ConfiguredPatterns.Add(new PatternCheck
                {
                    Pattern = motif,
                    MatchCount = correspondances.Count,
                    LastMatch = correspondances.LastOrDefault()?.Trim()
                });
            }
            catch (ArgumentException ex)
            {
                resultat.ConfiguredPatterns.Add(new PatternCheck
                {
                    Pattern = motif, MatchCount = 0, IsInvalid = true, Error = ex.Message
                });
            }
            catch (RegexMatchTimeoutException)
            {
                resultat.ConfiguredPatterns.Add(new PatternCheck
                {
                    Pattern = motif, MatchCount = 0, IsInvalid = true,
                    Error = "Expression trop coûteuse à évaluer : elle ralentirait chaque analyse."
                });
            }
        }
    }

    /// <summary>
    /// Repère les lignes qui ressemblent à une fin d'initialisation, et propose
    /// pour chacune une expression régulière prête à l'emploi.
    /// </summary>
    private static void ProposerCandidats(List<string> lignes, DiscoveryResult resultat)
    {
        // Formulations courantes de fin d'initialisation, tous produits Java
        // confondus. La liste est volontairement large : mieux vaut proposer
        // un candidat de trop que de manquer le bon.
        string[] indices =
        [
            "servlet .*initialized", "startup in", "started in", "initialization complete",
            "initialized successfully", "ready to accept", "listening on", "bound to",
            "started successfully", "is now active", "connection established", "connected to",
            "startup complete", "server started", "deployment .*finished", "waiting for",
            "standby mode", "loading complete", "equipment loaded"
        ];

        var groupes = new Dictionary<string, CandidateMarker>(StringComparer.OrdinalIgnoreCase);

        foreach (var ligne in lignes)
        {
            if (!indices.Any(i => Regex.IsMatch(ligne, i, RegexOptions.IgnoreCase))) continue;

            var message = ExtraireMessage(ligne);
            if (string.IsNullOrWhiteSpace(message) || message.Length < 8) continue;

            // Le regroupement se fait sur le message débarrassé de ses nombres :
            // deux démarrages écrivent la même ligne avec des durées différentes.
            var cle = Regex.Replace(message, @"\d+", "#");

            if (groupes.TryGetValue(cle, out var existant))
            {
                existant.Occurrences++;
                existant.LastSeen = ligne.Trim();
            }
            else
            {
                groupes[cle] = new CandidateMarker
                {
                    Message = message,
                    SuggestedPattern = ConstruireMotif(message),
                    Occurrences = 1,
                    FirstSeen = ligne.Trim(),
                    LastSeen = ligne.Trim()
                };
            }
        }

        // Une ligne écrite une seule fois est un bon marqueur : elle signe un
        // événement unique. Une ligne répétée est un battement de cœur, et ne
        // prouve rien sur l'achèvement du démarrage.
        resultat.Candidates.AddRange(groupes.Values
            .OrderBy(c => c.Occurrences)
            .ThenByDescending(c => c.Message.Length)
            .Take(15));
    }

    private static void ReleverErreurs(List<string> lignes, DiscoveryResult resultat)
    {
        resultat.ErrorLines.AddRange(lignes
            .Where(l => Regex.IsMatch(l, @"\b(ERROR|SEVERE|FATAL|Exception|Caused by)\b", RegexOptions.IgnoreCase))
            .TakeLast(15)
            .Select(l => l.Trim()));
    }

    /// <summary>
    /// Isole le message utile d'une ligne de journal : retire l'horodatage, le
    /// niveau, le thread et le nom de classe. Sans cela, le motif proposé
    /// contiendrait une date, et ne correspondrait plus jamais.
    /// </summary>
    internal static string ExtraireMessage(string ligne)
    {
        var s = ligne.Trim();

        // Horodatage en tête, sous ses formes courantes.
        s = Regex.Replace(s, @"^\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}([.,]\d+)?\s*", "");
        s = Regex.Replace(s, @"^\d{2}[/-]\d{2}[/-]\d{4}\s+\d{2}:\d{2}:\d{2}([.,]\d+)?\s*", "");

        // Niveau, avec ou sans crochets.
        s = Regex.Replace(s, @"^\[?(INFO|WARN|WARNING|ERROR|DEBUG|TRACE|FATAL|SEVERE)\]?\s+", "",
            RegexOptions.IgnoreCase);

        // Thread et contextes entre crochets, en série.
        s = Regex.Replace(s, @"^(\[[^\]]*\]\s*)+", "");

        // Nom de classe abrégé suivi d'un tiret : « c.n.apex.WebTier - ».
        s = Regex.Replace(s, @"^[\w.$]+\s+-\s+", "");

        return s.Trim();
    }

    /// <summary>
    /// Construit l'expression régulière d'un message : parties littérales
    /// échappées, nombres remplacés par \d+.
    ///
    /// Le remplacement intervient APRÈS l'échappement. L'inverse — poser un
    /// jeton puis échapper — échoue silencieusement, l'échappement s'appliquant
    /// aussi au jeton, qui devient introuvable.
    /// </summary>
    internal static string ConstruireMotif(string message)
    {
        var echappe = Regex.Escape(message);
        return Regex.Replace(echappe, @"\d+", @"\d+");
    }
}

public sealed class DiscoveryResult
{
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
    public string? ResolvedPath { get; init; }
    public long SizeBytes { get; init; }
    public DateTimeOffset? LastWriteTime { get; init; }
    public int LinesAnalyzed { get; init; }

    /// <summary>Vrai si seule la fin du fichier a été rapatriée.</summary>
    public bool Truncated { get; init; }

    public List<PatternCheck> ConfiguredPatterns { get; } = [];
    public List<CandidateMarker> Candidates { get; } = [];
    public List<string> ErrorLines { get; } = [];

    public string? Error { get; init; }
    public bool Succeeded => Error is null;

    /// <summary>Au moins un motif configuré correspond à ce journal.</summary>
    public bool HasWorkingPattern => ConfiguredPatterns.Any(p => p.MatchCount > 0);

    public static DiscoveryResult Failed(string error) => new() { Error = error };
}

public sealed class PatternCheck
{
    public required string Pattern { get; init; }
    public int MatchCount { get; init; }
    public string? LastMatch { get; init; }
    public bool IsInvalid { get; init; }
    public string? Error { get; init; }
}

public sealed class CandidateMarker
{
    public required string Message { get; init; }
    public required string SuggestedPattern { get; init; }
    public int Occurrences { get; set; }
    public required string FirstSeen { get; init; }
    public string LastSeen { get; set; } = string.Empty;

    /// <summary>
    /// Une occurrence unique signe un événement ponctuel : c'est ce qu'on
    /// cherche. Au-delà, la ligne est périodique et ne prouve rien.
    /// </summary>
    public bool IsGoodCandidate => Occurrences == 1;
}
