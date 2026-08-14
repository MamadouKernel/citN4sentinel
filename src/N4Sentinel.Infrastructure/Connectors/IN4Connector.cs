namespace N4Sentinel.Infrastructure.Connectors;

/// <summary>
/// Acces technique a un serveur de l'ecosysteme N4.
///
/// TOUT EST EN LECTURE SEULE SAUF <see cref="ControlServiceAsync"/>, seule
/// methode mutative de l'interface. Elle emet une commande et rend la main : la
/// PREUVE que le composant est operationnel n'est pas de son ressort, c'est
/// celui du moteur d'orchestration, qui la cherche dans le journal applicatif.
/// Confondre les deux reviendrait a reintroduire ici l'erreur que tout le
/// projet cherche a corriger.
///
/// Aucun ecran n'appelle ControlServiceAsync directement : le pilotage passe
/// par un workflow valide, tracé, et sous verrou d'environnement.
///
/// Les implementations ne levent pas d'exception sur echec technique : elles
/// retournent un resultat portant l'echec et sa cause. Un serveur injoignable
/// est une information de diagnostic, pas un incident applicatif.
/// </summary>
public interface IN4Connector
{
    /// <summary>Verifie que la cible repond et que l'execution distante fonctionne.</summary>
    Task<ConnectorResult<string>> PingAsync(ConnectorTarget target, CancellationToken ct = default);

    /// <summary>Etat d'un service Windows. Preuve faible : voir la note de ServiceSnapshot.</summary>
    Task<ConnectorResult<ServiceSnapshot>> GetServiceAsync(
        ConnectorTarget target, string serviceName, CancellationToken ct = default);

    /// <summary>Etat de plusieurs services en une seule connexion.</summary>
    Task<ConnectorResult<IReadOnlyList<ServiceSnapshot>>> GetServicesAsync(
        ConnectorTarget target, IReadOnlyCollection<string> serviceNames, CancellationToken ct = default);

    /// <summary>
    /// Enumere les services dont le nom ou le nom d'affichage contient l'un des
    /// motifs. Sert a decouvrir ce qui tourne sur un serveur sans le connaitre
    /// d'avance - notamment un composant installe mais jamais declare.
    /// </summary>
    Task<ConnectorResult<IReadOnlyList<ServiceSnapshot>>> ListServicesAsync(
        ConnectorTarget target, IReadOnlyCollection<string> namePatterns, CancellationToken ct = default);

    /// <summary>Ressources systeme : processeur, memoire, disques, horloge.</summary>
    Task<ConnectorResult<SystemSnapshot>> GetSystemAsync(
        ConnectorTarget target, CancellationToken ct = default);

    /// <summary>
    /// Lit la portion de journal ecrite depuis un offset donne.
    /// Reprend la mecanique eprouvee des scripts d'exploitation : lecture
    /// incrementale, partage en lecture/ecriture puisque la JVM garde son
    /// journal ouvert, et detection de rotation.
    /// </summary>
    Task<ConnectorResult<LogDelta>> ReadLogDeltaAsync(
        ConnectorTarget target, string logPathOrPattern, long offset,
        int maxBytes = 262_144, CancellationToken ct = default);

    /// <summary>Resout un motif de chemin vers le fichier le plus recent.</summary>
    Task<ConnectorResult<LogFileInfo>> ResolveLogAsync(
        ConnectorTarget target, string logPathOrPattern, CancellationToken ct = default);

    /// <summary>
    /// SEULE OPERATION MUTATIVE DE L'INTERFACE. Demande a Windows de demarrer
    /// ou d'arreter un service, et retourne l'etat observe juste apres.
    ///
    /// Cette methode ne dit PAS que le composant est operationnel : elle dit que
    /// la commande a ete acceptee. La preuve applicative est cherchee ensuite
    /// dans le journal par le moteur d'orchestration.
    /// </summary>
    Task<ConnectorResult<ServiceSnapshot>> ControlServiceAsync(
        ConnectorTarget target, string serviceName, ServiceControlAction action,
        CancellationToken ct = default);
}

/// <summary>
/// Commande adressee au gestionnaire de services Windows.
///
/// Il n'y a pas de "Redemarrer" ici volontairement : un redemarrage est un arret
/// dont on prouve l'aboutissement, suivi d'un demarrage dont on prouve
/// l'aboutissement. Le fondre en une seule commande ferait perdre la preuve
/// intermediaire, et donc la capacite a dire lequel des deux a echoue.
/// </summary>
public enum ServiceControlAction
{
    Demarrer = 0,
    Arreter = 1
}

/// <summary>Serveur cible et mode d'acces.</summary>
public sealed record ConnectorTarget
{
    public required string HostName { get; init; }
    public int WinRmPort { get; init; } = 5985;
    public bool UseSsl { get; init; }

    /// <summary>
    /// Execution dans le processus courant plutot que via WinRM. Reservee au
    /// serveur qui heberge N4 Sentinel lui-meme : passer par une session
    /// distante vers sa propre machine ajoute une dependance a WinRM sans
    /// rien apporter.
    /// </summary>
    public bool IsLocal { get; init; }

    /// <summary>Compte de service. Null = identite du processus applicatif.</summary>
    public System.Management.Automation.PSCredential? Credential { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    public static ConnectorTarget Local() => new() { HostName = Environment.MachineName, IsLocal = true };
}

/// <summary>
/// Resultat d'un appel : reussite avec valeur, ou echec avec cause exploitable.
/// </summary>
public sealed record ConnectorResult<T>
{
    public bool Succeeded { get; init; }
    public T? Value { get; init; }
    public string? Error { get; init; }
    public ConnectorFailure Failure { get; init; }
    public TimeSpan Duration { get; init; }
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;

    public static ConnectorResult<T> Ok(T value, TimeSpan duration) =>
        new() { Succeeded = true, Value = value, Duration = duration };

    public static ConnectorResult<T> Fail(ConnectorFailure failure, string error, TimeSpan duration) =>
        new() { Succeeded = false, Failure = failure, Error = error, Duration = duration };
}

/// <summary>
/// Nature de l'echec. Distinguer ces cas est ce qui permet au diagnostic de
/// dire "le serveur ne repond pas" plutot que "erreur", et de ne pas confondre
/// un refus d'authentification avec une panne reseau.
/// </summary>
public enum ConnectorFailure
{
    Aucune = 0,
    NomNonResolu,
    Injoignable,
    AuthentificationRefusee,
    AccesRefuse,
    Timeout,
    CibleIntrouvable,
    ErreurDistante
}

/// <summary>
/// Etat d'un service Windows.
///
/// PREUVE FAIBLE, et c'est le point central du projet : "Running" signifie que
/// Windows a lance le processus, pas que le composant N4 est operationnel. La
/// JVM peut encore charger sa configuration pendant plusieurs minutes. La
/// confirmation reelle vient du journal applicatif.
/// </summary>
public sealed record ServiceSnapshot
{
    public required string Name { get; init; }
    public string? DisplayName { get; init; }

    /// <summary>Running, Stopped, StartPending, StopPending, Paused, ou Introuvable.</summary>
    public required string Status { get; init; }
    public string? StartMode { get; init; }
    public int? ProcessId { get; init; }

    /// <summary>Memoire de travail du processus, quand il tourne.</summary>
    public long? WorkingSetBytes { get; init; }
    public DateTimeOffset? ProcessStartTime { get; init; }

    public bool Exists => !string.Equals(Status, "Introuvable", StringComparison.OrdinalIgnoreCase);
    public bool IsRunning => string.Equals(Status, "Running", StringComparison.OrdinalIgnoreCase);

    /// <summary>Service bloque en cours d'arret : cas connu du Standby Center Node.</summary>
    public bool IsStopPending => string.Equals(Status, "StopPending", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Ressources systeme d'un serveur.</summary>
public sealed record SystemSnapshot
{
    public required string HostName { get; init; }
    public string? OperatingSystem { get; init; }
    public DateTimeOffset? LastBootTime { get; init; }

    /// <summary>
    /// Heure du serveur. L'ecart avec l'heure de reference est une cause
    /// documentee de statuts DISCONNECTED trompeurs, et une cause frequente
    /// d'incident majeur sur N4.
    /// </summary>
    public DateTimeOffset? SystemTime { get; init; }
    public double? ClockSkewSeconds { get; init; }

    public double? CpuPercent { get; init; }
    public long? TotalMemoryBytes { get; init; }
    public long? FreeMemoryBytes { get; init; }
    public IReadOnlyList<DiskSnapshot> Disks { get; init; } = [];

    public double? MemoryUsedPercent =>
        TotalMemoryBytes is > 0 && FreeMemoryBytes is not null
            ? Math.Round((1 - (double)FreeMemoryBytes.Value / TotalMemoryBytes.Value) * 100, 1)
            : null;
}

public sealed record DiskSnapshot
{
    public required string Drive { get; init; }
    public long TotalBytes { get; init; }
    public long FreeBytes { get; init; }

    public double FreePercent => TotalBytes > 0 ? Math.Round((double)FreeBytes / TotalBytes * 100, 1) : 0;
}

/// <summary>Portion de journal lue depuis un offset.</summary>
public sealed record LogDelta
{
    public required string ResolvedPath { get; init; }
    public bool Exists { get; init; }
    public string Text { get; init; } = string.Empty;
    public long NewOffset { get; init; }
    public long Length { get; init; }

    /// <summary>Fichier redevenu plus court : le journal a tourne.</summary>
    public bool Rotated { get; init; }
}

public sealed record LogFileInfo
{
    public bool Exists { get; init; }
    public string? Path { get; init; }
    public long Length { get; init; }
    public DateTimeOffset? LastWriteTime { get; init; }
}
