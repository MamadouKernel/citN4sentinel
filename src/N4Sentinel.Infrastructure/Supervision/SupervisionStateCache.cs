using System.Collections.Concurrent;
using N4Sentinel.Domain;

namespace N4Sentinel.Infrastructure.Supervision;

/// <summary>
/// Cache thread-safe en mémoire conservant les derniers instantanés de santé.
///
/// Permet à l'interface Blazor d'afficher l'état en temps réel sans relancer
/// de session WinRM synchrone à chaque rafraîchissement d'écran.
/// </summary>
public sealed class SupervisionStateCache
{
    private readonly ConcurrentDictionary<Guid, ComponentHealthSnapshot> _snapshots = new();

    /// <summary>Événement déclenché à chaque mise à jour d'un statut.</summary>
    public event Action? OnStateChanged;

    public void UpdateSnapshot(ComponentHealthSnapshot snapshot)
    {
        _snapshots[snapshot.ComponentId] = snapshot;
        OnStateChanged?.Invoke();
    }

    public ComponentHealthSnapshot? GetSnapshot(Guid componentId)
    {
        return _snapshots.TryGetValue(componentId, out var snapshot) ? snapshot : null;
    }

    public IReadOnlyList<ComponentHealthSnapshot> GetAllSnapshots()
    {
        return _snapshots.Values.OrderBy(s => s.EnvironmentCode).ThenBy(s => s.StartOrder).ToList();
    }

    public IReadOnlyList<ComponentHealthSnapshot> GetSnapshotsForEnvironment(string environmentCode)
    {
        return _snapshots.Values
            .Where(s => string.Equals(s.EnvironmentCode, environmentCode, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.StartOrder)
            .ThenBy(s => s.LogicalName)
            .ToList();
    }

    public StateSummary GetSummary(string? environmentCode = null)
    {
        var items = string.IsNullOrWhiteSpace(environmentCode)
            ? _snapshots.Values.ToList()
            : _snapshots.Values.Where(s => string.Equals(s.EnvironmentCode, environmentCode, StringComparison.OrdinalIgnoreCase)).ToList();

        return new StateSummary
        {
            Total = items.Count,
            Disponible = items.Count(i => i.State == ComponentState.Disponible),
            Degrade = items.Count(i => i.State == ComponentState.Degrade),
            Indisponible = items.Count(i => i.State == ComponentState.Indisponible),
            Demarrage = items.Count(i => i.State == ComponentState.Demarrage),
            Arret = items.Count(i => i.State == ComponentState.Arret),
            Inconnu = items.Count(i => i.State == ComponentState.Inconnu),
            NonSupervise = items.Count(i => i.State == ComponentState.NonSupervise)
        };
    }
}

public sealed class StateSummary
{
    public int Total { get; init; }
    public int Disponible { get; init; }
    public int Degrade { get; init; }
    public int Indisponible { get; init; }
    public int Demarrage { get; init; }
    public int Arret { get; init; }
    public int Inconnu { get; init; }
    public int NonSupervise { get; init; }
}
