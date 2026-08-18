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
/// TOUT CE QUI EST EXECUTE ICI EST EN LECTURE SEULE, sauf ControlServiceAsync,
/// seule methode qui modifie l'etat du serveur cible.
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

    public async Task<ConnectorResult<IReadOnlyList<ServiceSnapshot>>> ListServicesAsync(
        ConnectorTarget target, IReadOnlyCollection<string> namePatterns, CancellationToken ct = default)
    {
        // Enumeration par motif, sur le nom court ET le nom d'affichage : selon
        // les installations, un meme service N4 se designe par l'un ou l'autre.
        const string script = """
            param([string[]]$Motifs)

            $services = Get-Service -ErrorAction SilentlyContinue | Where-Object {
                $svc = $_
                $Motifs | Where-Object { $svc.Name -like "*$_*" -or $svc.DisplayName -like "*$_*" }
            }

            foreach ($svc in $services) {
                $pidValeur = $null; $startMode = $null
                try {
                    $wmi = Get-CimInstance Win32_Service -Filter "Name='$($svc.Name)'" -ErrorAction SilentlyContinue
                    if ($wmi) { $startMode = $wmi.StartMode; if ($wmi.ProcessId -gt 0) { $pidValeur = $wmi.ProcessId } }
                } catch { }

                [PSCustomObject]@{
                    Name = $svc.Name; DisplayName = $svc.DisplayName; Status = [string]$svc.Status
                    StartMode = $startMode; ProcessId = $pidValeur; WorkingSet = $null; StartTime = $null
                }
            }
            """;

        var result = await ExecuteAsync(target, script,
            new Dictionary<string, object?> { ["Motifs"] = namePatterns.ToArray() }, ct);

        if (!result.Succeeded)
            return ConnectorResult<IReadOnlyList<ServiceSnapshot>>.Fail(result.Failure, result.Error!, result.Duration);

        var snapshots = result.Value!.Select(o => new ServiceSnapshot
        {
            Name = Get<string>(o, "Name") ?? "?",
            DisplayName = Get<string>(o, "DisplayName"),
            Status = Get<string>(o, "Status") ?? "Inconnu",
            StartMode = Get<string>(o, "StartMode"),
            ProcessId = GetNullableInt(o, "ProcessId")
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

    public async Task<ConnectorResult<LiveMetrics>> GetLiveMetricsAsync(
        ConnectorTarget target, CancellationToken ct = default)
    {
        // AUCUN COMPTEUR NOMME. Get-Counter attend des noms traduits selon la
        // langue du serveur - "\Processor(_Total)\% Processor Time" devient
        // "\Processeur(_Total)\% temps processeur" sur une installation
        // francaise, et le releve echoue sans rien dire d'utile. Les classes
        // Win32_PerfFormattedData portent les memes valeurs sous des noms de
        // propriete qui, eux, ne sont jamais traduits.
        const string script = """
            $os = Get-CimInstance Win32_OperatingSystem

            $cpu = $null; $file = $null; $queue = $null
            try {
                $p = Get-CimInstance Win32_PerfFormattedData_PerfOS_Processor -Filter "Name='_Total'" -ErrorAction Stop
                if ($p) { $cpu = [double]$p.PercentProcessorTime }
            } catch {
                try {
                    $cpu = (Get-CimInstance Win32_Processor | Measure-Object -Property LoadPercentage -Average).Average
                } catch { }
            }

            try {
                $sys = Get-CimInstance Win32_PerfFormattedData_PerfOS_System -ErrorAction SilentlyContinue
                if ($sys) { $queue = [double]$sys.ProcessorQueueLength }
            } catch { }

            try {
                $pf = Get-CimInstance Win32_PerfFormattedData_PerfOS_PagingFile -Filter "Name='_Total'" -ErrorAction SilentlyContinue
                if ($pf) { $file = [double]$pf.PercentUsage }
            } catch { }

            $coeurs = $null
            try { $coeurs = (Get-CimInstance Win32_ComputerSystem).NumberOfLogicalProcessors } catch { }

            $disques = @()
            foreach ($d in (Get-CimInstance Win32_LogicalDisk -Filter "DriveType=3")) {
                $disques += [PSCustomObject]@{ Drive = $d.DeviceID; Total = $d.Size; Free = $d.FreeSpace }
            }

            [PSCustomObject]@{
                HostName = $env:COMPUTERNAME
                Cpu = $cpu
                Coeurs = $coeurs
                File = $queue
                TotalMemory = ($os.TotalVisibleMemorySize * 1KB)
                FreeMemory = ($os.FreePhysicalMemory * 1KB)
                PageFile = $file
                SystemTime = (Get-Date)
                LastBoot = $os.LastBootUpTime
                Disks = $disques
            }
            """;

        var reference = DateTimeOffset.UtcNow;
        var result = await ExecuteAsync(target, script, null, ct);

        if (!result.Succeeded)
            return ConnectorResult<LiveMetrics>.Fail(result.Failure, result.Error!, result.Duration);

        var o = result.Value!.FirstOrDefault();
        if (o is null)
            return ConnectorResult<LiveMetrics>.Fail(
                ConnectorFailure.ErreurDistante, "Aucune metrique retournee.", result.Duration);

        var systemTime = GetNullableDate(o, "SystemTime");

        // Meme correction que pour GetSystemAsync : sans deduction de l'aller-
        // retour, une liaison lente ferait apparaitre un ecart qui n'existe pas.
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

        return ConnectorResult<LiveMetrics>.Ok(new LiveMetrics
        {
            HostName = Get<string>(o, "HostName") ?? target.HostName,
            CpuPercent = GetNullableDouble(o, "Cpu"),
            LogicalProcessors = GetNullableInt(o, "Coeurs"),
            ProcessorQueueLength = GetNullableDouble(o, "File"),
            TotalMemoryBytes = GetNullableLong(o, "TotalMemory"),
            FreeMemoryBytes = GetNullableLong(o, "FreeMemory"),
            PageFilePercent = GetNullableDouble(o, "PageFile"),
            SystemTime = systemTime,
            LastBootTime = GetNullableDate(o, "LastBoot"),
            ClockSkewSeconds = skew,
            Disks = disks
        }, result.Duration);
    }

    public async Task<ConnectorResult<TimeSyncSnapshot>> GetTimeSyncAsync(
        ConnectorTarget target, CancellationToken ct = default)
    {
        // Le mode et les pairs sont lus AU REGISTRE : ces valeurs (NT5DS, NTP,
        // NoSync) ne sont jamais traduites, contrairement au texte de
        // "w32tm /query /status" qui l'est integralement. La source effective
        // vient de "w32tm /query /source", dont la sortie est courte et dont on
        // se contente de reproduire le texte sans l'interpreter ici.
        const string script = """
            $svc = Get-Service -Name W32Time -ErrorAction SilentlyContinue
            $statut = if ($svc) { [string]$svc.Status } else { 'Introuvable' }

            $type = $null; $pairs = $null
            try {
                $cle = 'HKLM:\SYSTEM\CurrentControlSet\Services\W32Time\Parameters'
                $p = Get-ItemProperty -Path $cle -ErrorAction Stop
                $type = $p.Type
                $pairs = $p.NtpServer
            } catch { }

            $source = $null
            try {
                $brut = & w32tm /query /source 2>$null
                if ($LASTEXITCODE -eq 0 -and $brut) { $source = ($brut | Select-Object -First 1).Trim() }
            } catch { }

            $derniere = $null
            try {
                $cleEtat = 'HKLM:\SYSTEM\CurrentControlSet\Services\W32Time\Config'
                $e = Get-ItemProperty -Path $cleEtat -ErrorAction SilentlyContinue
                if ($e -and $e.LastKnownGoodTime) { $derniere = $e.LastKnownGoodTime }
            } catch { }

            $cs = Get-CimInstance Win32_ComputerSystem

            [PSCustomObject]@{
                HostName = $env:COMPUTERNAME
                Statut = $statut
                Type = $type
                Pairs = $pairs
                Source = $source
                Derniere = $derniere
                DansDomaine = [bool]$cs.PartOfDomain
                Domaine = $cs.Domain
            }
            """;

        var result = await ExecuteAsync(target, script, null, ct);

        if (!result.Succeeded)
            return ConnectorResult<TimeSyncSnapshot>.Fail(result.Failure, result.Error!, result.Duration);

        var o = result.Value!.FirstOrDefault();
        if (o is null)
            return ConnectorResult<TimeSyncSnapshot>.Fail(
                ConnectorFailure.ErreurDistante, "Aucun etat de synchronisation retourne.", result.Duration);

        return ConnectorResult<TimeSyncSnapshot>.Ok(new TimeSyncSnapshot
        {
            HostName = Get<string>(o, "HostName") ?? target.HostName,
            ServiceStatus = Get<string>(o, "Statut") ?? "Inconnu",
            SyncType = Get<string>(o, "Type"),
            ConfiguredPeers = Get<string>(o, "Pairs"),
            ActualSource = Get<string>(o, "Source"),
            LastSyncTime = GetNullableDate(o, "Derniere"),
            IsDomainMember = GetNullableBool(o, "DansDomaine") ?? false,
            DomainName = Get<string>(o, "Domaine")
        }, result.Duration);
    }

    public async Task<ConnectorResult<UpdateSnapshot>> GetPendingUpdatesAsync(
        ConnectorTarget target, CancellationToken ct = default)
    {
        // LENTE PAR NATURE. L'agent Windows Update interroge WSUS ou le service
        // en ligne. On lui laisse un delai large, tres au-dela de celui des
        // autres appels, et l'appelant sait qu'il ne doit pas la repeter.
        //
        // L'objet COM Microsoft.Update.Session ne franchit pas toujours une
        // session WinRM - restriction dite du double saut. L'echec est donc
        // traite comme un cas normal, avec un message qui dit quoi faire,
        // plutot que comme une panne.
        const string script = """
            $redemarrage = $false
            foreach ($c in @(
                'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired',
                'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending')) {
                if (Test-Path $c) { $redemarrage = $true }
            }

            $derniere = $null
            try {
                $auto = New-Object -ComObject Microsoft.Update.AutoUpdate
                if ($auto.Results -and $auto.Results.LastInstallationSuccessDate) {
                    $derniere = $auto.Results.LastInstallationSuccessDate
                }
            } catch { }

            try {
                $session = New-Object -ComObject Microsoft.Update.Session
                $chercheur = $session.CreateUpdateSearcher()
                $resultat = $chercheur.Search("IsInstalled=0 and IsHidden=0")

                $total = $resultat.Updates.Count
                $securite = 0; $critiques = 0; $titres = @()

                for ($i = 0; $i -lt $total; $i++) {
                    $u = $resultat.Updates.Item($i)
                    foreach ($cat in $u.Categories) {
                        if ($cat.Name -match 'Securit|Security') { $securite++; break }
                    }
                    if ($u.MsrcSeverity -eq 'Critical') { $critiques++ }
                    if ($titres.Count -lt 5) { $titres += $u.Title }
                }

                [PSCustomObject]@{
                    HostName = $env:COMPUTERNAME; Total = $total; Securite = $securite
                    Critiques = $critiques; Redemarrage = $redemarrage; Titres = $titres
                    Derniere = $derniere; Erreur = $null
                }
            } catch {
                [PSCustomObject]@{
                    HostName = $env:COMPUTERNAME; Total = -1; Securite = 0; Critiques = 0
                    Redemarrage = $redemarrage; Titres = @(); Derniere = $derniere
                    Erreur = $_.Exception.Message
                }
            }
            """;

        // Cible temporaire au delai elargi : la recherche depasse largement les
        // trente secondes habituelles.
        var cibleLente = target with { Timeout = TimeSpan.FromMinutes(3) };

        var result = await ExecuteAsync(cibleLente, script, null, ct);

        if (!result.Succeeded)
            return ConnectorResult<UpdateSnapshot>.Fail(result.Failure, result.Error!, result.Duration);

        var o = result.Value!.FirstOrDefault();
        if (o is null)
            return ConnectorResult<UpdateSnapshot>.Fail(
                ConnectorFailure.ErreurDistante, "Aucune reponse de l'agent Windows Update.", result.Duration);

        var total = GetNullableInt(o, "Total") ?? -1;

        if (total < 0)
        {
            var detail = Get<string>(o, "Erreur");
            return ConnectorResult<UpdateSnapshot>.Fail(
                ConnectorFailure.AccesRefuse,
                "L'agent Windows Update n'a pas pu etre interroge a distance. "
                + "L'objet COM Microsoft.Update.Session ne franchit pas une session WinRM sans "
                + "delegation configuree (restriction du double saut). "
                + "Executez le releve depuis le serveur lui-meme, ou configurez CredSSP. "
                + (detail is null ? string.Empty : $"Detail : {detail}"),
                result.Duration);
        }

        var titres = new List<string>();
        if (o.Properties["Titres"]?.Value is IEnumerable<object> bruts)
            foreach (var t in bruts)
                if (t?.ToString() is { Length: > 0 } titre) titres.Add(titre);

        return ConnectorResult<UpdateSnapshot>.Ok(new UpdateSnapshot
        {
            HostName = Get<string>(o, "HostName") ?? target.HostName,
            PendingCount = total,
            SecurityCount = GetNullableInt(o, "Securite") ?? 0,
            CriticalCount = GetNullableInt(o, "Critiques") ?? 0,
            RebootPending = GetNullableBool(o, "Redemarrage") ?? false,
            LastInstallationDate = GetNullableDate(o, "Derniere"),
            Sample = titres
        }, result.Duration);
    }

    public async Task<ConnectorResult<ServiceSnapshot>> ControlServiceAsync(
        ConnectorTarget target, string serviceName, ServiceControlAction action,
        CancellationToken ct = default)
    {
        // -Force sur l'arret : un service N4 a des services dependants declares,
        // et sans -Force Windows refuse purement et simplement.
        //
        // Le script N'ATTEND PAS que le service atteigne son etat cible. Il emet
        // la commande, laisse a Windows le temps de la prendre en compte, et
        // rend l'etat observe. L'attente reelle - et surtout la preuve par le
        // journal - appartient au moteur d'orchestration, qui sait afficher la
        // progression a l'operateur pendant ce temps.
        const string script = """
            param([string]$Nom, [string]$Action)

            $svc = Get-Service -Name $Nom -ErrorAction SilentlyContinue
            if (-not $svc) {
                $svc = Get-Service -ErrorAction SilentlyContinue |
                       Where-Object { $_.DisplayName -eq $Nom } | Select-Object -First 1
            }
            if (-not $svc) {
                [PSCustomObject]@{ Name = $Nom; DisplayName = $null; Status = 'Introuvable'
                                   Accepte = $false; Message = "Le service '$Nom' n'existe pas sur $env:COMPUTERNAME." }
                return
            }

            $message = $null
            $accepte = $true
            try {
                if ($Action -eq 'Demarrer') {
                    if ($svc.Status -eq 'Running') {
                        $message = 'Le service etait deja en cours d''execution.'
                    } else {
                        Start-Service -Name $svc.Name -ErrorAction Stop
                    }
                } else {
                    if ($svc.Status -eq 'Stopped') {
                        $message = 'Le service etait deja a l''arret.'
                    } else {
                        Stop-Service -Name $svc.Name -Force -ErrorAction Stop
                    }
                }
            } catch {
                $accepte = $false
                $message = $_.Exception.Message
            }

            Start-Sleep -Milliseconds 800
            $svc.Refresh()

            $pidValeur = $null
            try {
                $wmi = Get-CimInstance Win32_Service -Filter "Name='$($svc.Name)'" -ErrorAction SilentlyContinue
                if ($wmi -and $wmi.ProcessId -gt 0) { $pidValeur = $wmi.ProcessId }
            } catch { }

            [PSCustomObject]@{
                Name = $svc.Name; DisplayName = $svc.DisplayName; Status = [string]$svc.Status
                ProcessId = $pidValeur; Accepte = $accepte; Message = $message
            }
            """;

        logger.LogInformation("[{Hote}] {Action} du service {Service}", target.HostName, action, serviceName);

        var result = await ExecuteAsync(target, script, new Dictionary<string, object?>
        {
            ["Nom"] = serviceName,
            ["Action"] = action.ToString()
        }, ct);

        if (!result.Succeeded)
            return ConnectorResult<ServiceSnapshot>.Fail(result.Failure, result.Error!, result.Duration);

        var o = result.Value!.FirstOrDefault();
        if (o is null)
            return ConnectorResult<ServiceSnapshot>.Fail(
                ConnectorFailure.ErreurDistante,
                $"Aucune reponse a la commande {action} sur '{serviceName}'.", result.Duration);

        var statut = Get<string>(o, "Status") ?? "Inconnu";

        if (string.Equals(statut, "Introuvable", StringComparison.OrdinalIgnoreCase))
            return ConnectorResult<ServiceSnapshot>.Fail(
                ConnectorFailure.CibleIntrouvable,
                Get<string>(o, "Message") ?? $"Service '{serviceName}' introuvable.", result.Duration);

        if (GetNullableBool(o, "Accepte") == false)
            return ConnectorResult<ServiceSnapshot>.Fail(
                ConnectorFailure.ErreurDistante,
                Get<string>(o, "Message") ?? $"Commande {action} refusee sur '{serviceName}'.", result.Duration);

        var snapshot = new ServiceSnapshot
        {
            Name = Get<string>(o, "Name") ?? serviceName,
            DisplayName = Get<string>(o, "DisplayName"),
            Status = statut,
            ProcessId = GetNullableInt(o, "ProcessId")
        };

        return ConnectorResult<ServiceSnapshot>.Ok(snapshot, result.Duration);
    }

    public async Task<ConnectorResult<FolderSnapshot>> ListFilesAsync(
        ConnectorTarget target, string path, CancellationToken ct = default)
    {
        const string script = """
            param([string]$Chemin)

            if ([string]::IsNullOrWhiteSpace($Chemin) -or -not (Test-Path -LiteralPath $Chemin -PathType Container)) {
                [PSCustomObject]@{ Existe = $false; Fichiers = @() }
                return
            }

            $fichiers = @()
            foreach ($f in (Get-ChildItem -LiteralPath $Chemin -File -ErrorAction SilentlyContinue)) {
                $verrouille = $false
                try {
                    $flux = [System.IO.File]::Open(
                        $f.FullName, [System.IO.FileMode]::Open,
                        [System.IO.FileAccess]::Read, [System.IO.FileShare]::None)
                    $flux.Close()
                } catch {
                    $verrouille = $true
                }

                $fichiers += [PSCustomObject]@{
                    Nom = $f.Name; Taille = $f.Length
                    DerniereEcriture = $f.LastWriteTimeUtc; Verrouille = $verrouille
                }
            }

            [PSCustomObject]@{ Existe = $true; Fichiers = $fichiers }
            """;

        var result = await ExecuteAsync(target, script, new Dictionary<string, object?> { ["Chemin"] = path }, ct);
        if (!result.Succeeded)
            return ConnectorResult<FolderSnapshot>.Fail(result.Failure, result.Error!, result.Duration);

        var o = result.Value!.FirstOrDefault();
        if (o is null)
            return ConnectorResult<FolderSnapshot>.Fail(
                ConnectorFailure.ErreurDistante, "Aucune donnée retournée.", result.Duration);

        var fichiers = new List<RemoteFileInfo>();
        if (o.Properties["Fichiers"]?.Value is IEnumerable<object> bruts)
        {
            foreach (var brut in bruts)
            {
                var pso = brut as PSObject ?? PSObject.AsPSObject(brut);
                var nom = Get<string>(pso, "Nom");
                if (nom is null) continue;

                fichiers.Add(new RemoteFileInfo
                {
                    Name = nom,
                    SizeBytes = GetNullableLong(pso, "Taille") ?? 0,
                    LastWriteTime = GetNullableDate(pso, "DerniereEcriture") ?? DateTimeOffset.MinValue,
                    IsLocked = GetNullableBool(pso, "Verrouille") ?? false
                });
            }
        }

        var snapshot = new FolderSnapshot
        {
            Path = path,
            Exists = GetNullableBool(o, "Existe") ?? false,
            Files = fichiers
        };

        return ConnectorResult<FolderSnapshot>.Ok(snapshot, result.Duration);
    }

    public async Task<ConnectorResult<WriteProbeResult>> ProbeWriteAsync(
        ConnectorTarget target, string path, CancellationToken ct = default)
    {
        // Marqueur au nom unique : jamais de collision avec un fichier
        // existant, jamais rien touché d'autre que ce que ce test a créé.
        const string script = """
            param([string]$Chemin)

            if ([string]::IsNullOrWhiteSpace($Chemin) -or -not (Test-Path -LiteralPath $Chemin -PathType Container)) {
                [PSCustomObject]@{ Reussi = $false; Message = "Dossier introuvable." }
                return
            }

            $marqueur = Join-Path $Chemin (".n4sentinel-probe-" + [Guid]::NewGuid().ToString("N"))
            try {
                $contenu = [Guid]::NewGuid().ToString("N")
                [System.IO.File]::WriteAllText($marqueur, $contenu)
                $relu = [System.IO.File]::ReadAllText($marqueur)
                Remove-Item -LiteralPath $marqueur -Force -ErrorAction Stop

                if ($relu -ne $contenu) {
                    [PSCustomObject]@{ Reussi = $false; Message = "Contenu relu différent de celui écrit." }
                } else {
                    [PSCustomObject]@{ Reussi = $true; Message = $null }
                }
            } catch {
                try { Remove-Item -LiteralPath $marqueur -Force -ErrorAction SilentlyContinue } catch { }
                [PSCustomObject]@{ Reussi = $false; Message = $_.Exception.Message }
            }
            """;

        var result = await ExecuteAsync(target, script, new Dictionary<string, object?> { ["Chemin"] = path }, ct);
        if (!result.Succeeded)
            return ConnectorResult<WriteProbeResult>.Fail(result.Failure, result.Error!, result.Duration);

        var o = result.Value!.FirstOrDefault();
        if (o is null)
            return ConnectorResult<WriteProbeResult>.Fail(
                ConnectorFailure.ErreurDistante, "Aucune donnée retournée.", result.Duration);

        var probe = new WriteProbeResult
        {
            CanWrite = GetNullableBool(o, "Reussi") ?? false,
            Error = Get<string>(o, "Message")
        };

        return ConnectorResult<WriteProbeResult>.Ok(probe, result.Duration);
    }

    // -----------------------------------------------------------------------
    // Execution
    // -----------------------------------------------------------------------
    private async Task<ConnectorResult<IReadOnlyList<PSObject>>> ExecuteAsync(
        ConnectorTarget target, string script, Dictionary<string, object?>? parameters, CancellationToken ct)
    {
        var chrono = Stopwatch.StartNew();
        Runspace? runspace = null;
        string maskedScript = N4Sentinel.Infrastructure.Diagnostic.SecretMasker.Masquer(script).Texte;

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
                    chrono.Elapsed,
                    maskedScript);
            }

            // Les erreurs non bloquantes accompagnent souvent un resultat
            // partiel exploitable : on les journalise sans invalider la reponse.
            foreach (var erreur in ps.Streams.Error)
                logger.LogDebug("[{Hote}] Erreur non bloquante : {Message}",
                    target.HostName, erreur.Exception.Message);

            return ConnectorResult<IReadOnlyList<PSObject>>.Ok(sortie.ToList(), chrono.Elapsed, maskedScript);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Echec(ConnectorFailure.Timeout,
                $"Aucune reponse de {target.HostName} apres {target.Timeout.TotalSeconds:0} s.", chrono.Elapsed, maskedScript);
        }
        catch (TimeoutException)
        {
            return Echec(ConnectorFailure.Timeout,
                $"Aucune reponse de {target.HostName} apres {target.Timeout.TotalSeconds:0} s.", chrono.Elapsed, maskedScript);
        }
        catch (PSRemotingTransportException ex)
        {
            var (nature, message) = Interpreter(ex, target);
            return Echec(nature, message, chrono.Elapsed, maskedScript);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Echec du connecteur vers {Hote}", target.HostName);
            return Echec(ConnectorFailure.ErreurDistante, ex.Message, chrono.Elapsed, maskedScript);
        }
        finally
        {
            try { runspace?.Dispose(); } catch { /* fermeture au mieux */ }
        }

        static ConnectorResult<IReadOnlyList<PSObject>> Echec(ConnectorFailure f, string m, TimeSpan d, string masked) =>
            ConnectorResult<IReadOnlyList<PSObject>>.Fail(f, m, d, masked);
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
