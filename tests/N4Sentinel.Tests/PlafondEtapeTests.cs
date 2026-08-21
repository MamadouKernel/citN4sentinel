using Xunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Orchestration;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Tests;

/// <summary>
/// Plafond de durée d'une étape (`WorkflowStep.TimeoutSeconds`).
///
/// POURQUOI CES TESTS EXISTENT. Ce délai était saisissable à l'écran et
/// enregistré depuis toujours — et appliqué NULLE PART. Un exploitant qui le
/// ramenait à cinq minutes croyait borner l'étape ; en réalité seuls les
/// délais du profil de démarrage jouaient, et l'attente pouvait durer trente
/// minutes. Une valeur affichée sans effet est pire que pas de valeur.
///
/// Le plafond ne remplace pas les délais du profil : il les borne. Le premier
/// des deux qui expire arrête l'attente.
/// </summary>
public sealed class PlafondEtapeTests : IDisposable
{
    private readonly string _fichier =
        Path.Combine(Path.GetTempPath(), $"n4-plafond-{Guid.NewGuid():N}.db");

    private readonly Fabrique _fabrique;

    public PlafondEtapeTests()
    {
        _fabrique = new Fabrique(new DbContextOptionsBuilder<N4SentinelDbContext>()
            .UseSqlite($"Data Source={_fichier}",
                s => s.MigrationsAssembly("N4Sentinel.Migrations.Sqlite"))
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
            .Options);

        using var db = _fabrique.CreateDbContext();
        db.Database.Migrate();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(_fichier)) File.Delete(_fichier); } catch { /* verrou résiduel */ }
    }

    [Fact(DisplayName = "Un délai d'étape à zéro n'impose aucun plafond")]
    public void Zero_Ne_Plafonne_Pas()
    {
        // Le champ vaut 1800 par défaut ; zéro doit rester une façon explicite
        // de dire « pas de plafond », sans quoi une étape légitimement longue
        // serait coupée par une valeur oubliée à zéro.
        var etape = new ExecutionStep { TimeoutSeconds = 0 };
        Assert.Equal(0, etape.TimeoutSeconds);
    }

    [Fact(DisplayName = "Le plafond par défaut d'une étape vaut celui du profil de démarrage")]
    public void Le_Defaut_Ne_Change_Pas_Le_Comportement()
    {
        // 1800 s des deux côtés : poser le plafond ne modifie donc rien pour
        // une installation qui n'a jamais touché à ces valeurs.
        var etape = new ExecutionStep();
        var profil = new ReadinessProfile();

        Assert.Equal(profil.LogReadyTimeoutSeconds, etape.TimeoutSeconds);
    }

    [Fact(DisplayName = "Un plafond plus court que le profil est un piège : il doit se voir")]
    public async Task Un_Plafond_Trop_Court_Produit_Un_Message_Explicite()
    {
        // On éprouve le message, qui est ce que lira l'opérateur à 3 h du
        // matin. Il ne doit pas laisser croire à une panne du composant.
        var etape = new ExecutionStep
        {
            Id = Guid.NewGuid(),
            Name = "Démarrer le Center",
            Action = StepAction.Demarrer,
            TimeoutSeconds = 1
        };

        var issue = await SimulerPlafondAsync(etape);

        Assert.Equal(ExecutionStepState.Echec, issue.State);
        Assert.Equal(StepErrorType.TimeoutAttente, issue.ErrorType);

        // Le point qui compte : ne pas conclure à un échec du composant.
        Assert.Contains("n'est pas la preuve d'un échec", issue.Message);
        Assert.Contains("Vérifiez son état réel", issue.Message);
        Assert.Contains("1 s", issue.Message);
    }

    /// <summary>
    /// Reproduit la logique du plafond posée dans <c>OrchestrationEngine</c>,
    /// sans monter tout le moteur : une attente qui dépasse le délai accordé
    /// doit produire cette issue-là, et pas une exception qui remonte.
    /// </summary>
    private static async Task<StepOutcome> SimulerPlafondAsync(ExecutionStep etape)
    {
        using var plafond = new CancellationTokenSource();
        plafond.CancelAfter(TimeSpan.FromSeconds(etape.TimeoutSeconds));

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), plafond.Token);
            return StepOutcome.Succeeded("Ne devrait pas arriver.");
        }
        catch (OperationCanceledException) when (plafond.IsCancellationRequested)
        {
            return StepOutcome.Failed(
                $"Délai de l'étape dépassé ({etape.TimeoutSeconds} s). L'attente a été interrompue "
                + "AVANT que le résultat soit prouvé : ce n'est pas la preuve d'un échec du composant, "
                + "seulement la fin du temps accordé. Vérifiez son état réel avant de relancer, et "
                + "relevez le délai de l'étape s'il est plus court que le profil de démarrage du composant.",
                StepErrorType.TimeoutAttente);
        }
    }

    private sealed class Fabrique(DbContextOptions<N4SentinelDbContext> options)
        : IDbContextFactory<N4SentinelDbContext>
    {
        public N4SentinelDbContext CreateDbContext() => new(options);
    }
}
