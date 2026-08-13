using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure;
using N4Sentinel.Infrastructure.Identity;
using N4Sentinel.Infrastructure.Persistence;
using N4Sentinel.Web.Components;
using N4Sentinel.Web.Components.Account;
using Microsoft.AspNetCore.DataProtection;
using N4Sentinel.Web.Security;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Journalisation structuree (NFR-006, NFR-008)
// ---------------------------------------------------------------------------
// Console pour l'exploitation courante, fichier journalier conserve 30 jours
// pour l'analyse a posteriori. Chaque ligne porte un identifiant de correlation
// permettant de relier les evenements d'une meme operation.
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "N4Sentinel")
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        path: Path.Combine(AppContext.BaseDirectory, "logs", "n4sentinel-.log"),
        rollingInterval: Serilog.RollingInterval.Day,
        retainedFileCountLimit: 30,
        outputTemplate:
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}"));

// ---------------------------------------------------------------------------
// Interface
// ---------------------------------------------------------------------------
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// ---------------------------------------------------------------------------
// Protection des donnees : trousseau de cles du chiffrement des secrets
// ---------------------------------------------------------------------------
// Sans configuration explicite, ASP.NET Core range ses cles dans le profil de
// l'utilisateur courant. Sous un compte de service dont le profil n'est pas
// charge - cas d'un service Windows ou d'IIS -, elles sont regenerees a chaque
// demarrage : les mots de passe des comptes techniques deviendraient alors
// indechiffrables du jour au lendemain, sans message d'erreur explicite.
//
// On les persiste donc dans un dossier connu, chiffre par DPAPI a l'echelle
// de la machine. CE DOSSIER DOIT ETRE SAUVEGARDE au meme titre que la base :
// le perdre oblige a ressaisir tous les mots de passe enregistres.
var dossierCles = builder.Configuration["N4Sentinel:DataProtection:KeyPath"]
                  ?? Path.Combine(builder.Environment.ContentRootPath, "cles-protection");

var protection = builder.Services
    .AddDataProtection()
    .SetApplicationName("N4Sentinel")
    .PersistKeysToFileSystem(new DirectoryInfo(dossierCles));

if (OperatingSystem.IsWindows())
    protection.ProtectKeysWithDpapi(protectToLocalMachine: true);

builder.Services.AddCascadingAuthenticationState();

// Contexte d'environnement, par circuit Blazor : deux operateurs peuvent
// travailler simultanement sur deux environnements sans interference.
builder.Services.AddScoped<N4Sentinel.Web.State.CurrentEnvironmentState>();

// Amorcage : singleton, car l'etat "un compte existe" ne redevient jamais faux
// et n'a pas a etre reinterroge a chaque requete.
builder.Services.AddSingleton<N4Sentinel.Web.State.FirstRunState>();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

// ---------------------------------------------------------------------------
// Persistance, referentiel et audit
// ---------------------------------------------------------------------------
builder.Services.AddN4SentinelInfrastructure(builder.Configuration);

// L'acteur reel remplace le contexte "systeme" enregistre par l'Infrastructure :
// c'est lui qui apparaitra dans le journal d'audit.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUserContext, HttpCurrentUserContext>();

builder.Services.AddDatabaseDeveloperPageExceptionFilter();

// ---------------------------------------------------------------------------
// Authentification et habilitations
// ---------------------------------------------------------------------------
builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddIdentityCookies();

builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        options.SignIn.RequireConfirmedAccount = true;
        options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;

        // Au-dela du defaut d'Identity, la longueur minimale passe a 12
        // caracteres : ces comptes peuvent arreter la Production.
        options.Password.RequiredLength = 12;
        options.Password.RequireDigit = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireNonAlphanumeric = true;

        // Verrouillage apres tentatives infructueuses. Chaque echec est
        // journalise : un echec d'autorisation est une information de
        // securite, pas un non-evenement (SEC-008).
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);

        options.User.RequireUniqueEmail = true;
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<N4SentinelDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

builder.Services.AddN4SentinelAuthorization();

// Messagerie : confirmation de compte, reinitialisation et code de second
// facteur (SEC-001). Sans serveur configure, l'expediteur journalise au lieu
// d'envoyer, et le signale a chaque appel.
var smtpOptions = builder.Configuration.GetSection(SmtpOptions.SectionName).Get<SmtpOptions>() ?? new SmtpOptions();
builder.Services.AddSingleton(smtpOptions);
builder.Services.AddSingleton<SmtpEmailSender>();
builder.Services.AddSingleton<IEmailSender<ApplicationUser>>(sp => sp.GetRequiredService<SmtpEmailSender>());

var app = builder.Build();

// ---------------------------------------------------------------------------
// Amorcage : migrations en attente, roles, premier administrateur
// ---------------------------------------------------------------------------
using (var scope = app.Services.CreateScope())
{
    var seeder = scope.ServiceProvider.GetRequiredService<DatabaseSeeder>();
    await seeder.SeedAsync();
}

// ---------------------------------------------------------------------------
// Pipeline
// ---------------------------------------------------------------------------
if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseSerilogRequestLogging();

// Tant qu'aucun compte n'existe, toute navigation aboutit au parcours de
// premier demarrage. Sans cela, une installation neuve presente un ecran de
// connexion sur lequel personne ne peut se connecter, sans rien indiquer.
// Le controle s'arrete des qu'un compte a ete vu : aucun cout ensuite.
app.Use(async (context, next) =>
{
    var chemin = context.Request.Path;

    var exempte = chemin.StartsWithSegments("/premier-demarrage")
                  || chemin.StartsWithSegments("/_framework")
                  || chemin.StartsWithSegments("/_content")
                  || chemin.StartsWithSegments("/lib")
                  || chemin.StartsWithSegments("/Error")
                  || Path.HasExtension(chemin.Value);

    if (!exempte)
    {
        var amorcage = context.RequestServices.GetRequiredService<N4Sentinel.Web.State.FirstRunState>();
        if (await amorcage.NeedsSetupAsync(context.RequestAborted))
        {
            context.Response.Redirect("/premier-demarrage");
            return;
        }
    }

    await next();
});
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapAdditionalIdentityEndpoints();

app.Run();
