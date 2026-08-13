namespace N4Sentinel.Domain;

/// <summary>
/// Base des entites du referentiel. Porte la tracabilite exigee par le
/// cahier des charges : toute creation ou modification de configuration est
/// horodatee et rattachee a son auteur (principe "Tracabilite systematique").
/// </summary>
public abstract class AuditableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = string.Empty;

    public DateTimeOffset? ModifiedAt { get; set; }
    public string? ModifiedBy { get; set; }

    /// <summary>
    /// Jeton de concurrence. Deux administrateurs qui editent le meme
    /// composant ne doivent pas s'ecraser silencieusement.
    /// </summary>
    public byte[]? RowVersion { get; set; }
}
