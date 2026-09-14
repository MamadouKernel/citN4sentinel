namespace N4Sentinel.Domain.Interfaces;

/// <summary>
/// Contrat « interfaces externes · EDI » (S1-09).
///
/// <para>But : isoler le domaine de l'implémentation Infrastructure qui porte
/// les connecteurs PowerShell / SSH / SMB / HTTP. En V1, seul le suivi des
/// fichiers EDI (SharedFolder → en attente / consommé / erreur) est exposé ;
/// les méthodes d'émission seront ajoutées par implémentation explicite
/// d'interface (DIP — Dependency Inversion Principle).</para>
///
/// <para>L'interface ne référence AUCUN type de <c>N4Sentinel.Infrastructure</c> :
/// EdiFile, N4Component sont définis dans Domain.</para>
/// </summary>
public interface IEdiConnector
{
    /// <summary>
    /// Relève la réalité du dossier partagé déclaré sur le composant et
    /// synchronise les identités de fichiers dans la base : transition
    /// en-attente → consommé → archivé, comptage, détection de retard.
    /// Retourne l'état complet <b>après</b> synchronisation.
    /// </summary>
    /// <param name="composant">Composant dont le <see cref="N4Component.SharedFolder"/>
    /// déclare le sous-dossier et le motif de nommage EDI.</param>
    /// <param name="ct">Jeton d'annulation.</param>
    Task<List<EdiFile>> SyncAsync(N4Component composant, CancellationToken ct = default);

    /// <summary>
    /// Lecture seule : retourne l'image actuelle de la table EdiFiles pour un
    /// composant, sans émettre d'appel réseau. Utile pour les affichages UI
    /// (onglet « Interfaces » d'un environnement).
    /// </summary>
    Task<List<EdiFile>> GetForComponentAsync(Guid componentId, CancellationToken ct = default);

    /// <summary>
    /// Teste la joignabilité du dossier partagé d'un composant EDI via la
    /// factory de connecteurs (SMB / PowerShell). Retourne true si la
    /// résolution cible + un appel ListFiles fonctionnent ; false sinon.
    /// </summary>
    Task<bool> TestConnectivityAsync(N4Component composant, CancellationToken ct = default);

    /// <summary>
    /// Rejoue les fichiers d'un lot déclaré en échec. En V1, cette opération
    /// produit une trace d'audit et retourne la liste des fichiers remis en
    /// file d'attente ; l'exécution physique est faite par le moteur EDI
    /// externe (batch CIT-DSIE-02).
    /// </summary>
    /// <param name="fileIds">Identifiants des enregistrements à rejouer.</param>
    /// <param name="actor">Compte déclencheur (pour audit).</param>
    /// <param name="ct">Jeton d'annulation.</param>
    /// <returns>Nombre de fichiers passés en « à rejouer ».</returns>
    Task<int> ResendFailedAsync(IEnumerable<Guid> fileIds, string actor, CancellationToken ct = default);
}
