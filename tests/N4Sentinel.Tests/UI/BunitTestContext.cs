using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using N4Sentinel.Infrastructure.Orchestration;
using N4Sentinel.Infrastructure.Supervision;

namespace N4Sentinel.Tests.UI;

/// <summary>
/// Classe de base pour les tests UI utilisant Bunit.
/// Centralise la configuration du TestContext et les mocks courants.
/// </summary>
public abstract class BunitTestContext : TestContext
{
    protected Mock<ExecutionService> MockExecutionService { get; }
    protected Mock<SupervisionService> MockSupervisionService { get; }
    
    protected BunitTestContext()
    {
        MockExecutionService = new Mock<ExecutionService>(
            null, null, null, null, null, null, null, null, null, null, null);
        MockSupervisionService = new Mock<SupervisionService>(
            null, null, null, null, null, null);

        // Injection des services de base
        Services.AddSingleton(MockExecutionService.Object);
        Services.AddSingleton(MockSupervisionService.Object);
        Services.AddSingleton(NullLoggerFactory.Instance);
    }
}
