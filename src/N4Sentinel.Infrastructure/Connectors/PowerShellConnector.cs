using System.Diagnostics;
using System.Management.Automation;
using System.Management.Automation.Remoting;
using System.Management.Automation.Runspaces;
using Microsoft.Extensions.Logging;

namespace N4Sentinel.Infrastructure.Connectors;

/// <summary>
/// Connecteur PowerShell : execution locale dans le processus, ou distante via
/// WinRM.
///
/// POURQUOI POWERSHELL PLUTOT QUE WMI OU DES APPELS .NET DIRECTS
/// Les scripts d'exploitation deja eprouves - lecture incrementale de journal,
/// detection de rotation, ouverture en partage lecture/ecriture parce que la
/// JVM garde son fichier ouvert - sont ecrits en PowerShell. Rejouer la meme
/// logique ici garantit que l'application et les scripts observent exactement
/// la meme chose. Une reimplementation en C# aurait diverge tot ou tard, et la
/// divergence se serait manifestee un jour d'incident.
///
/// TOUT CE QUI EST EXECUTE ICI EST EN LECTURE SEULE. Aucune methode de cette
/// classe ne demarre, n'arrete ni ne modifie quoi que ce soit.
/// </summary>
public sealed class PowerShellConnector(ILogger<PowerShellConnector> logger) : IN4Connector
{
    // -----------------------------------------------------------------------
    // API publique
    // -----------------------------------------------------------------------
    public async Task<ConnectorResult<string>> PingAsync(ConnectorTarget target, CancellationToken ct = default)
    {
        const string script = """
            [PSCustomObject]@{
                Machine = $env:COMPUTERNAME
                PSVersion = $PSVersionTable.PSVersion.ToString()
                Utilisateur = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
            }
            """;

        var result = await ExecuteAsync(target, script, null, ct);
        if (!result.Succeeded)
            return ConnectorResult<string>.Fail(result.Failure, result.Error!, result.Duration);

        var o = result.Value!.FirstOrDefault();
        var description = o is null
            ? "reponse vide"
            : $"{Get<string>(o, "Machine")} (PowerShell {Get<string>(o, "PSVersion")}, " +
              $"execute par {Get<string>(o, "Utilisateur")})";

        return ConnectorResult<string>.Ok(description, result.Duration);
    }

    public async Task<ConnectorResult<ServiceSnapshot>> GetServiceAsync(
        ConnectorTarget target, string serviceName, CancellationToken ct = default)
    {
        var result = await GetServicesAsync(target, [serviceName], ct);
        if (!result.Succeeded)
            return ConnectorResult<ServiceSnapshot>.Fail(result.Failure, result.Error!, result.Duration);

        var snapshot = result.Value!.FirstOrDefault()
                       ?? new ServiceSnapshot { Name = serviceName, Status = "Introuvable" };
        return ConnectorResult<ServiceSnapshot>.Ok(snapshot, result.Duration);
    }

    public async Task<ConnectorResult<IReadOnlyList<ServiceSnapshot>>> GetServicesAsync(
        ConnectorTarget target, IReadOnlyCollection<string> serviceNames, CancellationToken ct = default)
    {
        // Un service peut etre designe par son nom court ou son nom d'affichage :
        // Navis-Config.json utilise les noms d'affichage ("Navis N4 Center Node").
        // On accepte les deux plutot que d'imposer une convention.
        const string script = """
            param([string[]]$Noms)
            foreach ($nom in $Noms) {
                $svc = Get-Service -Name $nom -ErrorAction SilentlyContinue
                if (-not $svc) {
                    $svc = Get-Service -ErrorAction SilentlyContinue |
                           Where-Object { $_.DisplayName -eq $nom } | Select-Object -First 1
                }
                if (-not $svc) {
                    [PSCustomObject]@{ Name = $nom; DisplayName = $null; Status = 'Introuvable'
                                       StartMode = $null; ProcessId = $null; WorkingSet = $null; StartTime = $null }
                    continue
                }

                $pidValeur = $null; $startMode = $null
                try {
                    $wmi = Get-CimInstance Win32_Service -Filter "Name='$($svc.Name)'" -ErrorAction SilentlyContinue
                    if ($wmi) { $startMode = $wmi.StartMode; if ($wmi.ProcessId -gt 0) { $pidValeur = $wmi.ProcessId } }
                } catch { }

                $ws = $null; $debut = $null
                if ($pidValeur) {
                    try {
                        $proc = Get-Process -Id $pidValeur -ErrorAction SilentlyContinue
                        if ($proc) { $ws = $proc.WorkingSet64; $debut = $proc.StartTime }
                    } catch { }
                }

                [PSCustomObject]@{
                    Name = $svc.Name; DisplayName = $svc.DisplayName; Status = [string]$svc.Status
                    StartMode = $startMode; ProcessId = $pidValeur; WorkingSet = $ws; StartTime = $debut
                }
            }
            """;

        var result = await ExecuteAsync(target, script,
            new Dictionary<string, object?> { ["Noms"] = serviceNames.ToArray() }, ct);

        if (!result.Succeeded)
            return ConnectorResult<IReadOnlyList<ServiceSnapshot>>.Fail(result.Failure, result.Error!, result.Duration);

        var snapshots = result.Value!.Select(o => new ServiceSnapshot
        {
            Name = Get<string>(o, "Name") ?? "?",
            DisplayName = Get<string>(o, "DisplayName"),
            Status = Get<string>(o, "Status") ?? "Inconnu",
            StartMode = Get<string>(o, "StartMode"),
            ProcessId = GetNullableInt(o, "ProcessId"),
            WorkingSetBytes = GetNullableLong(o, "WorkingSet"),
            ProcessStartTime = GetNullableDate(o, "StartTime")
        }).ToList();

        return ConnectorResult<IReadOnlyList<ServiceSnapshot>>.Ok(snapshots, result.Duration);
    }

    public async Task<ConnectorResult<SystemSnapshot>> GetSystemAsync(
        ConnectorTarget target, CancellationToken ct = default)
    {
        const string script = """
            $os = Get-CimInstance Win32_OperatingSystem
            $cpu = $null
            try {
                $cpu = (Get-CimInstance Win32_Processor | Measure-Object -Property LoadPercentage -Average).Average
            } catch { }

            $disques = @()
            foreach ($d in (Get-CimInstance Win32_LogicalDisk -Filter "DriveType=3")) {
                $disques += [PSCustomObject]@{ Drive = $d.DeviceID; Total = $d.Size; Free = $d.FreeSpace }
            }

            [PSCustomObject]@{
                HostName = $env:COMPUTERNAME
                OS = $os.Caption
                LastBoot = $os.LastBootUpTime
                SystemTime = (Get-Date)
                Cpu = $cpu
                TotalMemory = ($os.TotalVisibleMemorySize * 1KB)
                FreeMemory = ($os.FreePhysicalMemory * 1KB)
                Disks = $disques
            }
            """;

        var reference = DateTimeOffset.UtcNow;
        var result = await ExecuteAsync(target, script, null, ct);
        if (!result.Succeeded)
            return ConnectorResult<SystemSnapshot>.Fail(result.Failure, result.Error!, result.Duration);

        var o = result.Value!.FirstOrDefault();
        if (o is null)
            return ConnectorResult<SystemSnapshot>.Fail(
                ConnectorFailure.ErreurDistante, "Aucune donnee systeme retournee.", result.Duration);

        var systemTime = GetNullableDate(o, "SystemTime");

        // L'ecart d'horloge est mesure contre l'heure de cette machine, apres
        // deduction du temps d'aller-retour : sans cette correction, une liaison
        // lente ferait apparaitre un decalage qui n'existe pas.
        double? skew = null;
        if (systemTime.HasValue)
        {
            var attendu = reference.Add(result.Duration / 2);
            skew = Math.Round((systemTime.Value.ToUniversalTime() - attendu.ToUniversalTime()).TotalSeconds, 2);
        }

        var disks = new List<DiskSnapshot>();
        if (o.Properties["Disks"]?.Value is IEnumerable<object> bruts)
        {
            foreach (var d in bruts)
            {
                var pso = d as PSObject ?? PSObject.AsPSObject(d);
                var drive = Get<string>(pso, "Drive");
                if (drive is null) continue;
                disks.Add(new DiskSnapshot
                {
                    Drive = drive,
                    TotalBytes = GetNullableLong(pso, "Total") ?? 0,
                    FreeBytes = GetNullableLong(pso, "Free") ?? 0
                });
            }
        }

        var snapshot = new SystemSnapshot
        {
            HostName = Get<string>(o, "HostName") ?? target.HostName,
            OperatingSystem = Get<string>(o, "OS"),
            LastBootTime = GetNullableDate(o, "LastBoot"),
            SystemTime = systemTime,
            ClockSkewSeconds = skew,
            CpuPercent = GetNullableDouble(o, "Cpu"),
            TotalMemoryBytes = GetNullableLong(o, "TotalMemory"),
            FreeMemoryBytes = GetNullableLong(o, "FreeMemory"),
            Disks = disks
        };

        return ConnectorResult<SystemSnapshot>.Ok(snapshot, result.Duration);
    }

    public async Task<ConnectorResult<LogFileInfo>> ResolveLogAsync(
        ConnectorTarget target, string logPathOrPattern, CancellationToken ct = default)
    {
        const string script = """
            param([string]$Motif)
            if ([string]::IsNullOrWhiteSpace($Motif)) {
                [PSCustomObject]@{ Exists = $false; Path = $null; Length = 0; LastWrite = $null }; return
            }
            if ($Motif -notmatch '[\*\?]') {
                if (Test-Path -LiteralPath $Motif) {
                    $i = Get-Item -LiteralPath $Motif
                    [PSCustomObject]@{ Exists = $true; Path = $i.FullName; Length = $i.Length; LastWrite = $i.LastWriteTime }
                } else {
                    [PSCustomObject]@{ Exists = $false; Path = $Motif; Length = 0; LastWrite = $null }
                }
                return
            }
            $recent = Get-ChildItem -Path $Motif -File -ErrorAction SilentlyContinue |
                      Sort-Object LastWriteTime -Descending | Select-Object -First 1
            if ($recent) {
                [PSCustomObject]@{ Exists = $true; Path = $recent.FullName; Length = $recent.Length; LastWrite = $recent.LastWriteTime }
            } else {
                [PSCustomObject]@{ Exists = $false; Path = $null; Length = 0; LastWrite = $null }
            }
            """;

        var result = await ExecuteAsync(target, script,
            new Dictionary<string, object?> { ["Motif"] = logPathOrPattern }, ct);

        if (!result.Succeeded)
            return ConnectorResult<LogFileInfo>.Fail(result.Failure, result.Error!, result.Duration);

        var o = result.Value!.FirstOrDefault();
        var info = new LogFileInfo
        {
            Exists = o is not null && (GetNullableBool(o, "Exists") ?? false),
            Path = o is null ? null : Get<string>(o, "Path"),
            Length = o is null ? 0 : GetNullableLong(o, "Length") ?? 0,
            LastWriteTime = o is null ? null : GetNullableDate(o, "LastWrite")
        };

        return ConnectorResult<LogFileInfo>.Ok(info, result.Duration);
    }

    public async Task<ConnectorResult<LogDelta>> ReadLogDeltaAsync(
        ConnectorTarget target, string logPathOrPattern, long offset,
        int maxBytes = 262_144, CancellationToken ct = default)
    {
        // Reprise fidele de la lecture incrementale des scripts d'exploitation.
        // Le partage ReadWrite + Delete est indispensable : la JVM N4 garde son
        // journal ouvert en ecriture, un Get-Content classique echouerait.
        const string script = """
            param([string]$Motif, [long]$Offset, [int]$MaxBytes)

            $chemin = $Motif
            if ($Motif -match '[\*\?]') {
                $recent = Get-ChildItem -Path $Motif -File -ErrorAction SilentlyContinue |
                          Sort-Object LastWriteTime -Descending | Select-Object -First 1
                if (-not $recent) {
                    [PSCustomObject]@{ Exists = $false; Path = $null; Text = ''; NewOffset = $Offset; Length = 0; Rotated = $false }; return
                }
                $chemin = $recent.FullName
            }

            if (-not (Test-Path -LiteralPath $chemin)) {
                [PSCustomObject]@{ Exists = $false; Path = $chemin; Text = ''; NewOffset = $Offset; Length = 0; Rotated = $false }; return
            }

            $partage = [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete
            $fs = [System.IO.File]::Open($chemin, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, $partage)
            try {
                $longueur = $fs.Length
                $rotation = $false
                $debut = [int64]$Offset
                if ($longueur -lt $debut) { $rotation = $true; $debut = [int64]0 }
                if (($longueur - $debut) -gt $MaxBytes) { $debut = $longueur - $MaxBytes }

                $nb = [int]($longueur - $debut)
                if ($nb -le 0) {
                    [PSCustomObject]@{ Exists = $true; Path = $chemin; Text = ''; NewOffset = $longueur; Length = $longueur; Rotated = $rotation }
                    return
                }

                $fs.Seek($debut, [System.IO.SeekOrigin]::Begin) | Out-Null
                $tampon = New-Object byte[] $nb
                $lus = $fs.Read($tampon, 0, $nb)
                $texte = [System.Text.Encoding]::UTF8.GetString($tampon, 0, $lus)

                [PSCustomObject]@{ Exists = $true; Path = $chemin; Text = $texte
                                   NewOffset = ($debut + $lus); Length = $longueur; Rotated = $rotation }
            } finally {
                $fs.Dispose()
            }
            """;

        var result = await ExecuteAsync(target, script, new Dictionary<string, object?>
        {
            ["Motif"] = logPathOrPattern,
            ["Offset"] = offset,
            ["MaxBytes"] = maxBytes
        }, ct);

        if (!result.Succeeded)
            return ConnectorResult<LogDelta>.Fail(result.Failure, result.Error!, result.Duration);

        var o = result.Value!.FirstOrDefault();
        if (o is null)
            return ConnectorResult<LogDelta>.Fail(
                ConnectorFailure.ErreurDistante, "Aucune reponse a la lecture du journal.", result.Duration);

        var delta = new LogDelta
        {
            ResolvedPath = Get<string>(o, "Path") ?? logPathOrPattern,
            Exists = GetNullableBool(o, "Exists") ?? false,
            Text = Get<string>(o, "Text") ?? string.Empty,
            NewOffset = GetNullableLong(o, "NewOffset") ?? offset,
            Length = GetNullableLong(o, "Length") ?? 0,
            Rotated = GetNullableBool(o, "Rotated") ?? false
        };

        return ConnectorResult<LogDelta>.Ok(delta, result.Duration);
    }

    // -----------------------------------------------------------------------
    // Execution
    // -----------------------------------------------------------------------
    private async Task<ConnectorResult<IReadOnlyList<PSObject>>> ExecuteAsync(
        ConnectorTarget target, string script, Dictionary<string, object?>? parameters, CancellationToken ct)
    {
        var chrono = Stopwatch.StartNew();
        Runspace? runspace = null;

        try
        {
            runspace = target.IsLocal
                ? RunspaceFactory.CreateRunspace()
                : RunspaceFactory.CreateRunspace(BuildConnectionInfo(target));

            runspace.Open();

            using var ps = PowerShell.Create();
            ps.Runspace = runspace;
            ps.AddScript(script);

            if (parameters is not null)
                foreach (var (nom, valeur) in parameters)
                    ps.AddParameter(nom, valeur);

            var sortie = await ps.InvokeAsync().WaitAsync(target.Timeout, ct);

            if (ps.HadErrors && sortie.Count == 0)
            {
                var messages = string.Join(" | ", ps.Streams.Error.Select(e => e.Exception.Message));
                return ConnectorResult<IReadOnlyList<PSObject>>.Fail(
                    ConnectorFailure.ErreurDistante,
                    string.IsNullOrWhiteSpace(messages) ? "Erreur distante sans message." : messages,
                    chrono.Elapsed);
            }

            // Les erreurs non bloquantes accompagnent souvent un resultat
            // partiel exploitable : on les journalise sans invalider la reponse.
            foreach (var erreur in ps.Streams.Error)
                logger.LogDebug("[{Hote}] Erreur non bloquante : {Message}",
                    target.HostName, erreur.Exception.Message);

            return ConnectorResult<IReadOnlyList<PSObject>>.Ok(sortie.ToList(), chrono.Elapsed);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Echec(ConnectorFailure.Timeout,
                $"Aucune reponse de {target.HostName} apres {target.Timeout.TotalSeconds:0} s.", chrono.Elapsed);
        }
        catch (TimeoutException)
        {
            return Echec(ConnectorFailure.Timeout,
                $"Aucune reponse de {target.HostName} apres {target.Timeout.TotalSeconds:0} s.", chrono.Elapsed);
        }
        catch (PSRemotingTransportException ex)
        {
            var (nature, message) = Interpreter(ex, target);
            return Echec(nature, message, chrono.Elapsed);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Echec du connecteur vers {Hote}", target.HostName);
            return Echec(ConnectorFailure.ErreurDistante, ex.Message, chrono.Elapsed);
        }
        finally
        {
            try { runspace?.Dispose(); } catch { /* fermeture au mieux */ }
        }

        static ConnectorResult<IReadOnlyList<PSObject>> Echec(ConnectorFailure f, string m, TimeSpan d) =>
            ConnectorResult<IReadOnlyList<PSObject>>.Fail(f, m, d);
    }

    private static WSManConnectionInfo BuildConnectionInfo(ConnectorTarget target)
    {
        var scheme = target.UseSsl ? "https" : "http";
        var uri = new Uri($"{scheme}://{target.HostName}:{target.WinRmPort}/wsman");

        var info = new WSManConnectionInfo(uri,
            "http://schemas.microsoft.com/powershell/Microsoft.PowerShell",
            target.Credential)
        {
            OperationTimeout = (int)target.Timeout.TotalMilliseconds,
            OpenTimeout = (int)target.Timeout.TotalMilliseconds
        };

        // Sans credential explicite, on utilise l'identite du processus
        // applicatif : c'est le mode retenu quand N4 Sentinel tourne sous un
        // compte de service du domaine, ce qui evite tout mot de passe stocke.
        if (target.Credential is null)
            info.AuthenticationMechanism = AuthenticationMechanism.NegotiateWithImplicitCredential;

        return info;
    }

    /// <summary>
    /// Traduit une panne de transport en cause exploitable. Un operateur qui
    /// lit "acces refuse" sait quoi faire ; devant "erreur distante", non.
    /// </summary>
    private static (ConnectorFailure, string) Interpreter(PSRemotingTransportException ex, ConnectorTarget target)
    {
        // Le message de WinRM est LOCALISE : sur un serveur francais, un refus
        // d'acces s'annonce "Acces refuse" et non "Access is denied". Se fier au
        // seul texte anglais faisait classer un refus d'acces en "injoignable",
        // et afficher un conseil hors sujet a l'operateur. On s'appuie donc
        // d'abord sur le code d'erreur, et le texte n'est qu'un repli - compare
        // sans accents ni casse.
        var code = ex.ErrorCode;
        var texte = SansAccents(ex.Message);

        // 5 = ERROR_ACCESS_DENIED
        if (code == 5 || texte.Contains("access is denied") || texte.Contains("acces refuse")
            || texte.Contains("acces est refuse"))
            return (ConnectorFailure.AccesRefuse,
                $"Acces refuse par {target.HostName}. Le serveur repond, mais il rejette la connexion. " +
                "Le compte d'execution doit etre administrateur local du serveur cible, ou membre du groupe " +
                "'Remote Management Users'. Vers la machine locale, une session elevee est en outre requise.");

        // 1326 = ERROR_LOGON_FAILURE
        if (code == 1326 || texte.Contains("authentication") || texte.Contains("authentification")
            || texte.Contains("logon") || texte.Contains("ouverture de session"))
            return (ConnectorFailure.AuthentificationRefusee,
                $"Authentification refusee par {target.HostName}. Verifiez le compte de service, " +
                "son mot de passe, et qu'il n'est ni verrouille ni expire.");

        // 1722 / 53 = serveur RPC indisponible, chemin reseau introuvable
        if (texte.Contains("cannot be resolved") || texte.Contains("not be resolved")
            || texte.Contains("ne peut pas etre resolu") || texte.Contains("introuvable"))
            return (ConnectorFailure.NomNonResolu,
                $"Nom '{target.HostName}' non resolu. Verifiez l'orthographe, le DNS et le suffixe du domaine.");

        return (ConnectorFailure.Injoignable,
            $"{target.HostName} ne repond pas sur WinRM (port {target.WinRmPort}). " +
            "Sur le serveur cible, en administrateur : Enable-PSRemoting -Force. " +
            $"Detail : {ex.Message}");
    }

    /// <summary>
    /// Normalise un message pour comparaison : minuscules et sans diacritiques.
    /// Les messages systeme varient selon la langue d'installation du serveur ;
    /// une comparaison naive ne survit pas au passage en Production.
    /// </summary>
    private static string SansAccents(string texte)
    {
        var normalise = texte.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(normalise.Length);

        foreach (var c in normalise)
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c)
                != System.Globalization.UnicodeCategory.NonSpacingMark)
                sb.Append(c);

        return sb.ToString().Normalize(System.Text.NormalizationForm.FormC).ToLowerInvariant();
    }

    // -----------------------------------------------------------------------
    // Extraction des proprietes PowerShell
    // -----------------------------------------------------------------------
    private static T? Get<T>(PSObject o, string nom) where T : class
    {
        var v = o.Properties[nom]?.Value;
        return v switch
        {
            null => null,
            T t => t,
            _ => v.ToString() as T
        };
    }

    private static int? GetNullableInt(PSObject o, string nom) =>
        o.Properties[nom]?.Value is { } v && int.TryParse(v.ToString(), out var i) ? i : null;

    private static long? GetNullableLong(PSObject o, string nom) =>
        o.Properties[nom]?.Value is { } v && long.TryParse(v.ToString(), out var l) ? l : null;

    private static double? GetNullableDouble(PSObject o, string nom) =>
        o.Properties[nom]?.Value is { } v &&
        double.TryParse(v.ToString(), System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;

    private static bool? GetNullableBool(PSObject o, string nom) =>
        o.Properties[nom]?.Value is { } v && bool.TryParse(v.ToString(), out var b) ? b : null;

    private static DateTimeOffset? GetNullableDate(PSObject o, string nom) => o.Properties[nom]?.Value switch
    {
        DateTime dt => new DateTimeOffset(dt),
        DateTimeOffset dto => dto,
        { } v when DateTime.TryParse(v.ToString(), out var parsed) => new DateTimeOffset(parsed),
        _ => null
    };
}
