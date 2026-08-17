using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace N4Sentinel.Infrastructure.Security;

/// <summary>
/// Scan antivirus au versement de documents (SEC-007), via Windows Defender
/// en ligne de commande (MpCmdRun.exe) — présent par défaut sur les serveurs
/// Windows visés par ce déploiement, sans dépendance externe à installer.
///
/// N'ARRÊTE PAS LE VERSEMENT QUAND LE MOTEUR EST INDISPONIBLE. Un
/// environnement sans Windows Defender (dev, Linux, désactivé par stratégie)
/// ne doit pas rendre le versement de documentation impossible : l'absence de
/// contrôle est signalée à l'opérateur plutôt que masquée, jamais transformée
/// en blocage silencieux. Une menace réellement détectée, elle, bloque
/// toujours le versement — sans exception.
/// </summary>
public sealed class DocumentAntivirusScanner(ILogger<DocumentAntivirusScanner> logger)
{
    private static readonly string[] CheminsCandidats =
    [
        Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\Windows Defender\MpCmdRun.exe"),
        Environment.ExpandEnvironmentVariables(@"%ProgramData%\Microsoft\Windows Defender\Platform\*\MpCmdRun.exe")
    ];

    public async Task<ScanResult> ScanAsync(byte[] contenu, CancellationToken ct = default)
    {
        var moteur = ResoudreMoteur();
        if (moteur is null)
        {
            logger.LogDebug("Aucun moteur antivirus trouvé (Windows Defender absent) : versement non scanné.");
            return ScanResult.Indisponible();
        }

        // Fichier temporaire au nom genere par l'application, jamais celui
        // fourni par l'utilisateur : aucune valeur externe n'entre dans la
        // ligne de commande.
        var cheminTemp = Path.Combine(Path.GetTempPath(), $"n4sentinel-scan-{Guid.NewGuid():N}.tmp");

        try
        {
            await File.WriteAllBytesAsync(cheminTemp, contenu, ct);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(60));

            var psi = new ProcessStartInfo
            {
                FileName = moteur,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-Scan");
            psi.ArgumentList.Add("-ScanType");
            psi.ArgumentList.Add("3");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(cheminTemp);
            psi.ArgumentList.Add("-DisableRemediation");

            using var process = Process.Start(psi);
            if (process is null) return ScanResult.Indisponible();

            var sortie = await process.StandardOutput.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);

            // MpCmdRun ne garantit pas un code de sortie distinct par
            // resultat selon les versions : le texte reste le signal fiable.
            if (sortie.Contains("Threat(s) found", StringComparison.OrdinalIgnoreCase)
                || sortie.Contains("Threats found", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("Menace détectée par Windows Defender au versement d'un document.");
                return ScanResult.Infecte(ExtraireNomMenace(sortie));
            }

            if (sortie.Contains("No threats detected", StringComparison.OrdinalIgnoreCase))
                return ScanResult.Propre();

            // Sortie ni clairement saine ni clairement infectee (erreur du
            // moteur, licence, etc.) : on le dit tel quel plutot que d'inventer
            // un verdict dans un sens ou l'autre.
            logger.LogDebug("Sortie Windows Defender non concluante : {Sortie}", sortie);
            return ScanResult.Indisponible();
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Scan antivirus interrompu (délai dépassé).");
            return ScanResult.Indisponible();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Échec de l'exécution du scan antivirus.");
            return ScanResult.Indisponible();
        }
        finally
        {
            try { if (File.Exists(cheminTemp)) File.Delete(cheminTemp); } catch { /* best effort */ }
        }
    }

    private static string? ResoudreMoteur()
    {
        foreach (var motif in CheminsCandidats)
        {
            if (!motif.Contains('*'))
            {
                if (File.Exists(motif)) return motif;
                continue;
            }

            var dossier = Path.GetDirectoryName(motif.Split('*')[0]);
            if (dossier is null || !Directory.Exists(dossier)) continue;

            var trouve = Directory.GetDirectories(dossier)
                .OrderDescending(StringComparer.Ordinal)
                .Select(d => Path.Combine(d, "MpCmdRun.exe"))
                .FirstOrDefault(File.Exists);

            if (trouve is not null) return trouve;
        }

        return null;
    }

    private static string? ExtraireNomMenace(string sortie)
    {
        var ligne = sortie.Split('\n').FirstOrDefault(l => l.Contains("Threat", StringComparison.OrdinalIgnoreCase));
        return ligne?.Trim();
    }
}

public enum ScanVerdict { Propre, Infecte, Indisponible }

public sealed record ScanResult
{
    public ScanVerdict Verdict { get; init; }
    public string? ThreatName { get; init; }

    public static ScanResult Propre() => new() { Verdict = ScanVerdict.Propre };
    public static ScanResult Infecte(string? menace) => new() { Verdict = ScanVerdict.Infecte, ThreatName = menace };
    public static ScanResult Indisponible() => new() { Verdict = ScanVerdict.Indisponible };
}
