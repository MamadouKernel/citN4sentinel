using N4Sentinel.Domain;

namespace N4Sentinel.Web.State;

/// <summary>
/// Environnement sur lequel l'utilisateur travaille actuellement.
///
/// Le cahier des charges exige que l'environnement soit visible en permanence
/// et que la Production soit clairement identifiee (FR-001, SEC-004). La raison
/// est operationnelle, pas cosmetique : la confusion entre UAT et Production
/// est le scenario qui transforme une manipulation banale en incident majeur.
///
/// Portee circuit : chaque session Blazor a la sienne. Deux operateurs peuvent
/// travailler simultanement sur deux environnements differents sans se marcher
/// dessus.
/// </summary>
public sealed class CurrentEnvironmentState
{
    private N4Environment? _current;

    /// <summary>Declenche quand l'environnement courant change, pour rafraichir l'en-tete.</summary>
    public event Action? Changed;

    public N4Environment? Current
    {
        get => _current;
        private set
        {
            if (_current?.Id == value?.Id) return;
            _current = value;
            Changed?.Invoke();
        }
    }

    public bool HasContext => _current is not null;

    /// <summary>Vrai uniquement si l'environnement courant est declare Production.</summary>
    public bool IsProduction => _current?.IsProduction ?? false;

    public void Set(N4Environment environment) => Current = environment;

    public void Clear() => Current = null;
}
