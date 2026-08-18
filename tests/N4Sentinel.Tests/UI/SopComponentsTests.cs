using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Persistence;
using N4Sentinel.Infrastructure.Procedures;
using N4Sentinel.Infrastructure.Referential;
using N4Sentinel.Infrastructure.Security;
using N4Sentinel.Web.Components.Pages;
using System.Security.Claims;
using Moq;

namespace N4Sentinel.Tests.UI;

public class SopComponentsTests : BunitContext
{
    public SopComponentsTests()
    {
        // DB en mémoire
        var dbName = Guid.NewGuid().ToString();
        Services.AddDbContextFactory<N4SentinelDbContext>(options =>
            options.UseInMemoryDatabase(dbName));

        var auditWriterMock = new Mock<IAuditWriter>();
        Services.AddSingleton(auditWriterMock.Object);
        
        // Services
        // SopService depend de KnowledgeService (rattachement d'une SOP a la
        // documentation) : sans lui, l'activation echoue au rendu.
        Services.AddSingleton<N4Sentinel.Infrastructure.Knowledge.KnowledgeService>();
        Services.AddSingleton<SopService>();
        Services.AddSingleton<SopExecutionService>();
        Services.AddSingleton<ReferentialService>();

        // Auth
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

        // Autorisations
        Services.AddAuthorizationCore(options =>
        {
            options.AddPolicy(N4Policies.PeutConsulter, p => p.RequireAssertion(_ => true));
            options.AddPolicy(N4Policies.PeutExecuter, p => p.RequireAssertion(_ => true));
            options.AddPolicy(N4Policies.PeutAdministrerReferentiel, p => p.RequireAssertion(_ => true));
        });
    }

    [Fact]
    public void L_Ecran_Des_SOP_S_Affiche_Sur_Une_Base_Vide()
    {
        var cut = Render<SopExecutions>();

        // MarkupMatches compare du balisage, pas une expression reguliere :
        // l'ecrire ainsi faisait echouer le test quoi qu'il arrive.
        Assert.Contains("Procédures opérationnelles", cut.Markup);
    }
}
