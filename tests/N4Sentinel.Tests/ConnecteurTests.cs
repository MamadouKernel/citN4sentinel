using Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using N4Sentinel.Infrastructure.Connectors;

namespace N4Sentinel.Tests;

/// <summary>
/// Tests du connecteur contre la machine locale.
///
/// Ils s'executent en deux modes : en direct dans le processus, et via WinRM
/// vers localhost. Le second est celui qui compte - c'est exactement le chemin
/// emprunte vers un serveur N4 reel, y compris la couche de transport, la
/// negociation d'authentification et la serialisation des objets.
///
/// Les tests WinRM se sautent proprement si le service n'est pas actif sur la
/// machine, plutot que d'echouer : un poste sans WinRM n'est pas un defaut du
/// code.
/// </summary>
public sealed class ConnecteurTests
{
    private static PowerShellConnector Connecteur() =>
        new(NullLogger<PowerShellConnector>.Instance);

    private static ConnectorTarget Local() => ConnectorTarget.Local();

    private static ConnectorTarget ViaWinRm() => new()
    {
        HostName = "localhost",
        WinRmPort = 5985,
        Timeout = TimeSpan.FromSeconds(60)
    };

    private static bool WinRmDisponible()
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            return client.ConnectAsync("localhost", 5985).Wait(TimeSpan.FromSeconds(3));
        }
        catch { return false; }
    }

    // -----------------------------------------------------------------------
    // Execution locale
    // -----------------------------------------------------------------------
    [SkippableFact]
    public async Task Ping_en_local_identifie_la_machine()
    {
        var r = await Connecteur().PingAsync(Local());

        Assert.True(r.Succeeded, r.Error);
        Assert.Contains(Environment.MachineName, r.Value!, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Service_inexistant_est_rapporte_introuvable_et_non_en_erreur()
    {
        // Distinction importante : un service absent du serveur est une
        // information de referentiel, pas une panne du connecteur.
        var r = await Connecteur().GetServiceAsync(Local(), "ServiceQuiNExistePas_N4Sentinel");

        Assert.True(r.Succeeded, r.Error);
        Assert.False(r.Value!.Exists);
        Assert.Equal("Introuvable", r.Value.Status);
    }

    [SkippableFact]
    public async Task Service_connu_remonte_son_etat_et_son_processus()
    {
        // Le service de gestion a distance tourne forcement si l'on peut
        // executer ce test, ce qui en fait une cible fiable.
        var r = await Connecteur().GetServiceAsync(Local(), "Winmgmt");

        Assert.True(r.Succeeded, r.Error);
        Assert.True(r.Value!.Exists);
        Assert.Equal("Running", r.Value.Status);
        Assert.NotNull(r.Value.ProcessId);
        Assert.True(r.Value.WorkingSetBytes > 0);
    }

    [SkippableFact]
    public async Task Plusieurs_services_sont_interroges_en_une_seule_connexion()
    {
        var r = await Connecteur().GetServicesAsync(Local(), ["Winmgmt", "EventLog", "ServiceAbsent_N4"]);

        Assert.True(r.Succeeded, r.Error);
        Assert.Equal(3, r.Value!.Count);
        Assert.Equal(2, r.Value.Count(s => s.Exists));
    }

    [SkippableFact]
    public async Task Le_systeme_remonte_ressources_et_ecart_d_horloge()
    {
        var r = await Connecteur().GetSystemAsync(Local());

        Assert.True(r.Succeeded, r.Error);
        var s = r.Value!;

        Assert.NotNull(s.OperatingSystem);
        Assert.NotNull(s.LastBootTime);
        Assert.True(s.TotalMemoryBytes > 0);
        Assert.NotEmpty(s.Disks);
        Assert.All(s.Disks, d => Assert.True(d.TotalBytes > 0));

        // En local, l'ecart d'horloge doit etre negligeable. C'est le controle
        // qui, sur un serveur distant, revele la cause n.1 de statuts
        // DISCONNECTED trompeurs sur N4.
        Assert.NotNull(s.ClockSkewSeconds);
        Assert.True(Math.Abs(s.ClockSkewSeconds!.Value) < 5,
            $"Ecart d'horloge local anormal : {s.ClockSkewSeconds} s");
    }

    // -----------------------------------------------------------------------
    // Lecture de journal - le mecanisme dont depend toute la preuve de demarrage
    // -----------------------------------------------------------------------
    [SkippableFact]
    public async Task La_lecture_incrementale_ne_relit_pas_les_lignes_deja_vues()
    {
        var fichier = Path.Combine(Path.GetTempPath(), $"n4-test-{Guid.NewGuid():N}.log");
        try
        {
            await File.WriteAllTextAsync(fichier,
                "2026-08-13 09:00:00 INFO Web tier servlet 'action' initialized (demarrage PRECEDENT)\r\n");

            var connecteur = Connecteur();

            // Point de reference pose apres le demarrage precedent.
            var depart = await connecteur.ResolveLogAsync(Local(), fichier);
            Assert.True(depart.Succeeded, depart.Error);
            var offset = depart.Value!.Length;

            // Rien de neuf : le marqueur precedent ne doit pas etre relu.
            var vide = await connecteur.ReadLogDeltaAsync(Local(), fichier, offset);
            Assert.True(vide.Succeeded, vide.Error);
            Assert.Equal(string.Empty, vide.Value!.Text);

            // Nouvelle ligne apres le point de reference.
            await File.AppendAllTextAsync(fichier,
                "2026-08-13 11:04:12 INFO Web tier servlet 'action' initialized\r\n");

            var delta = await connecteur.ReadLogDeltaAsync(Local(), fichier, vide.Value.NewOffset);
            Assert.True(delta.Succeeded, delta.Error);
            Assert.Contains("11:04:12", delta.Value!.Text);
            Assert.DoesNotContain("PRECEDENT", delta.Value.Text);
        }
        finally
        {
            if (File.Exists(fichier)) File.Delete(fichier);
        }
    }

    [SkippableFact]
    public async Task La_rotation_de_journal_est_detectee()
    {
        var fichier = Path.Combine(Path.GetTempPath(), $"n4-test-{Guid.NewGuid():N}.log");
        try
        {
            await File.WriteAllTextAsync(fichier, new string('x', 5000));
            var connecteur = Connecteur();

            var premier = await connecteur.ReadLogDeltaAsync(Local(), fichier, 0);
            Assert.True(premier.Succeeded, premier.Error);

            // Le journal repart de zero : cas du log XPS, recree a chaque demarrage.
            await File.WriteAllTextAsync(fichier, "nouveau fichier apres rotation\r\n");

            var apres = await connecteur.ReadLogDeltaAsync(Local(), fichier, premier.Value!.NewOffset);
            Assert.True(apres.Succeeded, apres.Error);
            Assert.True(apres.Value!.Rotated);
            Assert.Contains("rotation", apres.Value.Text);
        }
        finally
        {
            if (File.Exists(fichier)) File.Delete(fichier);
        }
    }

    [SkippableFact]
    public async Task Un_motif_generique_resout_le_fichier_le_plus_recent()
    {
        // Cas reel : le journal XPS s'appelle xps_AAAAMMJJHHMMSS et change de
        // nom a chaque demarrage.
        var dossier = Path.Combine(Path.GetTempPath(), $"n4-motif-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dossier);
        try
        {
            var ancien = Path.Combine(dossier, "xps_20260810120000.log");
            var recent = Path.Combine(dossier, "xps_20260813090000.log");

            await File.WriteAllTextAsync(ancien, "ancien demarrage\r\n");
            File.SetLastWriteTime(ancien, DateTime.Now.AddDays(-3));

            await File.WriteAllTextAsync(recent, "demarrage du jour\r\n");
            File.SetLastWriteTime(recent, DateTime.Now);

            var r = await Connecteur().ResolveLogAsync(Local(), Path.Combine(dossier, "xps_*.log"));

            Assert.True(r.Succeeded, r.Error);
            Assert.True(r.Value!.Exists);
            Assert.Equal(recent, r.Value.Path);
        }
        finally
        {
            if (Directory.Exists(dossier)) Directory.Delete(dossier, recursive: true);
        }
    }

    [SkippableFact]
    public async Task Un_journal_ouvert_en_ecriture_reste_lisible()
    {
        // Indispensable : la JVM N4 garde son journal ouvert en permanence.
        var fichier = Path.Combine(Path.GetTempPath(), $"n4-verrou-{Guid.NewGuid():N}.log");
        try
        {
            await using (var flux = new FileStream(fichier, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
            await using (var ecrivain = new StreamWriter(flux))
            {
                await ecrivain.WriteLineAsync("ligne ecrite pendant que le fichier est ouvert");
                await ecrivain.FlushAsync();

                var r = await Connecteur().ReadLogDeltaAsync(Local(), fichier, 0);

                Assert.True(r.Succeeded, r.Error);
                Assert.Contains("pendant que le fichier est ouvert", r.Value!.Text);
            }
        }
        finally
        {
            if (File.Exists(fichier)) File.Delete(fichier);
        }
    }

    [SkippableFact]
    public async Task Un_journal_absent_n_est_pas_une_erreur()
    {
        var r = await Connecteur().ReadLogDeltaAsync(
            Local(), Path.Combine(Path.GetTempPath(), "journal-qui-n-existe-pas-n4.log"), 0);

        Assert.True(r.Succeeded, r.Error);
        Assert.False(r.Value!.Exists);
    }

    // -----------------------------------------------------------------------
    // Chemin distant : identique a celui d'un serveur N4 reel
    // -----------------------------------------------------------------------
    /// <summary>
    /// Le remoting vers la machine locale exige une session elevee, en plus des
    /// droits d'administration. Sans elevation, le test ne peut pas conclure :
    /// on le saute plutot que de le declarer en echec, mais on verifie au
    /// passage que le refus est correctement qualifie.
    /// </summary>
    private static async Task<bool> RemotingUtilisableAsync()
    {
        if (!WinRmDisponible()) return false;

        var r = await Connecteur().PingAsync(ViaWinRm());
        if (r.Succeeded) return true;

        // Un refus d'acces doit etre reconnu comme tel : c'est ce qui permet a
        // l'operateur de savoir que le serveur repond mais rejette le compte.
        Assert.Equal(ConnectorFailure.AccesRefuse, r.Failure);
        return false;
    }

    [SkippableFact]
    public async Task Le_chemin_WinRM_fonctionne_de_bout_en_bout()
    {
        Skip.IfNot(await RemotingUtilisableAsync(),
            "Remoting WinRM vers localhost indisponible : une console elevee est requise. " +
            "Le refus a bien ete qualifie 'AccesRefuse'.");

        var r = await Connecteur().PingAsync(ViaWinRm());

        Assert.True(r.Succeeded, r.Error);
        Assert.Contains("PowerShell", r.Value!);
    }

    [SkippableFact]
    public async Task Un_service_est_interroge_via_WinRM()
    {
        Skip.IfNot(await RemotingUtilisableAsync(),
            "Remoting WinRM vers localhost indisponible : une console elevee est requise.");

        var r = await Connecteur().GetServiceAsync(ViaWinRm(), "Winmgmt");

        Assert.True(r.Succeeded, r.Error);
        Assert.Equal("Running", r.Value!.Status);
    }

    [SkippableFact]
    public async Task Un_hote_inexistant_produit_une_cause_exploitable()
    {
        var cible = new ConnectorTarget
        {
            HostName = "serveur-n4-inexistant.invalid",
            Timeout = TimeSpan.FromSeconds(15)
        };

        var r = await Connecteur().PingAsync(cible);

        Assert.False(r.Succeeded);
        Assert.NotEqual(ConnectorFailure.Aucune, r.Failure);
        Assert.False(string.IsNullOrWhiteSpace(r.Error));
    }
}
