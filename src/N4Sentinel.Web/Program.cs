using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
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

// L'en-tete Server annonce la technologie a qui cartographie le parc (audit
// SEC-A9). L'effet est modeste, le cout nul.
builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);

// LIMITATION DE DEBIT (audit SEC-A8).
//
// Le verrouillage de compte traite deja la force brute sur un compte donne -
// cinq tentatives, quinze minutes. Ce qu'il ne traite pas : l'enumeration de
// comptes, et la saturation par des operations couteuses (versement d'un PDF
// de 40 Mo, analyse d'un journal de 2 Mo, releve Windows Update).
//
// Les seuils sont larges a dessein. Une limitation qui gene l'exploitation un
// jour d'incident serait desactivee le lendemain, et ne protegerait plus rien.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Par adresse : suffisant sur un reseau d'exploitation, ou chaque poste
    // est identifie. Les ressources statiques et le circuit Blazor en sont
    // exclus, sinon la moindre page en consommerait le quota.
    options.GlobalLimiter = System.Threading.RateLimiting.PartitionedRateLimiter.Create<HttpContext, string>(
        contexte =>
        {
            var chemin = contexte.Request.Path;

            if (chemin.StartsWithSegments("/_framework")
                || chemin.StartsWithSegments("/_blazor")
                || chemin.StartsWithSegments("/_content")
                || Path.HasExtension(chemin.Value))
                return System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter("statique");

            // §3.19 : la sonde de sante n'est pas limitee. Un repartiteur de
            // charge interroge /health toutes les quelques secondes ; l'etrangler
            // ferait declarer l'application morte alors qu'elle va bien.
            if (chemin.StartsWithSegments("/health"))
                return System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter("sante");

            var cle = contexte.Connection.RemoteIpAddress?.ToString() ?? "inconnu";

            return System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(cle,
                _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
                {
                    PermitLimit = 300,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                });
        });
});

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
// Health Checks (Phase IX)
// ---------------------------------------------------------------------------
builder.Services.AddHealthChecks()
    .AddDbContextCheck<N4SentinelDbContext>("database")
    // Note: Ajouter d'autres checks liveness/readiness ici (stockage, etc.)
    ;

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

builder.Services.AddTransient<IMfaProvider, TotpMfaProvider>();

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
    .AddUserManager<N4UserManager>()
    .AddSignInManager<N4SignInManager>()
    .AddPasswordValidator<PasswordHistoryValidator>()
    .AddDefaultTokenProviders();

builder.Services.AddN4SentinelAuthorization(builder.Configuration);

// Messagerie : confirmation de compte, reinitialisation et code de second
// facteur (SEC-001). Sans serveur configure, l'expediteur journalise au lieu
// d'envoyer, et le signale a chaque appel.
var smtpOptions = builder.Configuration.GetSection(SmtpOptions.SectionName).Get<SmtpOptions>() ?? new SmtpOptions();
builder.Services.AddSingleton(smtpOptions);
builder.Services.AddSingleton<SmtpEmailSender>();
builder.Services.AddSingleton<IEmailSender<ApplicationUser>>(sp => sp.GetRequiredService<SmtpEmailSender>());

// FR-095 : meme expediteur, canal generique pour les notifications d'operation.
builder.Services.AddSingleton<N4Sentinel.Infrastructure.Notifications.INotificationSender>(
    sp => sp.GetRequiredService<SmtpEmailSender>());

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

    // §3.19 : la sonde de sante est exemptee. Une installation neuve n'a pas
    // encore de compte, mais elle TOURNE — rediriger /health vers le parcours
    // de premier demarrage ferait repondre 302 la ou un repartiteur de charge
    // attend « Healthy », et il conclurait que l'application est morte alors
    // qu'elle attend simplement qu'on la configure.
    // /_blazor est le circuit interactif, et il DOIT etre exempte.
    //
    // Sans cette ligne, la negociation SignalR est elle-meme redirigee vers
    // /premier-demarrage : le circuit ne s'etablit jamais, la page reste non
    // interactive, la saisie n'atteint pas le serveur, et le formulaire part
    // en POST HTML classique que le serveur re-affiche avec un modele vide.
    // L'operateur voit alors « obligatoire » sur des champs qu'il vient de
    // remplir, et AUCUNE installation neuve ne peut creer son premier
    // administrateur. Le middleware qui conduit a cette page en interdisait
    // l'usage.
    var exempte = chemin.StartsWithSegments("/premier-demarrage")
                  || chemin.StartsWithSegments("/_blazor")
                  || chemin.StartsWithSegments("/health")
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
// EN-TETES DE SECURITE (audit SEC-A3).
//
// Pose avant tout autre traitement pour couvrir egalement les reponses
// d'erreur : c'est justement sur une page d'erreur qu'un en-tete oublie se
// remarque le moins.
//
// La protection contre le detournement de clic est deja assuree par ASP.NET
// Core (frame-ancestors + X-Frame-Options) et n'est pas repetee ici. Ce qui
// manquait releve de la defense en profondeur : aucun vecteur XSS n'existe
// aujourd'hui - Blazor encode, et le produit n'emploie MarkupString nulle part -
// mais l'application affiche des extraits de journaux, des sections de
// documents verses et des constats de diagnostic, tous d'origine externe. Une
// politique de contenu est la protection qui reste le jour ou une regression
// passe la revue.
app.Use(async (context, next) =>
{
    var entetes = context.Response.Headers;

    // Empeche le navigateur de deviner un type MIME : un fragment de journal
    // servi en text/plain ne doit jamais etre reinterprete comme du script.
    entetes["X-Content-Type-Options"] = "nosniff";

    // Ne fuite pas l'URL consultee vers un tiers. Les URL portent des
    // identifiants d'execution et de diagnostic.
    entetes["Referrer-Policy"] = "strict-origin-when-cross-origin";

    // Aucune fonctionnalite materielle n'est utilisee, sauf le microphone que
    // reclame l'assistant vocal - et uniquement depuis l'application elle-meme.
    entetes["Permissions-Policy"] = "camera=(), geolocation=(), microphone=(self), payment=(), usb=()";

    // Jeton a usage unique, regenere a CHAQUE requete. C'est ce qui distingue
    // un nonce de 'unsafe-inline' : seul le script portant ce jeton precis
    // s'execute, et un script injecte par un attaquant ne peut pas le deviner.
    //
    // Il sert exclusivement au <script type="importmap"> qu'emet Blazor
    // lui-meme : ce bloc est genere par le framework, on ne peut pas le sortir
    // dans un fichier, et sans nonce il etait bloque a chaque chargement de
    // page. Aucun code du produit ne doit s'en servir pour ecrire du script en
    // ligne — le geste correct reste un fichier servi depuis l'origine, comme
    // password-toggle.js et sw-register.js.
    // Hexadecimal et non base64 : base64 produit des '+' et des '/' que Razor
    // encode en entites HTML dans l'attribut ('+' devient '&#x2B;'). Le
    // navigateur les redecode, donc cela fonctionne — mais toute comparaison
    // manuelle entre l'en-tete et la balise donne un faux ecart, et un futur
    // lecteur y perdrait du temps. L'hexadecimal traverse sans transformation.
    var nonce = Convert.ToHexString(
        System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
    context.Items[N4Sentinel.Web.Security.CspNonce.CleContexte] = nonce;

    // Politique de contenu. 'unsafe-inline' sur les styles est impose par
    // Blazor, qui genere des styles en ligne ; le retirer casserait le rendu
    // sans rien apporter, l'injection de style n'etant pas un vecteur ici.
    // Les scripts, eux, sont strictement limites a l'origine : aucune source
    // externe n'est autorisee, ce qui est coherent avec un produit concu pour
    // fonctionner sur un reseau isole.
    if (!entetes.ContainsKey("Content-Security-Policy"))
        entetes["Content-Security-Policy"] =
            "default-src 'self'; "
            + $"script-src 'self' 'nonce-{nonce}'; "
            + "style-src 'self' 'unsafe-inline'; "
            + "img-src 'self' data:; "
            + "font-src 'self'; "
            + "connect-src 'self' ws: wss:; "      // circuit Blazor Server
            + "object-src 'none'; "
            + "base-uri 'self'; "
            + "form-action 'self'; "
            + "frame-ancestors 'self'";

    await next();
});

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseRateLimiter();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapAdditionalIdentityEndpoints();

// §3.19 : comportement dégradé explicite. AddHealthChecks() était enregistré
// mais n'était exposé nulle part — le contrôle existait sans être joignable,
// ce qui revient à ne pas en avoir.
//
// Deux points d'entrée volontairement distincts :
//
//   /health  — anonyme, réponse minimale (« Healthy » / « Unhealthy »). C'est
//              ce qu'interroge IIS ou un répartiteur de charge, qui n'est pas
//              authentifié et n'a pas à connaître le détail.
//
//   /health/detail — réservé aux habilités : nomme le contrôle en échec et sa
//              description. Savoir que la base est injoignable est une
//              information d'exploitation, pas une information publique.
app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResponseWriter = async (contexte, rapport) =>
    {
        contexte.Response.ContentType = "text/plain; charset=utf-8";
        await contexte.Response.WriteAsync(rapport.Status.ToString());
    }
}).AllowAnonymous();

app.MapHealthChecks("/health/detail", new HealthCheckOptions
{
    // La mise en forme vit dans HealthReportFormatter, pour etre testable sans
    // authentification : le contenu de ce point d'entree n'est lisible que
    // connecte, et le controler dans un navigateur supposerait de saisir un
    // mot de passe.
    ResponseWriter = async (contexte, rapport) =>
    {
        contexte.Response.ContentType = "text/plain; charset=utf-8";
        await contexte.Response.WriteAsync(
            N4Sentinel.Web.Security.HealthReportFormatter.Formater(rapport));
    }
}).RequireAuthorization(N4Policies.PeutConsulter);

// NFR-008 : métriques d'exploitation exposées au format texte (compatible
// scrape Prometheus), pour un outil d'APM externe — au-delà des logs.
app.MapGet("/metrics", (N4Sentinel.Infrastructure.Observability.MetricsService metrics) =>
{
    var s = metrics.GetSnapshot();
    var lignes = new List<string>
    {
        "# HELP n4sentinel_supervision_poll_total Nombre de passages de supervision effectués.",
        "# TYPE n4sentinel_supervision_poll_total counter",
        $"n4sentinel_supervision_poll_total {s.SupervisionPollCount}",
        "# HELP n4sentinel_supervision_poll_failures_total Nombre de passages de supervision en échec.",
        "# TYPE n4sentinel_supervision_poll_failures_total counter",
        $"n4sentinel_supervision_poll_failures_total {s.SupervisionPollFailureCount}",
        "# HELP n4sentinel_supervision_poll_duration_ms_avg Durée moyenne d'un passage de supervision.",
        "# TYPE n4sentinel_supervision_poll_duration_ms_avg gauge",
        $"n4sentinel_supervision_poll_duration_ms_avg {s.SupervisionPollAverageMs.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}",
        "# HELP n4sentinel_step_outcome_total Issues d'étape d'exécution, par état.",
        "# TYPE n4sentinel_step_outcome_total counter"
    };
    foreach (var (etat, n) in s.StepOutcomes)
        lignes.Add($"n4sentinel_step_outcome_total{{state=\"{etat}\"}} {n}");

    lignes.Add("# HELP n4sentinel_diagnostic_verdict_total Diagnostics conclus, par verdict.");
    lignes.Add("# TYPE n4sentinel_diagnostic_verdict_total counter");
    foreach (var (verdict, n) in s.DiagnosticVerdicts)
        lignes.Add($"n4sentinel_diagnostic_verdict_total{{verdict=\"{verdict}\"}} {n}");

    return Results.Text(string.Join('\n', lignes) + '\n', "text/plain; version=0.0.4");
})
// AUDIT SEC-A2 : ce point d'entree repondait a un appelant anonyme.
//
// Il ne divulgue aucun secret, mais il confirme la presence de l'application
// et revele son rythme d'exploitation : nombre d'operations, taux d'echec,
// verdicts de diagnostic. Pour qui prepare une intrusion, c'est un indicateur
// des moments ou l'equipe est occupee ailleurs.
//
// L'authentification est exigee. Un collecteur Prometheus s'y conforme en
// portant un compte de service dedie, habilite a la seule consultation.
.RequireAuthorization(N4Policies.PeutConsulter);

app.Run();
