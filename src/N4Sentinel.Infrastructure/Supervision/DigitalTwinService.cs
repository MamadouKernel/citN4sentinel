using Microsoft.Extensions.Logging;

namespace N4Sentinel.Infrastructure.Supervision;

/// <summary>
/// Service de cartographie Jumeau Numérique 3D / Isometric Canvas
/// liant les équipements portuaires physiques (Portiques STS, RTG, Guérites) aux composants Navis N4.
/// </summary>
public sealed class DigitalTwinService(ILogger<DigitalTwinService> logger)
{
    public DigitalTwinSnapshot GetTerminalSnapshot()
    {
        logger.LogInformation("[Digital Twin] Génération du snapshot du jumeau numérique Côte d'Ivoire Terminal.");

        return new DigitalTwinSnapshot
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            PhysicalAssets = [
                new TerminalPhysicalAsset
                {
                    Id = "STS-01",
                    Name = "Portique Quai STS 01 (Liebherr)",
                    Category = "Portique Quai",
                    Zone = "Quai Sud",
                    Status = "OPERATIONNEL",
                    AssociatedSoftwareNodes = ["SRV-N4-NODE01", "ECN4-SERVICE"],
                    ActiveWorkInstructionCount = 142
                },
                new TerminalPhysicalAsset
                {
                    Id = "STS-02",
                    Name = "Portique Quai STS 02 (Liebherr)",
                    Category = "Portique Quai",
                    Zone = "Quai Sud",
                    Status = "OPERATIONNEL",
                    AssociatedSoftwareNodes = ["SRV-N4-NODE02", "ECN4-SERVICE"],
                    ActiveWorkInstructionCount = 128
                },
                new TerminalPhysicalAsset
                {
                    Id = "RTG-05",
                    Name = "Grue de Parc RTG 05 (Kalmar)",
                    Category = "Parc Conteneurs",
                    Zone = "Bloc B4",
                    Status = "ATTENTION",
                    AssociatedSoftwareNodes = ["XPS-SERVICE", "SRV-AMQ-BROKER01"],
                    ActiveWorkInstructionCount = 89
                },
                new TerminalPhysicalAsset
                {
                    Id = "GATE-SOUTH",
                    Name = "Guérite d'Accès Camions Sud (Camco GOS)",
                    Category = "Guérite Accès",
                    Zone = "Portail Sud",
                    Status = "OPERATIONNEL",
                    AssociatedSoftwareNodes = ["SRV-N4-NODE03", "EDI-SERVICE"],
                    ActiveWorkInstructionCount = 312
                },
                new TerminalPhysicalAsset
                {
                    Id = "RACK-DC-01",
                    Name = "Baie Serveur Datacenter RACK-01",
                    Category = "Datacenter IT",
                    Zone = "Salle Serveurs CIT",
                    Status = "CRITIQUE",
                    AssociatedSoftwareNodes = ["SRV-N4-NODE02", "SRV-SQL-PRIMARY"],
                    ActiveWorkInstructionCount = 0
                }
            ]
        };
    }
}

public sealed class DigitalTwinSnapshot
{
    public DateTimeOffset GeneratedAt { get; init; }
    public List<TerminalPhysicalAsset> PhysicalAssets { get; init; } = [];
}

public sealed class TerminalPhysicalAsset
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Category { get; init; }
    public required string Zone { get; init; }
    public required string Status { get; init; }
    public List<string> AssociatedSoftwareNodes { get; init; } = [];
    public int ActiveWorkInstructionCount { get; init; }
}
