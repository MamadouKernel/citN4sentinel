using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using N4Sentinel.Infrastructure.Orchestration;
using N4Sentinel.Infrastructure.Persistence;
using N4Sentinel.Infrastructure.Referential;
using N4Sentinel.Infrastructure.Security;
using N4Sentinel.Web.Components.Pages;
using N4Sentinel.Domain;
using Moq;
using System.Security.Claims;

namespace N4Sentinel.Tests.UI;

public class HistoryComponentsTests : BunitContext
{
    public HistoryComponentsTests()
    {
        var dbName = Guid.NewGuid().ToString();
        Services.AddDbContextFactory<N4SentinelDbContext>(options =>
            options.UseInMemoryDatabase(dbName));

        var auditWriterMock = new Mock<IAuditWriter>();
        Services.AddSingleton(auditWriterMock.Object);
        
        // Le service s'appelle HistoryService (Infrastructure.Reporting) :
        // ExecutionHistoryService n'a jamais existe.
        Services.AddSingleton<N4Sentinel.Infrastructure.Reporting.HistoryService>();
        Services.AddSingleton<N4Sentinel.Infrastructure.Reporting.ReportDocumentService>();
        Services.AddSingleton<ReferentialService>();

        // La page appelle JS pour proposer le telechargement du dossier
        // d'escalade : le mode permissif suffit, rien n'est verifie ici.
        JSInterop.Mode = Bunit.JSRuntimeMode.Loose;

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, "TestUser"),
            new Claim(ClaimTypes.Role, "Consultant")
        };
        var identity = new ClaimsIdentity(claims, "TestAuthType");
        var claimsPrincipal = new ClaimsPrincipal(identity);

        var authState = new AuthenticationState(claimsPrincipal);
        var authProviderMock = new Moq.Mock<AuthenticationStateProvider>();
        authProviderMock.Setup(p => p.GetAuthenticationStateAsync()).ReturnsAsync(authState);
        
        Services.AddSingleton<AuthenticationStateProvider>(authProviderMock.Object);

        Services.AddAuthorizationCore(options =>
        {
            options.AddPolicy(N4Policies.PeutConsulter, p => p.RequireAssertion(_ => true));
        });
    }

    [Fact]
    public void L_Historique_S_Affiche_Et_Dit_Que_L_Absence_D_Evenement_Ne_Prouve_Rien()
    {
        var cut = Render<Historique>();

        // MarkupMatches compare du balisage, pas une expression reguliere :
        // l'ecrire ainsi faisait echouer le test quoi qu'il arrive.
        Assert.Contains("Historique", cut.Markup);

        // Le point qui compte : sur une base vide, l'ecran ne doit pas laisser
        // croire qu'il ne s'est rien passe. Les actions menees hors de
        // l'application n'y figurent pas, et il doit le dire.
        Assert.Contains("Aucun événement enregistré", cut.Markup);
        Assert.Contains("hors de", cut.Markup);
    }
}
