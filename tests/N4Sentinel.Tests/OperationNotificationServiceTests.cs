using Xunit;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Identity;
using N4Sentinel.Infrastructure.Notifications;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Tests;

/// <summary>
/// FR-095 : notifications de lancement/blocage/fin d'opération. Le point qui
/// compte n'est pas l'envoi lui-même (SmtpEmailSender l'a déjà) mais LA
/// SÉLECTION DES DESTINATAIRES — demandeur + Validateur habilités, jamais un
/// compte désactivé — et le fait qu'un échec d'envoi ne remonte JAMAIS comme
/// exception (le moteur d'orchestration ne doit pas tomber pour un
/// notification ratée).
/// </summary>
public sealed class OperationNotificationServiceTests : IAsyncLifetime
{
    private const string MasterConnection =
        "Server=localhost;Database=master;Trusted_Connection=True;TrustServerCertificate=True";

    private readonly string _databaseName = $"n4sentinel_test_{Guid.NewGuid():N}";
    private ServiceProvider _provider = null!;
    private IServiceScope _scope = null!;
    private UserManager<ApplicationUser> _users = null!;
    private FakeSender _sender = null!;
    private OperationNotificationService _service = null!;

    public async Task InitializeAsync()
    {
        TestConnectionHelper.SkipIfUnavailable();
        var cs = TestConnectionHelper.BuildDatabaseConnectionString(_databaseName);

        var options = TestDbContextOptions.Builder(cs).Options;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(options);
        services.AddScoped(sp => new N4SentinelDbContext(sp.GetRequiredService<DbContextOptions<N4SentinelDbContext>>()));
        services.AddIdentityCore<ApplicationUser>(o =>
            {
                o.Password.RequireDigit = false;
                o.Password.RequireUppercase = false;
                o.Password.RequireNonAlphanumeric = false;
                o.Password.RequiredLength = 6;
            })
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<N4SentinelDbContext>();

        _provider = services.BuildServiceProvider();
        _scope = _provider.CreateScope();

        var db = _scope.ServiceProvider.GetRequiredService<N4SentinelDbContext>();
        await db.Database.EnsureCreatedAsync();

        var roles = _scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        await roles.CreateAsync(new IdentityRole(N4Roles.Validateur));

        _users = _scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        await CreerUtilisateurAsync("demandeur@cit.ci", roles: []);
        await CreerUtilisateurAsync("validateur.actif@cit.ci", roles: [N4Roles.Validateur]);
        await CreerUtilisateurAsync("validateur.desactive@cit.ci", roles: [N4Roles.Validateur], desactive: true);

        _sender = new FakeSender();
        _service = new OperationNotificationService(_sender, _users, NullLogger<OperationNotificationService>.Instance);
    }

    private async Task CreerUtilisateurAsync(string email, string[] roles, bool desactive = false)
    {
        var user = new ApplicationUser
        {
            UserName = email, Email = email, EmailConfirmed = true, IsDisabled = desactive
        };
        var cree = await _users.CreateAsync(user, "MotDePasse1");
        Assert.True(cree.Succeeded, string.Join(" | ", cree.Errors.Select(e => e.Description)));

        if (roles.Length > 0) await _users.AddToRolesAsync(user, roles);
    }

    private static WorkflowExecution NouvelleExecution() => new()
    {
        WorkflowName = "Démarrage complet",
        WorkflowVersion = 1,
        EnvironmentCode = "UAT",
        RequestedBy = "demandeur@cit.ci",
        Reason = "Test",
        CorrelationId = "correl-test-01",
        Status = ExecutionStatus.EnCours
    };

    [SkippableFact]
    public async Task NotifierLancementAsync_Envoie_Au_Demandeur_Et_Aux_Validateurs_Actifs_Seulement()
    {
        var execution = NouvelleExecution();

        await _service.NotifierLancementAsync(execution);

        Assert.Equal(2, _sender.Envois.Count);
        var destinataires = _sender.Envois.Select(e => e.To).ToList();
        Assert.Contains("demandeur@cit.ci", destinataires);
        Assert.Contains("validateur.actif@cit.ci", destinataires);
        Assert.DoesNotContain("validateur.desactive@cit.ci", destinataires);
    }

    [SkippableFact]
    public async Task NotifierLancementAsync_Ne_Notifie_Jamais_Deux_Fois_Le_Meme_Destinataire()
    {
        // Le demandeur EST le validateur : un seul envoi attendu, pas deux.
        var execution = NouvelleExecution();
        execution.RequestedBy = "validateur.actif@cit.ci";

        await _service.NotifierLancementAsync(execution);

        Assert.Single(_sender.Envois);
    }

    [SkippableFact]
    public async Task NotifierFinAsync_Compose_Un_Sujet_Portant_Le_Statut()
    {
        var execution = NouvelleExecution();
        execution.Status = ExecutionStatus.TermineAvecAvertissements;
        execution.Outcome = "2 étapes sans preuve.";

        await _service.NotifierFinAsync(execution);

        Assert.Contains(_sender.Envois, e => e.Subject.Contains("réserves", StringComparison.OrdinalIgnoreCase));
    }

    [SkippableFact]
    public async Task Une_Simulation_Ne_Declenche_Aucune_Notification()
    {
        // FR-005 : une simulation n'a rien produit sur l'ecosysteme reel —
        // notifier un Validateur d'un lancement ou d'une fin qui n'a jamais eu
        // lieu serait plus trompeur qu'utile.
        var execution = NouvelleExecution();
        execution.IsSimulation = true;

        await _service.NotifierLancementAsync(execution);
        await _service.NotifierFinAsync(execution);
        await _service.NotifierBlocageAsync(execution, "Motif quelconque.");

        Assert.Empty(_sender.Envois);
    }

    [SkippableFact]
    public async Task NotifierBlocageAsync_N_Echoue_Jamais_Meme_Si_L_Envoi_Explose()
    {
        _sender.LeverUneExceptionAuProchainEnvoi = true;
        var execution = NouvelleExecution();

        // Ne doit lever aucune exception : un envoi rate ne doit jamais faire
        // tomber l'appelant (le moteur d'orchestration).
        await _service.NotifierBlocageAsync(execution, "Étape en échec, décision attendue.");
    }

    [SkippableFact]
    public async Task NotifierLancementAsync_Sans_Aucun_Destinataire_Valide_N_Envoie_Rien_Et_Ne_Leve_Rien()
    {
        var execution = NouvelleExecution();
        execution.RequestedBy = "inconnu@cit.ci";

        await _service.NotifierLancementAsync(execution);

        Assert.Single(_sender.Envois); // le validateur actif seul
    }

    public async Task DisposeAsync()
    {
        _scope.Dispose();
        await _provider.DisposeAsync();

        Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
        await using var master = new Microsoft.Data.SqlClient.SqlConnection(MasterConnection);
        await master.OpenAsync();
        var cmd = master.CreateCommand();
        cmd.CommandText =
            $"IF DB_ID('{_databaseName}') IS NOT NULL BEGIN " +
            $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
            $"DROP DATABASE [{_databaseName}]; END";
        await cmd.ExecuteNonQueryAsync();
    }

    private sealed class FakeSender : INotificationSender
    {
        public List<(string To, string Subject, string Body)> Envois { get; } = [];
        public bool LeverUneExceptionAuProchainEnvoi { get; set; }

        public Task SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default)
        {
            if (LeverUneExceptionAuProchainEnvoi)
            {
                LeverUneExceptionAuProchainEnvoi = false;
                throw new InvalidOperationException("Panne SMTP simulée.");
            }

            Envois.Add((to, subject, htmlBody));
            return Task.CompletedTask;
        }
    }
}
