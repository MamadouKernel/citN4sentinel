using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure;
using N4Sentinel.Infrastructure.Identity;
using N4Sentinel.Infrastructure.Persistence;
using N4Sentinel.Web.Components;
using N4Sentinel.Web.Components.Account;
using N4Sentinel.Web.Security;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Interface
// ---------------------------------------------------------------------------
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddCascadingAuthenticationState();
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

// A REMPLACER EN S1 par un expediteur SMTP reel : tant que cet expediteur ne
// fait rien, le second facteur par e-mail exige en V1 ne peut pas etre
// delivre, et la confirmation de compte non plus (SEC-001).
builder.Services.AddSingleton<IEmailSender<ApplicationUser>, IdentityNoOpEmailSender>();

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

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapAdditionalIdentityEndpoints();

app.Run();
