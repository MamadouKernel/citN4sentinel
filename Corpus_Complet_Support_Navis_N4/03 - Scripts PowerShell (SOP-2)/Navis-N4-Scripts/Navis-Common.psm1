<#
.SYNOPSIS
    Module commun pour les scripts d'exploitation Navis N4 (SOP-2).

.DESCRIPTION
    Fournit :
      - la journalisation standardisee (Write-N4Log, New-N4LogSession)
      - le chargement de la configuration externe (Navis-Config.json)
      - les verifications reseau / services / base de donnees
      - LA PREUVE DE DEMARRAGE PAR LE LOG APPLICATIF :
        Wait-N4ComponentReady, Start-N4Component, Stop-N4Component
      - la protection d'integrite + code PIN

    A placer dans le meme dossier que les autres scripts, et importer avec :
        Import-Module .\Navis-Common.psm1 -Force

.NOTES
    Copyright : (c) KMKernel
    Auteur   : Mamadou KONATE
    Portee   : SOP-2 - Niveau 2/3 uniquement. Ne pas executer sans habilitation.

    PRINCIPE DIRECTEUR DE CE MODULE
    -------------------------------
    Un service Windows "Running" ne prouve PAS qu'un composant N4 est
    operationnel. Le service passe Running en quelques secondes ; la JVM N4,
    elle, met souvent plusieurs minutes a charger sa configuration, ouvrir la
    base, rejoindre le cluster Hazelcast et initialiser son tier web.
    C'est pourquoi toute attente de demarrage se fait ici en DEUX temps :
        1. le service Windows atteint Running        (preuve faible)
        2. le LOG APPLICATIF affiche le marqueur de fin d'initialisation
           configure pour ce composant                (preuve reelle)
    Si le log ne peut pas servir de preuve (chemin non configure, fichier
    inaccessible, marqueur jamais vu), l'etat retourne est "Unknown" -
    JAMAIS "Ready". C'est au script appelant de decider quoi faire, et a
    l'operateur de trancher. On n'affirme pas ce qu'on n'a pas prouve.
#>

# ============================================================
# CHARGEMENT DE LA CONFIGURATION EXTERNE (Navis-Config.json)
# ============================================================
# La liste des serveurs, services, seuils et marqueurs de log se modifie
# UNIQUEMENT dans Navis-Config.json - aucune edition de code necessaire,
# quel que soit le nombre de serveurs (2, 5, 20...).
# Le fichier doit se trouver dans le meme dossier que ce module, sauf si
# la variable d'environnement NAVIS_N4_CONFIG_PATH pointe vers un autre
# emplacement (utile pour distinguer plusieurs environnements : dev/test/prod).

function ConvertTo-N4Hashtable {
    <#
    .SYNOPSIS
        Convertit recursivement un objet issu de ConvertFrom-Json (PSCustomObject)
        en hashtable, et garantit que les tableaux restent des tableaux meme
        s'ils ne contiennent qu'un seul element (limitation connue de
        ConvertFrom-Json sur certaines versions de PowerShell).
    #>
    param($InputObject)

    if ($InputObject -is [System.Management.Automation.PSCustomObject]) {
        $hash = @{}
        foreach ($prop in $InputObject.PSObject.Properties) {
            $hash[$prop.Name] = ConvertTo-N4Hashtable -InputObject $prop.Value
        }
        return $hash
    } elseif ($InputObject -is [System.Collections.IEnumerable] -and $InputObject -isnot [string]) {
        $arr = @()
        foreach ($item in $InputObject) { $arr += , (ConvertTo-N4Hashtable -InputObject $item) }
        return , $arr
    } else {
        return $InputObject
    }
}

$configPath = $env:NAVIS_N4_CONFIG_PATH
if (-not $configPath) {
    $configPath = Join-Path $PSScriptRoot "Navis-Config.json"
}

if (-not (Test-Path $configPath)) {
    throw "Fichier de configuration introuvable : $configPath`nCreez Navis-Config.json (voir Navis-Config.example.json) ou definissez la variable d'environnement NAVIS_N4_CONFIG_PATH."
}

try {
    $rawJson = Get-Content -Path $configPath -Raw -Encoding UTF8
    $parsed = $rawJson | ConvertFrom-Json -ErrorAction Stop
    $Global:N4Config = ConvertTo-N4Hashtable -InputObject $parsed

    # Garde-fou explicite : ClusterNodes doit toujours etre un tableau,
    # meme si le fichier JSON n'en liste qu'un seul.
    if ($Global:N4Config.ClusterNodes -isnot [System.Array]) {
        $Global:N4Config.ClusterNodes = @($Global:N4Config.ClusterNodes)
    }

    Write-Host "[Navis-Common] Configuration chargee depuis : $configPath ($($Global:N4Config.ClusterNodes.Count) noeud(s) Cluster)" -ForegroundColor DarkGray
} catch {
    throw "Echec du chargement de la configuration ($configPath) : $($_.Exception.Message)"
}

# ============================================================
# JOURNALISATION
# ============================================================
function Write-N4Log {
    <#
    .SYNOPSIS
        Ecrit une ligne de log horodatee, a l'ecran et dans un fichier.
    .PARAMETER Message
        Le message a consigner.
    .PARAMETER Level
        INFO, WARN, ERROR, ACTION ou OK. Determine la couleur console.
    .PARAMETER LogFile
        Chemin du fichier log. Si absent, utilise le fichier de session en cours.
    #>
    param(
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Message,
        [ValidateSet("INFO", "WARN", "ERROR", "ACTION", "OK")][string]$Level = "INFO",
        [string]$LogFile = $Global:N4CurrentLogFile
    )

    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $line = "[$timestamp] [$Level] $Message"

    switch ($Level) {
        "ERROR"  { Write-Host $line -ForegroundColor Red }
        "WARN"   { Write-Host $line -ForegroundColor Yellow }
        "ACTION" { Write-Host $line -ForegroundColor Cyan }
        "OK"     { Write-Host $line -ForegroundColor Green }
        default  { Write-Host $line }
    }

    if ($LogFile) {
        try {
            Add-Content -Path $LogFile -Value $line -Encoding UTF8
        } catch {
            Write-Host "[$timestamp] [ERROR] Impossible d'ecrire dans le log $LogFile : $($_.Exception.Message)" -ForegroundColor Red
        }
    }
}

function Write-N4Instruction {
    <#
    .SYNOPSIS
        Affiche un bloc d'instructions operateur (encadre, lisible en console
        ET consigne dans le log). Sert a expliquer QUOI faire, OU regarder et
        QUEL critere valide l'etape - pas seulement a signaler un evenement.
    .PARAMETER Title
        Titre du bloc.
    .PARAMETER Lines
        Lignes d'instruction (tableau de chaines).
    .PARAMETER Level
        Niveau de log applique aux lignes (INFO par defaut, WARN si l'operateur
        doit agir ou verifier avant de poursuivre).
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Title,
        [Parameter(Mandatory = $true)][string[]]$Lines,
        [ValidateSet("INFO", "WARN", "ERROR", "ACTION", "OK")][string]$Level = "INFO"
    )

    Write-N4Log -Message ("+-- " + $Title + " " + ("-" * [Math]::Max(0, 60 - $Title.Length))) -Level $Level
    foreach ($l in $Lines) {
        Write-N4Log -Message ("|   " + $l) -Level $Level
    }
    Write-N4Log -Message ("+" + ("-" * 64)) -Level $Level
}

function Get-N4LocalIPAddress {
    <#
    .SYNOPSIS
        Retourne la premiere adresse IPv4 non-loopback de la machine locale.
    #>
    try {
        $ip = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction Stop |
            Where-Object { $_.IPAddress -notlike "127.*" -and $_.PrefixOrigin -ne "WellKnown" } |
            Select-Object -First 1 -ExpandProperty IPAddress
        if (-not $ip) { $ip = "IP non determinee" }
        return $ip
    } catch {
        # Repli pour les machines sans le module NetTCPIP (versions PowerShell anciennes)
        try {
            $ip = ([System.Net.Dns]::GetHostAddresses($env:COMPUTERNAME) |
                Where-Object { $_.AddressFamily -eq "InterNetwork" } |
                Select-Object -First 1).IPAddressToString
            if (-not $ip) { $ip = "IP non determinee" }
            return $ip
        } catch {
            return "IP non determinee"
        }
    }
}

function New-N4LogSession {
    <#
    .SYNOPSIS
        Cree un nouveau fichier de log horodate pour la session en cours,
        et consigne QUI execute le script et DEPUIS QUELLE IP.
    .PARAMETER ScriptName
        Nom court du script (utilise dans le nom de fichier).
    #>
    param([Parameter(Mandatory = $true)][string]$ScriptName)

    $folder = $Global:N4Config.LocalLogFolder
    if (-not (Test-Path $folder)) {
        New-Item -Path $folder -ItemType Directory -Force | Out-Null
    }
    $stamp = Get-Date -Format "yyyyMMdd_HHmmss"
    $file = Join-Path $folder "$ScriptName`_$stamp.log"
    $Global:N4CurrentLogFile = $file

    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
    $ip = Get-N4LocalIPAddress

    Write-N4Log -Message "=== Nouvelle session : $ScriptName ===" -Level INFO -LogFile $file
    Write-N4Log -Message "Utilisateur execution : $identity" -Level INFO -LogFile $file
    Write-N4Log -Message "Machine execution     : $env:COMPUTERNAME" -Level INFO -LogFile $file
    Write-N4Log -Message "Adresse IP execution  : $ip" -Level INFO -LogFile $file
    Write-N4Log -Message "Config utilisee       : $configPath" -Level INFO -LogFile $file
    Write-N4Log -Message "Copyright : (c) KMKernel" -Level INFO -LogFile $file
    return $file
}

function Format-N4Duration {
    <#
    .SYNOPSIS
        Formate une duree en secondes sous forme lisible (ex : "7m 12s").
    #>
    param([Parameter(Mandatory = $true)][int]$Seconds)
    # [int] arrondit au lieu de tronquer : [int]1.5 vaut 2. On utilise donc
    # Floor, sans quoi 90 secondes s'affichent "2m 30s".
    $ts = [TimeSpan]::FromSeconds($Seconds)
    if ($ts.TotalHours -ge 1) { return ("{0}h {1:00}m {2:00}s" -f [Math]::Floor($ts.TotalHours), $ts.Minutes, $ts.Seconds) }
    if ($ts.TotalMinutes -ge 1) { return ("{0}m {1:00}s" -f [Math]::Floor($ts.TotalMinutes), $ts.Seconds) }
    return ("{0}s" -f $ts.Seconds)
}

# ============================================================
# PARAMETRES DE DEMARRAGE / PREUVE PAR LE LOG (section Readiness)
# ============================================================
# Valeurs utilisees si Navis-Config.json ne definit rien : elles restent
# volontairement GENEREUSES. Un timeout trop court transforme un demarrage
# lent (mais normal) en faux incident, et pousse l'operateur a relancer un
# composant qui etait simplement en cours de chargement - ce qui aggrave la
# situation. Mieux vaut attendre 25 minutes et savoir, que declarer un echec
# au bout de 3 minutes et deviner.

$script:N4ReadinessDefaults = @{
    ServiceRunningTimeoutSeconds = 300      # 5 min  : le service Windows doit au moins passer Running
    LogReadyTimeoutSeconds       = 1800     # 30 min : temps laisse a la JVM pour finir son initialisation
    StopTimeoutSeconds           = 600      # 10 min : arret propre (vidange des files, flush)
    PollIntervalSeconds          = 10       # frequence d'interrogation du serveur distant
    ProgressEverySeconds         = 60       # frequence des messages "toujours en attente..."
    PostReadySettleSeconds       = 0        # observation supplementaire apres le marqueur (0 = desactive)
    LogTailBytes                 = 262144   # volume max de log relu par passe (256 Ko)
    LogPath                      = $null
    ReadyPatterns                = @()
    ErrorPatterns                = @()
    IgnorePatterns               = @()
}

function Get-N4ReadinessSettings {
    <#
    .SYNOPSIS
        Retourne les parametres d'attente effectifs d'un composant, par fusion
        de trois niveaux : valeurs internes du module < Readiness.Defaults du
        fichier de config < Readiness.Components.<Cle> du fichier de config.
    .PARAMETER ComponentKey
        Cle du composant : Cluster, Center, Standby, Bridge, XPS, ECN4, ECN4Web.
    .OUTPUTS
        Hashtable des parametres effectifs.
    #>
    param([Parameter(Mandatory = $true)][string]$ComponentKey)

    $settings = @{}
    foreach ($k in $script:N4ReadinessDefaults.Keys) { $settings[$k] = $script:N4ReadinessDefaults[$k] }

    $readiness = $Global:N4Config.Readiness
    if ($readiness) {
        if ($readiness.Defaults) {
            foreach ($k in $readiness.Defaults.Keys) {
                if ($k -notlike "_*") { $settings[$k] = $readiness.Defaults[$k] }
            }
        }
        if ($readiness.Components -and $readiness.Components.ContainsKey($ComponentKey)) {
            $comp = $readiness.Components[$ComponentKey]
            foreach ($k in $comp.Keys) {
                if ($k -notlike "_*") { $settings[$k] = $comp[$k] }
            }
        }
    }

    # Normalisation : les listes de motifs doivent toujours etre des tableaux,
    # meme si le JSON n'en contient qu'un seul element.
    foreach ($listKey in @("ReadyPatterns", "ErrorPatterns", "IgnorePatterns")) {
        if ($null -eq $settings[$listKey]) { $settings[$listKey] = @() }
        elseif ($settings[$listKey] -isnot [System.Array]) { $settings[$listKey] = @($settings[$listKey]) }
    }

    $settings["ComponentKey"] = $ComponentKey
    return $settings
}

# ============================================================
# LECTURE INCREMENTALE D'UN LOG DISTANT
# ============================================================
# On NE fait PAS de Get-Content -Wait (qui bloquerait la session distante).
# On memorise un offset en octets, et a chaque passe on ne relit que ce qui
# a ete ecrit depuis la passe precedente. Le fichier est ouvert en
# FileShare ReadWrite+Delete : indispensable, car la JVM N4 garde son log
# ouvert en ecriture et un Get-Content classique echouerait ou verrouillerait.

function Get-N4LogReaderScriptBlock {
    <#
    .SYNOPSIS
        Retourne le ScriptBlock (execute sur le serveur cible) qui lit la
        portion de log ecrite depuis un offset donne.
        Renvoie : Exists, Text, NewOffset, Length, Rotated, Error.
    #>
    return {
        param($path, $offset, $maxBytes)

        if ([string]::IsNullOrWhiteSpace($path) -or -not (Test-Path -LiteralPath $path)) {
            return [PSCustomObject]@{
                Exists = $false; Text = ""; NewOffset = [int64]$offset
                Length = [int64]0; Rotated = $false; Error = $null
            }
        }

        try {
            $share = [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete
            $fs = [System.IO.File]::Open($path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, $share)
        } catch {
            return [PSCustomObject]@{
                Exists = $true; Text = ""; NewOffset = [int64]$offset
                Length = [int64]0; Rotated = $false; Error = $_.Exception.Message
            }
        }

        try {
            $len = $fs.Length
            $rotated = $false
            $start = [int64]$offset

            # Fichier plus court qu'a la passe precedente = rotation du log :
            # on repart du debut du nouveau fichier.
            if ($len -lt $start) { $rotated = $true; $start = [int64]0 }

            # Garde-fou volume : on ne rapatrie jamais plus que maxBytes par passe.
            if (($len - $start) -gt $maxBytes) { $start = $len - $maxBytes }

            $count = [int]($len - $start)
            if ($count -le 0) {
                return [PSCustomObject]@{
                    Exists = $true; Text = ""; NewOffset = $len
                    Length = $len; Rotated = $rotated; Error = $null
                }
            }

            $fs.Seek($start, [System.IO.SeekOrigin]::Begin) | Out-Null
            $buffer = New-Object byte[] $count
            $read = $fs.Read($buffer, 0, $count)
            $text = [System.Text.Encoding]::UTF8.GetString($buffer, 0, $read)

            [PSCustomObject]@{
                Exists = $true; Text = $text; NewOffset = ($start + $read)
                Length = $len; Rotated = $rotated; Error = $null
            }
        } catch {
            [PSCustomObject]@{
                Exists = $true; Text = ""; NewOffset = [int64]$offset
                Length = [int64]0; Rotated = $false; Error = $_.Exception.Message
            }
        } finally {
            $fs.Dispose()
        }
    }
}

function Get-N4LogResolverScriptBlock {
    <#
    .SYNOPSIS
        Retourne le ScriptBlock (execute sur le serveur cible) qui resout un
        chemin de log en fichier concret.
    .DESCRIPTION
        Indispensable car plusieurs composants N4 n'ecrivent PAS dans un
        fichier au nom fixe : le log XPS s'appelle xps_AAAAMMJJHHMMSS et
        REPART A ZERO A CHAQUE DEMARRAGE ; les logs Bridge, ECN4 et ECN4Web
        portent egalement un suffixe de date. Un LogPath contenant un
        caractere generique (*) est donc resolu vers le fichier le PLUS
        RECENT qui correspond, a chaque interrogation.
        Renvoie : Exists, Path, Length.
    #>
    return {
        param($pattern)

        if ([string]::IsNullOrWhiteSpace($pattern)) {
            return [PSCustomObject]@{ Exists = $false; Path = $null; Length = [int64]0 }
        }

        if ($pattern -notmatch '[\*\?]') {
            if (Test-Path -LiteralPath $pattern) {
                $i = Get-Item -LiteralPath $pattern
                return [PSCustomObject]@{ Exists = $true; Path = $i.FullName; Length = [int64]$i.Length }
            }
            return [PSCustomObject]@{ Exists = $false; Path = $pattern; Length = [int64]0 }
        }

        $newest = Get-ChildItem -Path $pattern -File -ErrorAction SilentlyContinue |
                  Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($newest) {
            return [PSCustomObject]@{ Exists = $true; Path = $newest.FullName; Length = [int64]$newest.Length }
        }
        return [PSCustomObject]@{ Exists = $false; Path = $null; Length = [int64]0 }
    }
}

function New-N4LogWatchpoint {
    <#
    .SYNOPSIS
        Pose un point de reference sur le log d'un composant AVANT de lancer
        une action, pour ne compter ensuite QUE les lignes ecrites apres.
    .DESCRIPTION
        C'est le garde-fou anti-faux-positif : sans watchpoint, le script
        pourrait relire le marqueur de succes du demarrage PRECEDENT (encore
        present dans le fichier) et conclure a tort que le composant est pret.
        A appeler systematiquement AVANT Start-Service / Restart-Service.

        Si LogPath contient un caractere generique (*), le fichier le plus
        recent est retenu. Un nouveau fichier apparaissant apres le demarrage
        (cas du log XPS, recree a chaque lancement) est detecte et analyse
        depuis son debut.
    .PARAMETER ComputerName
        Serveur portant le log.
    .PARAMETER LogPath
        Chemin LOCAL du log sur ce serveur (tel que vu par le serveur lui-meme),
        eventuellement avec un caractere generique.
    .OUTPUTS
        Hashtable : ComputerName, LogPath (motif), ResolvedPath, Offset, Existed.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$ComputerName,
        [string]$LogPath,
        [hashtable]$IcmParams = @{}
    )

    $watch = @{
        ComputerName = $ComputerName; LogPath = $LogPath; ResolvedPath = $LogPath
        Offset = [int64]0; Existed = $false
    }
    if ([string]::IsNullOrWhiteSpace($LogPath)) { return $watch }

    try {
        $info = Invoke-Command -ComputerName $ComputerName @IcmParams `
                    -ScriptBlock (Get-N4LogResolverScriptBlock) -ArgumentList $LogPath -ErrorAction Stop

        $watch.Offset       = [int64]$info.Length
        $watch.Existed      = [bool]$info.Exists
        if ($info.Path) { $watch.ResolvedPath = $info.Path }

        if ($info.Exists) {
            Write-N4Log -Message "Point de reference pose sur $($info.Path) ($ComputerName) : seules les lignes ecrites a partir d'ici seront analysees." -Level INFO
        } else {
            Write-N4Log -Message "Aucun fichier ne correspond encore a '$LogPath' sur $ComputerName : il sera analyse des sa creation par le service." -Level INFO
        }
    } catch {
        Write-N4Log -Message "Impossible de poser le point de reference sur '$LogPath' ($ComputerName) : $($_.Exception.Message). L'analyse repartira de l'offset 0." -Level WARN
    }

    return $watch
}

function Wait-N4ServiceRunning {
    <#
    .SYNOPSIS
        Attend qu'un service Windows DISTANT atteigne l'etat Running.
        Preuve FAIBLE : a completer par Wait-N4LogReady.
    .OUTPUTS
        PSCustomObject : Running (bool), Status, ElapsedSeconds, Reason.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$ComputerName,
        [Parameter(Mandatory = $true)][string]$ServiceName,
        [string]$Label = "",
        [int]$TimeoutSeconds = 300,
        [int]$PollIntervalSeconds = 5,
        [hashtable]$IcmParams = @{}
    )

    if (-not $Label) { $Label = $ServiceName }
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $lastStatus = "inconnu"

    while ($sw.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        try {
            $status = Invoke-Command -ComputerName $ComputerName @IcmParams -ScriptBlock {
                param($n)
                $s = Get-Service -Name $n -ErrorAction SilentlyContinue
                if ($null -eq $s) { "INTROUVABLE" } else { [string]$s.Status }
            } -ArgumentList $ServiceName -ErrorAction Stop

            $lastStatus = $status

            if ($status -eq "INTROUVABLE") {
                return [PSCustomObject]@{
                    Running = $false; Status = $status; ElapsedSeconds = [int]$sw.Elapsed.TotalSeconds
                    Reason  = "Le service '$ServiceName' n'existe pas sur $ComputerName. Verifier le nom exact dans Navis-Config.json (section ServiceNames) avec 'Get-Service | Where-Object Name -like ""*navis*""'."
                }
            }
            if ($status -eq "Running") {
                return [PSCustomObject]@{
                    Running = $true; Status = $status; ElapsedSeconds = [int]$sw.Elapsed.TotalSeconds; Reason = $null
                }
            }
        } catch {
            Write-N4Log -Message "[$ComputerName] Interrogation du service '$Label' impossible sur cette passe : $($_.Exception.Message)" -Level WARN
        }
        Start-Sleep -Seconds $PollIntervalSeconds
    }

    return [PSCustomObject]@{
        Running = $false; Status = $lastStatus; ElapsedSeconds = [int]$sw.Elapsed.TotalSeconds
        Reason  = "Le service Windows n'est pas passe Running en $(Format-N4Duration -Seconds $TimeoutSeconds) (dernier statut observe : $lastStatus)."
    }
}

function Wait-N4LogReady {
    <#
    .SYNOPSIS
        Attend qu'un marqueur de fin d'initialisation apparaisse dans le log
        applicatif d'un composant. C'est LA preuve de demarrage reelle.
    .DESCRIPTION
        Relit en boucle la portion de log ecrite depuis le watchpoint et
        cherche, ligne par ligne :
          - ErrorPatterns  : echec caracterise -> on arrete d'attendre
          - ReadyPatterns  : composant initialise -> succes
        Les lignes correspondant a IgnorePatterns sont ecartees avant toute
        evaluation (utile pour neutraliser un ERROR connu et sans consequence
        au demarrage).
    .OUTPUTS
        PSCustomObject : Ready, Failed, Evidence, FailureLine, ElapsedSeconds,
                         LinesSeen, LastLine, Reason.
    #>
    param(
        [Parameter(Mandatory = $true)][hashtable]$Watchpoint,
        [Parameter(Mandatory = $true)][hashtable]$Settings,
        [string]$Label = "composant",
        [hashtable]$IcmParams = @{}
    )

    $computer  = $Watchpoint.ComputerName
    $pattern   = $Watchpoint.LogPath
    $logPath   = if ($Watchpoint.ResolvedPath) { $Watchpoint.ResolvedPath } else { $pattern }
    $offset    = [int64]$Watchpoint.Offset
    $timeout   = [int]$Settings.LogReadyTimeoutSeconds
    $poll      = [int]$Settings.PollIntervalSeconds
    $progress  = [int]$Settings.ProgressEverySeconds
    $maxBytes  = [int]$Settings.LogTailBytes
    $reader    = Get-N4LogReaderScriptBlock
    $resolver  = Get-N4LogResolverScriptBlock

    # Un motif generique doit etre reevalue a chaque passe : le composant peut
    # creer un NOUVEAU fichier au demarrage (cas du log XPS). Tant qu'aucun
    # fichier n'existe encore, on continue aussi de chercher.
    $isPattern = ($pattern -match '[\*\?]')

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $lastProgress = 0
    $linesSeen = 0
    $lastLine = ""
    $everExisted = $Watchpoint.Existed
    $readErrorReported = $false

    Write-N4Log -Message "[$computer] Attente de la preuve de demarrage de '$Label' dans $logPath (timeout $(Format-N4Duration -Seconds $timeout))." -Level INFO
    Write-N4Log -Message "[$computer] Marqueur(s) attendu(s) : $($Settings.ReadyPatterns -join ' | ')" -Level INFO

    while ($sw.Elapsed.TotalSeconds -lt $timeout) {

        if ($isPattern -or -not $everExisted) {
            try {
                $res = Invoke-Command -ComputerName $computer @IcmParams -ScriptBlock $resolver `
                            -ArgumentList $pattern -ErrorAction Stop
                if ($res.Exists -and $res.Path -and ($res.Path -ne $logPath)) {
                    Write-N4Log -Message "[$computer] Nouveau fichier de log detecte : $($res.Path). Analyse depuis son debut (comportement normal pour un composant qui recree son log au demarrage)." -Level INFO
                    $logPath = $res.Path
                    $offset = [int64]0
                }
            } catch {
                # Sans resolution possible, on retente a la passe suivante.
            }
        }

        try {
            $chunk = Invoke-Command -ComputerName $computer @IcmParams -ScriptBlock $reader `
                        -ArgumentList $logPath, $offset, $maxBytes -ErrorAction Stop
        } catch {
            if (-not $readErrorReported) {
                Write-N4Log -Message "[$computer] Lecture du log impossible : $($_.Exception.Message). Nouvelle tentative a chaque passe." -Level WARN
                $readErrorReported = $true
            }
            Start-Sleep -Seconds $poll
            continue
        }

        if ($chunk.Error -and -not $readErrorReported) {
            Write-N4Log -Message "[$computer] Le fichier $logPath existe mais n'a pas pu etre lu : $($chunk.Error). Verifier les droits du compte d'execution sur ce fichier." -Level WARN
            $readErrorReported = $true
        }

        if ($chunk.Exists) {
            if (-not $everExisted) {
                Write-N4Log -Message "[$computer] Le log $logPath vient d'etre cree par le service : analyse en cours." -Level INFO
                $everExisted = $true
            }
            if ($chunk.Rotated) {
                Write-N4Log -Message "[$computer] Rotation de log detectee sur $logPath : l'analyse repart du debut du nouveau fichier." -Level WARN
            }

            $offset = [int64]$chunk.NewOffset

            if ($chunk.Text) {
                $lines = $chunk.Text -split "`r?`n"
                foreach ($line in $lines) {
                    if ([string]::IsNullOrWhiteSpace($line)) { continue }
                    $linesSeen++
                    $lastLine = $line.Trim()

                    $ignored = $false
                    foreach ($ip in $Settings.IgnorePatterns) {
                        if ($ip -and ($line -match $ip)) { $ignored = $true; break }
                    }
                    if ($ignored) { continue }

                    foreach ($ep in $Settings.ErrorPatterns) {
                        if ($ep -and ($line -match $ep)) {
                            Write-N4Log -Message "[$computer] SIGNATURE D'ECHEC detectee dans $logPath (motif '$ep')." -Level ERROR
                            Write-N4Log -Message "[$computer] Ligne : $($line.Trim())" -Level ERROR
                            return [PSCustomObject]@{
                                Ready = $false; Failed = $true; Evidence = $null; FailureLine = $line.Trim()
                                ElapsedSeconds = [int]$sw.Elapsed.TotalSeconds; LinesSeen = $linesSeen
                                LastLine = $lastLine
                                Reason = "Signature d'echec '$ep' rencontree dans le log applicatif."
                            }
                        }
                    }

                    foreach ($rp in $Settings.ReadyPatterns) {
                        if ($rp -and ($line -match $rp)) {
                            $elapsed = [int]$sw.Elapsed.TotalSeconds
                            Write-N4Log -Message "[$computer] PREUVE DE DEMARRAGE trouvee apres $(Format-N4Duration -Seconds $elapsed) (motif '$rp')." -Level OK
                            Write-N4Log -Message "[$computer] Ligne : $($line.Trim())" -Level OK

                            if ([int]$Settings.PostReadySettleSeconds -gt 0) {
                                $settle = [int]$Settings.PostReadySettleSeconds
                                Write-N4Log -Message "[$computer] Observation complementaire de $(Format-N4Duration -Seconds $settle) pour verifier qu'aucune erreur ne survient juste apres l'initialisation..." -Level INFO
                                Start-Sleep -Seconds $settle
                                try {
                                    $post = Invoke-Command -ComputerName $computer @IcmParams -ScriptBlock $reader `
                                                -ArgumentList $logPath, $offset, $maxBytes -ErrorAction Stop
                                    foreach ($pl in ($post.Text -split "`r?`n")) {
                                        if ([string]::IsNullOrWhiteSpace($pl)) { continue }
                                        foreach ($ep in $Settings.ErrorPatterns) {
                                            if ($ep -and ($pl -match $ep)) {
                                                Write-N4Log -Message "[$computer] Erreur survenue APRES le marqueur de demarrage : $($pl.Trim())" -Level ERROR
                                                return [PSCustomObject]@{
                                                    Ready = $false; Failed = $true; Evidence = $line.Trim(); FailureLine = $pl.Trim()
                                                    ElapsedSeconds = [int]$sw.Elapsed.TotalSeconds; LinesSeen = $linesSeen
                                                    LastLine = $pl.Trim()
                                                    Reason = "Le marqueur de demarrage est apparu, mais une signature d'echec '$ep' a suivi pendant la periode d'observation."
                                                }
                                            }
                                        }
                                    }
                                } catch {
                                    Write-N4Log -Message "[$computer] Observation complementaire non concluante (lecture impossible) : $($_.Exception.Message)" -Level WARN
                                }
                            }

                            return [PSCustomObject]@{
                                Ready = $true; Failed = $false; Evidence = $line.Trim(); FailureLine = $null
                                ElapsedSeconds = $elapsed; LinesSeen = $linesSeen; LastLine = $lastLine; Reason = $null
                            }
                        }
                    }
                }
            }
        }

        if ($progress -gt 0 -and ($sw.Elapsed.TotalSeconds - $lastProgress) -ge $progress) {
            $lastProgress = [int]$sw.Elapsed.TotalSeconds
            $pct = [Math]::Min(99, [int](($sw.Elapsed.TotalSeconds / $timeout) * 100))
            Write-N4Log -Message "[$computer] '$Label' toujours en cours d'initialisation - $(Format-N4Duration -Seconds $lastProgress) ecoulees sur $(Format-N4Duration -Seconds $timeout) ($pct%), $linesSeen ligne(s) de log analysee(s)." -Level INFO
            if ($lastLine) {
                $extract = if ($lastLine.Length -gt 180) { $lastLine.Substring(0, 180) + "..." } else { $lastLine }
                Write-N4Log -Message "[$computer] Derniere ligne du log : $extract" -Level INFO
            } elseif (-not $everExisted) {
                Write-N4Log -Message "[$computer] Aucune ligne ecrite pour l'instant : le fichier $logPath n'existe toujours pas. Verifier le chemin configure (section Readiness) si cela persiste." -Level WARN
            }
        }

        Start-Sleep -Seconds $poll
    }

    $elapsed = [int]$sw.Elapsed.TotalSeconds
    return [PSCustomObject]@{
        Ready = $false; Failed = $false; Evidence = $null; FailureLine = $null
        ElapsedSeconds = $elapsed; LinesSeen = $linesSeen; LastLine = $lastLine
        Reason = "Aucun marqueur de demarrage ni signature d'echec apres $(Format-N4Duration -Seconds $elapsed). Le composant est peut-etre encore en cours d'initialisation, ou le marqueur configure ne correspond pas a ce que ce composant ecrit reellement."
    }
}

function Wait-N4ComponentReady {
    <#
    .SYNOPSIS
        Attente complete en deux temps : service Windows Running, PUIS preuve
        de demarrage dans le log applicatif.
    .DESCRIPTION
        Retourne un etat a TROIS valeurs, jamais un simple vrai/faux :
          Ready   - service Running ET marqueur de demarrage trouve dans le log
          Failed  - service absent/non demarre, ou signature d'echec dans le log
          Unknown - service Running mais preuve de log indisponible ou timeout
                    atteint sans marqueur. L'etat reel n'est PAS etabli : c'est
                    a l'operateur de trancher, pas au script.
    .OUTPUTS
        PSCustomObject : Status, Component, ComputerName, ServiceStatus,
                         Evidence, FailureLine, ElapsedSeconds, LogPath, Reason,
                         Recommendations.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$ComputerName,
        [Parameter(Mandatory = $true)][string]$ServiceName,
        [Parameter(Mandatory = $true)][string]$ComponentKey,
        [string]$Label,
        [hashtable]$Watchpoint,
        [hashtable]$IcmParams = @{}
    )

    if (-not $Label) { $Label = $ComponentKey }
    $settings = Get-N4ReadinessSettings -ComponentKey $ComponentKey
    $totalSw = [System.Diagnostics.Stopwatch]::StartNew()

    # ---- Phase 1 : le service Windows doit au moins passer Running ----
    Write-N4Log -Message "[$ComputerName] Phase 1/2 - attente du statut Windows 'Running' pour '$Label' (timeout $(Format-N4Duration -Seconds ([int]$settings.ServiceRunningTimeoutSeconds)))." -Level INFO
    $svc = Wait-N4ServiceRunning -ComputerName $ComputerName -ServiceName $ServiceName -Label $Label `
                -TimeoutSeconds ([int]$settings.ServiceRunningTimeoutSeconds) `
                -PollIntervalSeconds ([Math]::Min(5, [int]$settings.PollIntervalSeconds)) -IcmParams $IcmParams

    if (-not $svc.Running) {
        return [PSCustomObject]@{
            Status = "Failed"; Component = $ComponentKey; ComputerName = $ComputerName
            ServiceStatus = $svc.Status; Evidence = $null; FailureLine = $null
            ElapsedSeconds = [int]$totalSw.Elapsed.TotalSeconds; LogPath = $settings.LogPath
            Reason = $svc.Reason
            Recommendations = @(
                "Se connecter a $ComputerName et lire l'Observateur d'evenements (Journaux Windows > Systeme et Application) autour de l'heure de la tentative.",
                "Verifier que le compte de service N4 n'a pas expire et qu'il conserve le droit 'Ouvrir une session en tant que service'.",
                "Lancer le service manuellement (services.msc) pour recuperer le message d'erreur exact.",
                "NE PAS demarrer le composant suivant tant que celui-ci n'est pas operationnel."
            )
        }
    }
    Write-N4Log -Message "[$ComputerName] Service '$Label' Running apres $(Format-N4Duration -Seconds $svc.ElapsedSeconds). Attention : cela ne prouve pas encore que le composant est operationnel." -Level OK

    # ---- Phase 2 : preuve reelle dans le log applicatif ----
    if ([string]::IsNullOrWhiteSpace($settings.LogPath) -or $settings.ReadyPatterns.Count -eq 0) {
        Write-N4Instruction -Title "PREUVE DE LOG NON CONFIGUREE POUR '$ComponentKey'" -Level WARN -Lines @(
            "Le service Windows est Running, mais aucun marqueur de log n'est defini pour ce composant.",
            "L'etat retourne est donc 'A CONFIRMER' (Unknown), pas 'demarre'.",
            "Pour activer la preuve reelle, renseigner dans Navis-Config.json :",
            "    Readiness > Components > $ComponentKey > LogPath      (chemin local du log sur le serveur)",
            "    Readiness > Components > $ComponentKey > ReadyPatterns (marqueur de fin d'initialisation)",
            "Le script Find-N4ReadinessPattern.ps1 aide a identifier ce marqueur a partir d'un log reel.",
            "En attendant : confirmer visuellement dans N4 (Cluster Services / Node Info Desk) que le composant est ACTIVE."
        )
        return [PSCustomObject]@{
            Status = "Unknown"; Component = $ComponentKey; ComputerName = $ComputerName
            ServiceStatus = $svc.Status; Evidence = $null; FailureLine = $null
            ElapsedSeconds = [int]$totalSw.Elapsed.TotalSeconds; LogPath = $settings.LogPath
            Reason = "Aucune preuve de log configuree pour '$ComponentKey' : le statut Windows Running est la seule information disponible."
            Recommendations = @(
                "Renseigner LogPath et ReadyPatterns pour '$ComponentKey' dans Navis-Config.json (section Readiness).",
                "Confirmer manuellement l'etat ACTIVE du composant dans N4 avant de poursuivre."
            )
        }
    }

    Write-N4Log -Message "[$ComputerName] Phase 2/2 - recherche de la preuve de demarrage dans le log applicatif." -Level INFO

    if (-not $Watchpoint) {
        Write-N4Log -Message "[$ComputerName] Aucun point de reference fourni : l'analyse ne portera que sur les lignes ecrites a partir de maintenant." -Level WARN
        $Watchpoint = New-N4LogWatchpoint -ComputerName $ComputerName -LogPath $settings.LogPath -IcmParams $IcmParams
    }

    $logResult = Wait-N4LogReady -Watchpoint $Watchpoint -Settings $settings -Label $Label -IcmParams $IcmParams

    if ($logResult.Ready) {
        return [PSCustomObject]@{
            Status = "Ready"; Component = $ComponentKey; ComputerName = $ComputerName
            ServiceStatus = $svc.Status; Evidence = $logResult.Evidence; FailureLine = $null
            ElapsedSeconds = [int]$totalSw.Elapsed.TotalSeconds; LogPath = $settings.LogPath
            Reason = $null; Recommendations = @()
        }
    }

    if ($logResult.Failed) {
        return [PSCustomObject]@{
            Status = "Failed"; Component = $ComponentKey; ComputerName = $ComputerName
            ServiceStatus = $svc.Status; Evidence = $logResult.Evidence; FailureLine = $logResult.FailureLine
            ElapsedSeconds = [int]$totalSw.Elapsed.TotalSeconds; LogPath = $settings.LogPath
            Reason = $logResult.Reason
            Recommendations = @(
                "Ouvrir $($settings.LogPath) sur $ComputerName et lire le contexte autour de la ligne signalee (20 lignes avant / apres).",
                "Ne pas relancer le composant en boucle : traiter la cause indiquee par la signature detectee.",
                "Si la signature correspond a une corruption amq (IOException / NegativeArraySizeException), appliquer la Fiche A (SOP-2) et non un simple redemarrage.",
                "NE PAS demarrer le composant suivant : la dependance n'est pas satisfaite."
            )
        }
    }

    # Timeout sans marqueur ni echec : etat non etabli.
    return [PSCustomObject]@{
        Status = "Unknown"; Component = $ComponentKey; ComputerName = $ComputerName
        ServiceStatus = $svc.Status; Evidence = $null; FailureLine = $null
        ElapsedSeconds = [int]$totalSw.Elapsed.TotalSeconds; LogPath = $settings.LogPath
        Reason = $logResult.Reason
        Recommendations = @(
            "Verifier d'abord le marqueur : le motif configure ($($settings.ReadyPatterns -join ' | ')) correspond-il a ce que ce composant ecrit reellement ? Utiliser Find-N4ReadinessPattern.ps1 sur un log de demarrage reussi.",
            "Verifier ensuite le chemin : $($settings.LogPath) est-il bien le log actif de ce composant sur $ComputerName ?",
            "Si le composant met legitimement plus longtemps a demarrer, augmenter LogReadyTimeoutSeconds pour '$ComponentKey' dans Navis-Config.json.",
            "Derniere ligne observee : $($logResult.LastLine)",
            "Confirmer manuellement dans N4 (Cluster Services / Node Info Desk) avant toute decision de poursuite."
        )
    }
}

function Write-N4ReadinessOutcome {
    <#
    .SYNOPSIS
        Journalise de facon lisible le resultat d'une attente de demarrage,
        avec les recommandations associees.
    #>
    param([Parameter(Mandatory = $true)]$Result)

    switch ($Result.Status) {
        "Ready" {
            Write-N4Log -Message "[$($Result.ComputerName)] '$($Result.Component)' OPERATIONNEL - confirme par le log en $(Format-N4Duration -Seconds $Result.ElapsedSeconds)." -Level OK
        }
        "Failed" {
            Write-N4Log -Message "[$($Result.ComputerName)] '$($Result.Component)' EN ECHEC apres $(Format-N4Duration -Seconds $Result.ElapsedSeconds) : $($Result.Reason)" -Level ERROR
            if ($Result.Recommendations.Count -gt 0) {
                Write-N4Instruction -Title "QUE FAIRE MAINTENANT" -Level ERROR -Lines $Result.Recommendations
            }
        }
        "Unknown" {
            Write-N4Log -Message "[$($Result.ComputerName)] '$($Result.Component)' ETAT A CONFIRMER apres $(Format-N4Duration -Seconds $Result.ElapsedSeconds) : $($Result.Reason)" -Level WARN
            if ($Result.Recommendations.Count -gt 0) {
                Write-N4Instruction -Title "VERIFICATIONS A MENER AVANT DE POURSUIVRE" -Level WARN -Lines $Result.Recommendations
            }
        }
        "AlreadyRunning" {
            Write-N4Log -Message "[$($Result.ComputerName)] '$($Result.Component)' etait deja demarre : etat A CONFIRMER (preuve de log non rejouable)." -Level WARN
            if ($Result.Recommendations.Count -gt 0) {
                Write-N4Instruction -Title "VERIFICATIONS A MENER AVANT DE POURSUIVRE" -Level WARN -Lines $Result.Recommendations
            }
        }
    }
}

function Start-N4Component {
    <#
    .SYNOPSIS
        Demarre un composant N4 et attend la preuve reelle qu'il est operationnel.
    .DESCRIPTION
        Enchaine : pose du point de reference sur le log -> Start-Service
        (ou Restart-Service si -Restart) -> attente service Running -> attente
        du marqueur de demarrage dans le log.
    .PARAMETER ComponentKey
        Cle de la section Readiness.Components (Cluster, Center, Standby,
        Bridge, XPS, ECN4, ECN4Web).
    .PARAMETER Restart
        Redemarre le service meme s'il tourne deja (arret puis demarrage).
    .OUTPUTS
        Le PSCustomObject de Wait-N4ComponentReady (Status Ready/Unknown/Failed).
    #>
    param(
        [Parameter(Mandatory = $true)][string]$ComputerName,
        [Parameter(Mandatory = $true)][string]$ServiceName,
        [Parameter(Mandatory = $true)][string]$ComponentKey,
        [string]$Label,
        [hashtable]$IcmParams = @{},
        [switch]$Restart
    )

    if (-not $Label) { $Label = $ComponentKey }
    $settings = Get-N4ReadinessSettings -ComponentKey $ComponentKey

    # 0. Le composant tourne-t-il DEJA ?
    #    Cas important : si le service tourne deja, son marqueur de demarrage a
    #    ete ecrit il y a longtemps et ne sera jamais reecrit. Attendre une
    #    preuve qui ne viendra plus consommerait tout le timeout pour rien.
    #    On le dit clairement au lieu de faire semblant d'avoir verifie.
    if (-not $Restart) {
        try {
            $preStatus = Invoke-Command -ComputerName $ComputerName @IcmParams -ScriptBlock {
                param($n)
                $s = Get-Service -Name $n -ErrorAction SilentlyContinue
                if ($null -eq $s) { "INTROUVABLE" } else { [string]$s.Status }
            } -ArgumentList $ServiceName -ErrorAction Stop
        } catch {
            $preStatus = "inconnu"
        }

        if ($preStatus -eq "Running") {
            Write-N4Instruction -Title "'$ComponentKey' TOURNE DEJA SUR $ComputerName" -Level WARN -Lines @(
                "Le service etait deja Running avant cet appel : aucune commande de demarrage n'a ete envoyee.",
                "Son marqueur de demarrage a ete ecrit lors de son lancement precedent et ne sera pas reecrit :",
                "la preuve par le log n'est donc pas rejouable ici. L'etat est 'A CONFIRMER', pas 'demarre'.",
                "Deux options : confirmer visuellement dans N4 que ce composant est ACTIVE,",
                "ou le redemarrer volontairement pour obtenir une preuve fraiche (Start-N4Component -Restart)."
            )
            return [PSCustomObject]@{
                Status = "AlreadyRunning"; Component = $ComponentKey; ComputerName = $ComputerName
                ServiceStatus = $preStatus; Evidence = $null; FailureLine = $null
                ElapsedSeconds = 0; LogPath = $settings.LogPath
                Reason = "Service deja Running avant l'appel : preuve de demarrage non rejouable."
                Recommendations = @(
                    "Confirmer dans N4 (Cluster Services / Node Info Desk) que '$ComponentKey' est bien ACTIVE.",
                    "Si son etat est douteux, le redemarrer explicitement plutot que de le supposer sain."
                )
            }
        }
    }

    # 1. Point de reference AVANT toute action (anti-faux-positif).
    $watch = New-N4LogWatchpoint -ComputerName $ComputerName -LogPath $settings.LogPath -IcmParams $IcmParams

    # 2. Demarrage / redemarrage.
    $verbe = if ($Restart) { "Redemarrage" } else { "Demarrage" }
    Write-N4Log -Message "[$ComputerName] $verbe de '$Label' ($ServiceName)..." -Level ACTION
    try {
        Invoke-Command -ComputerName $ComputerName @IcmParams -ScriptBlock {
            param($svcName, $doRestart, $stopTimeout)
            $svc = Get-Service -Name $svcName -ErrorAction Stop
            if ($doRestart -and $svc.Status -ne "Stopped") {
                Stop-Service -Name $svcName -ErrorAction Stop
                $svc.WaitForStatus("Stopped", [TimeSpan]::FromSeconds($stopTimeout))
                $svc.Refresh()
            }
            if ((Get-Service -Name $svcName).Status -ne "Running") {
                Start-Service -Name $svcName -ErrorAction Stop
            }
            (Get-Service -Name $svcName).Status
        } -ArgumentList $ServiceName, [bool]$Restart, ([int]$settings.StopTimeoutSeconds) -ErrorAction Stop | Out-Null
    } catch {
        Write-N4Log -Message "[$ComputerName] ECHEC de la commande de $($verbe.ToLower()) pour '$Label' : $($_.Exception.Message)" -Level ERROR
        return [PSCustomObject]@{
            Status = "Failed"; Component = $ComponentKey; ComputerName = $ComputerName
            ServiceStatus = "inconnu"; Evidence = $null; FailureLine = $null
            ElapsedSeconds = 0; LogPath = $settings.LogPath
            Reason = "La commande de $($verbe.ToLower()) a echoue : $($_.Exception.Message)"
            Recommendations = @(
                "Verifier la connectivite WinRM : Test-WSMan $ComputerName",
                "Verifier que le compte d'execution est bien Administrateur local sur $ComputerName (ou relancer avec -Credential).",
                "Verifier le nom du service dans Navis-Config.json (section ServiceNames)."
            )
        }
    }
    Write-N4Log -Message "[$ComputerName] Commande de $($verbe.ToLower()) acceptee. Debut de la verification reelle." -Level INFO

    # 3. Preuve.
    $result = Wait-N4ComponentReady -ComputerName $ComputerName -ServiceName $ServiceName `
                -ComponentKey $ComponentKey -Label $Label -Watchpoint $watch -IcmParams $IcmParams
    Write-N4ReadinessOutcome -Result $result
    return $result
}

function Stop-N4Component {
    <#
    .SYNOPSIS
        Arrete un composant N4 et confirme reellement son arret.
    .DESCRIPTION
        Distingue explicitement trois situations que l'ancienne version
        confondait :
          - Stopped                : arret confirme
          - StopPending (Stopping) : le service est bloque en cours d'arret,
                                     le processus tient encore. On le SIGNALE
                                     avec le PID, on ne tue JAMAIS le processus
                                     automatiquement.
          - autre / injoignable    : etat non etabli.
    .OUTPUTS
        PSCustomObject : Stopped, Status, ElapsedSeconds, ProcessId, Reason,
                         Recommendations.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$ComputerName,
        [Parameter(Mandatory = $true)][string]$ServiceName,
        [Parameter(Mandatory = $true)][string]$ComponentKey,
        [string]$Label,
        [hashtable]$IcmParams = @{}
    )

    if (-not $Label) { $Label = $ComponentKey }
    $settings = Get-N4ReadinessSettings -ComponentKey $ComponentKey
    $timeout  = [int]$settings.StopTimeoutSeconds
    $poll     = [int]$settings.PollIntervalSeconds
    $progress = [int]$settings.ProgressEverySeconds

    Write-N4Log -Message "[$ComputerName] Arret de '$Label' ($ServiceName) - timeout $(Format-N4Duration -Seconds $timeout)." -Level ACTION

    try {
        Invoke-Command -ComputerName $ComputerName @IcmParams -ScriptBlock {
            param($n)
            $s = Get-Service -Name $n -ErrorAction Stop
            if ($s.Status -eq "Running") { Stop-Service -Name $n -ErrorAction Stop }
        } -ArgumentList $ServiceName -ErrorAction Stop
    } catch {
        Write-N4Log -Message "[$ComputerName] La commande d'arret de '$Label' a echoue : $($_.Exception.Message)" -Level ERROR
        return [PSCustomObject]@{
            Stopped = $false; Status = "inconnu"; ElapsedSeconds = 0; ProcessId = $null
            Reason = "Commande d'arret refusee ou injoignable : $($_.Exception.Message)"
            Recommendations = @("Verifier WinRM et les droits, puis relancer. Ne pas passer a l'etape suivante tant que l'arret n'est pas confirme.")
        }
    }

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $lastProgress = 0
    $lastStatus = "inconnu"

    while ($sw.Elapsed.TotalSeconds -lt $timeout) {
        try {
            $info = Invoke-Command -ComputerName $ComputerName @IcmParams -ScriptBlock {
                param($n)
                $s = Get-Service -Name $n -ErrorAction SilentlyContinue
                if ($null -eq $s) { return [PSCustomObject]@{ Status = "INTROUVABLE"; ProcessId = $null } }
                $pid2 = $null
                try {
                    $w = Get-CimInstance -ClassName Win32_Service -Filter "Name='$n'" -ErrorAction SilentlyContinue
                    if ($w -and $w.ProcessId -gt 0) { $pid2 = $w.ProcessId }
                } catch { }
                [PSCustomObject]@{ Status = [string]$s.Status; ProcessId = $pid2 }
            } -ArgumentList $ServiceName -ErrorAction Stop

            $lastStatus = $info.Status

            if ($info.Status -eq "Stopped" -or $info.Status -eq "INTROUVABLE") {
                $elapsed = [int]$sw.Elapsed.TotalSeconds
                Write-N4Log -Message "[$ComputerName] '$Label' est arrete (confirme apres $(Format-N4Duration -Seconds $elapsed))." -Level OK
                return [PSCustomObject]@{
                    Stopped = $true; Status = $info.Status; ElapsedSeconds = $elapsed
                    ProcessId = $null; Reason = $null; Recommendations = @()
                }
            }
        } catch {
            Write-N4Log -Message "[$ComputerName] Verification de l'arret impossible sur cette passe : $($_.Exception.Message)" -Level WARN
        }

        if ($progress -gt 0 -and ($sw.Elapsed.TotalSeconds - $lastProgress) -ge $progress) {
            $lastProgress = [int]$sw.Elapsed.TotalSeconds
            Write-N4Log -Message "[$ComputerName] '$Label' toujours en cours d'arret (statut : $lastStatus) - $(Format-N4Duration -Seconds $lastProgress) sur $(Format-N4Duration -Seconds $timeout)." -Level INFO
        }
        Start-Sleep -Seconds $poll
    }

    $elapsed = [int]$sw.Elapsed.TotalSeconds
    $processId = $null
    try {
        $processId = Invoke-Command -ComputerName $ComputerName @IcmParams -ScriptBlock {
            param($n)
            $w = Get-CimInstance -ClassName Win32_Service -Filter "Name='$n'" -ErrorAction SilentlyContinue
            if ($w -and $w.ProcessId -gt 0) { $w.ProcessId } else { $null }
        } -ArgumentList $ServiceName -ErrorAction Stop
    } catch { }

    $reco = @(
        "Statut bloquant observe : $lastStatus. Le service n'a PAS confirme son arret en $(Format-N4Duration -Seconds $timeout).",
        "Ce script ne tue jamais un processus automatiquement : un arret force pendant une ecriture ActiveMQ/KahaDB est une cause connue de corruption."
    )
    if ($processId) {
        $reco += "Processus encore actif sur $ComputerName : PID $processId. Verifier sa consommation CPU avant toute decision (un JVM qui flushe ses files est occupe, pas bloque)."
        $reco += "Si et seulement si l'arret force est valide par un habilite : Invoke-Command -ComputerName $ComputerName -ScriptBlock { Stop-Process -Id $processId -Force }"
    }
    $reco += "Consigner la decision (attente prolongee ou arret force) dans le ticket : c'est une derogation a tracer."

    Write-N4Log -Message "[$ComputerName] '$Label' N'A PAS confirme son arret apres $(Format-N4Duration -Seconds $elapsed) (statut : $lastStatus)." -Level ERROR
    Write-N4Instruction -Title "SERVICE BLOQUE EN COURS D'ARRET" -Level ERROR -Lines $reco

    return [PSCustomObject]@{
        Stopped = $false; Status = $lastStatus; ElapsedSeconds = $elapsed; ProcessId = $processId
        Reason = "Arret non confirme en $(Format-N4Duration -Seconds $timeout) (statut : $lastStatus)."
        Recommendations = $reco
    }
}

function Confirm-N4ContinueOnUnknown {
    <#
    .SYNOPSIS
        Demande a l'operateur s'il souhaite poursuivre malgre un etat non
        etabli (Unknown). Toute poursuite est journalisee comme derogation.
    .PARAMETER Result
        L'objet retourne par Wait-N4ComponentReady / Start-N4Component.
    .PARAMETER Unattended
        En mode non surveille, ne pose aucune question et refuse la poursuite.
    #>
    param(
        [Parameter(Mandatory = $true)]$Result,
        [switch]$Unattended
    )

    if ($Unattended) {
        Write-N4Log -Message "Mode -Unattended : etat 'A CONFIRMER' traite comme bloquant pour '$($Result.Component)'. Aucune poursuite automatique." -Level ERROR
        return $false
    }

    Write-N4Instruction -Title "DECISION OPERATEUR REQUISE" -Level WARN -Lines @(
        "Composant : $($Result.Component) sur $($Result.ComputerName)",
        "Le service Windows tourne, mais la preuve de demarrage n'a pas ete obtenue.",
        "Avant de repondre OUI : confirmer dans N4 (Cluster Services / Node Info Desk) que ce composant est ACTIVE.",
        "Repondre NON (ou n'importe quoi d'autre que OUI) arrete la sequence sans rien demarrer de plus."
    )
    $ok = Confirm-N4Action -Prompt "Poursuivre malgre l'etat non confirme de '$($Result.Component)' ?"
    if ($ok) {
        $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
        Write-N4Log -Message "DEROGATION : poursuite autorisee par $identity malgre l'etat non confirme de '$($Result.Component)' sur $($Result.ComputerName). A justifier dans le ticket." -Level WARN
    } else {
        Write-N4Log -Message "Poursuite refusee par l'operateur. Sequence interrompue a un point sur." -Level WARN
    }
    return $ok
}

# ============================================================
# GESTION DES MISES A JOUR WINDOWS (API native Windows Update Agent)
# ============================================================
# Ces fonctions s'executent COTE SERVEUR CIBLE via Invoke-Command
# (le COM object Microsoft.Update.Session doit tourner localement
# sur la machine qu'on veut patcher). Elles retournent des objets
# structures ; c'est le script appelant qui se charge de les journaliser
# avec Write-N4Log, car Write-N4Log ecrit dans le fichier log LOCAL
# a la machine qui orchestre (pas sur le serveur distant).

function Get-N4PendingUpdatesScriptBlock {
    <#
    .SYNOPSIS
        Retourne le ScriptBlock (a executer via Invoke-Command) qui recherche
        les mises a jour Windows non installees sur la machine cible.
        Renvoie un objet : Count, Titles (liste "KB - Titre")
    #>
    return {
        try {
            $session = New-Object -ComObject Microsoft.Update.Session
            $searcher = $session.CreateUpdateSearcher()
            $result = $searcher.Search("IsInstalled=0 and IsHidden=0")
            $titles = @()
            foreach ($u in $result.Updates) {
                $kb = if ($u.KBArticleIDs.Count -gt 0) { "KB$($u.KBArticleIDs[0])" } else { "KB inconnu" }
                $titles += "$kb - $($u.Title)"
            }
            [PSCustomObject]@{
                Success = $true
                Count   = $result.Updates.Count
                Titles  = $titles
                Error   = $null
            }
        } catch {
            [PSCustomObject]@{
                Success = $false
                Count   = 0
                Titles  = @()
                Error   = $_.Exception.Message
            }
        }
    }
}

function Get-N4InstallUpdatesScriptBlock {
    <#
    .SYNOPSIS
        Retourne le ScriptBlock (a executer via Invoke-Command) qui telecharge
        et installe toutes les mises a jour Windows non installees sur la
        machine cible. Renvoie : InstalledCount, FailedCount, RebootRequired.
    #>
    return {
        try {
            $session = New-Object -ComObject Microsoft.Update.Session
            $searcher = $session.CreateUpdateSearcher()
            $searchResult = $searcher.Search("IsInstalled=0 and IsHidden=0")

            if ($searchResult.Updates.Count -eq 0) {
                return [PSCustomObject]@{ Success = $true; InstalledCount = 0; FailedCount = 0; RebootRequired = $false; Error = $null }
            }

            $toDownload = New-Object -ComObject Microsoft.Update.UpdateColl
            foreach ($u in $searchResult.Updates) {
                if (-not $u.EulaAccepted) { $u.AcceptEula() | Out-Null }
                $toDownload.Add($u) | Out-Null
            }
            $downloader = $session.CreateUpdateDownloader()
            $downloader.Updates = $toDownload
            $downloader.Download() | Out-Null

            $toInstall = New-Object -ComObject Microsoft.Update.UpdateColl
            foreach ($u in $searchResult.Updates) {
                if ($u.IsDownloaded) { $toInstall.Add($u) | Out-Null }
            }

            $installer = $session.CreateUpdateInstaller()
            $installer.Updates = $toInstall
            $installResult = $installer.Install()

            [PSCustomObject]@{
                Success         = $true
                InstalledCount  = $toInstall.Count
                FailedCount     = $searchResult.Updates.Count - $toInstall.Count
                RebootRequired  = [bool]$installResult.RebootRequired
                Error           = $null
            }
        } catch {
            [PSCustomObject]@{
                Success = $false; InstalledCount = 0; FailedCount = 0; RebootRequired = $false
                Error   = $_.Exception.Message
            }
        }
    }
}

function Wait-N4ServiceStatus {
    <#
    .SYNOPSIS
        Attend qu'un service Windows LOCAL atteigne un statut donne
        (Running/Stopped), avec timeout.
    .DESCRIPTION
        Conservee pour compatibilite avec d'anciens appels. Pour un composant
        N4 distant, preferer Wait-N4ComponentReady : le statut Windows seul
        ne prouve pas qu'un composant N4 est operationnel.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$ServiceName,
        [Parameter(Mandatory = $true)][ValidateSet("Running", "Stopped")][string]$DesiredStatus,
        [int]$TimeoutSeconds = 300
    )
    $elapsed = 0
    $intervalSec = 5
    while ($elapsed -lt $TimeoutSeconds) {
        $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if ($null -eq $svc) {
            Write-N4Log -Message "Service '$ServiceName' introuvable sur cette machine." -Level ERROR
            return $false
        }
        if ($svc.Status -eq $DesiredStatus) {
            Write-N4Log -Message "Service '$ServiceName' est maintenant $DesiredStatus." -Level OK
            return $true
        }
        Start-Sleep -Seconds $intervalSec
        $elapsed += $intervalSec
    }
    Write-N4Log -Message "Timeout ($TimeoutSeconds s) atteint : '$ServiceName' n'est pas $DesiredStatus." -Level WARN
    return $false
}

function Confirm-N4Action {
    <#
    .SYNOPSIS
        Demande une confirmation explicite avant une action a risque (ex. suppression de fichier).
    .PARAMETER Prompt
        Message a afficher.
    #>
    param([Parameter(Mandatory = $true)][string]$Prompt)
    $reponse = Read-Host "$Prompt (tapez OUI en majuscules pour confirmer)"
    return ($reponse -ceq "OUI")
}

function Test-N4ServerReachable {
    <#
    .SYNOPSIS
        Verifie qu'un serveur repond au ping ET a PowerShell Remoting (WinRM).
        A utiliser en pre-vol avant toute sequence de demarrage/arret.
    .PARAMETER ComputerName
        Nom ou IP du serveur a tester.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$ComputerName,
        [hashtable]$IcmParams = @{}
    )

    $pingOk = $false
    $latencyMs = $null
    try {
        $r = Test-Connection -ComputerName $ComputerName -Count 1 -ErrorAction Stop
        $pingOk = $true
        $latencyMs = if ($r.PSObject.Properties.Name -contains "Latency") { $r.Latency } else { $r.ResponseTime }
    } catch {
        $pingOk = $false
    }

    $winrmOk = $false
    try {
        Test-WSMan -ComputerName $ComputerName -ErrorAction Stop | Out-Null
        $winrmOk = $true
    } catch {
        $winrmOk = $false
    }

    [PSCustomObject]@{
        ComputerName = $ComputerName
        PingOk       = $pingOk
        LatencyMs    = $latencyMs
        WinRMOk      = $winrmOk
        Reachable    = $winrmOk   # WinRM est le critere qui compte : c'est lui dont dependent les scripts
    }
}

function Confirm-N4AllServicesStopped {
    <#
    .SYNOPSIS
        Interroge une liste de services (potentiellement sur plusieurs serveurs)
        et confirme que TOUS sont bien a l'etat Stopped. Sert de portillon avant
        toute operation de maintenance (ex. patching Windows).
    .PARAMETER ServiceChecklist
        Tableau d'objets @{ Server=...; Service=...; Label=... }
    #>
    param(
        [Parameter(Mandatory = $true)][array]$ServiceChecklist,
        [hashtable]$IcmParams = @{}
    )

    $allStopped = $true
    $details = @()

    foreach ($item in $ServiceChecklist) {
        try {
            $status = Invoke-Command -ComputerName $item.Server @IcmParams -ScriptBlock {
                param($n) (Get-Service -Name $n -ErrorAction SilentlyContinue).Status
            } -ArgumentList $item.Service -ErrorAction Stop
        } catch {
            $status = "INCONNU (connexion echouee)"
        }

        $stopped = ($status -eq "Stopped")
        if (-not $stopped) { $allStopped = $false }

        $details += [PSCustomObject]@{
            Server  = $item.Server
            Label   = $item.Label
            Status  = $status
            Stopped = $stopped
        }
    }

    [PSCustomObject]@{
        AllStopped = $allStopped
        Details    = $details
    }
}

function Test-N4TcpPort {
    <#
    .SYNOPSIS
        Teste rapidement si un port TCP repond sur un hote (ex. base de donnees),
        sans dependance au module NetTCPIP.
    .PARAMETER ComputerName
        Hote a tester.
    .PARAMETER Port
        Port TCP a tester.
    .PARAMETER TimeoutMs
        Delai d'attente en millisecondes (defaut 3000).
    #>
    param(
        [Parameter(Mandatory = $true)][string]$ComputerName,
        [Parameter(Mandatory = $true)][int]$Port,
        [int]$TimeoutMs = 3000
    )

    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $asyncResult = $client.BeginConnect($ComputerName, $Port, $null, $null)
        $success = $asyncResult.AsyncWaitHandle.WaitOne($TimeoutMs, $false)
        if ($success -and $client.Connected) {
            $client.EndConnect($asyncResult)
            return [PSCustomObject]@{ Success = $true; Error = $null }
        } else {
            return [PSCustomObject]@{ Success = $false; Error = "Timeout apres ${TimeoutMs}ms" }
        }
    } catch {
        return [PSCustomObject]@{ Success = $false; Error = $_.Exception.Message }
    } finally {
        $client.Close()
    }
}

# ============================================================
# PROTECTION D'INTEGRITE + CODE PIN
# ============================================================
# Chaque script protege verifie sa propre empreinte SHA-256 par rapport a
# Navis-Integrity.json au demarrage. Si le fichier a ete modifie (y compris
# une suppression de la mention de copyright), le script s'arrete et exige
# le code PIN (verifie contre le hash stocke dans Navis-Protection.json)
# avant d'autoriser l'execution malgre tout.
# IMPORTANT : ce mecanisme est un GARDE-FOU ET UNE TRACE D'AUDIT, pas une
# protection inviolable - un script PowerShell reste du texte en clair.
# Toute execution derogatoire (PIN valide sur fichier modifie) est
# consignee dans le log avec date, utilisateur et IP.

function Get-N4PinHash {
    <#
    .SYNOPSIS
        Calcule le hash SHA-256 d'un code PIN saisi de maniere securisee (SecureString).
    #>
    param([Parameter(Mandatory = $true)][System.Security.SecureString]$SecurePin)

    $bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($SecurePin)
    try {
        $plainPin = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto($bstr)
    } finally {
        [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    }
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    $bytes = $sha256.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($plainPin))
    return ([System.BitConverter]::ToString($bytes) -replace '-', '').ToLower()
}

function Test-N4Integrity {
    <#
    .SYNOPSIS
        Verifie que le fichier appelant n'a pas ete modifie depuis sa version
        de reference (Navis-Integrity.json). Si modifie, demande le code PIN.
    .PARAMETER ScriptPath
        Chemin complet du script a verifier (utiliser $PSCommandPath dans
        le script appelant).
    .OUTPUTS
        $true si l'execution est autorisee, $false sinon.
    #>
    param([Parameter(Mandatory = $true)][string]$ScriptPath)

    $manifestPath    = Join-Path $PSScriptRoot "Navis-Integrity.json"
    $protectionPath  = Join-Path $PSScriptRoot "Navis-Protection.json"
    $fileName        = Split-Path $ScriptPath -Leaf

    if (-not (Test-Path $manifestPath) -or -not (Test-Path $protectionPath)) {
        Write-N4Log -Message "Fichiers de protection introuvables (Navis-Integrity.json / Navis-Protection.json). Verification d'integrite ignoree pour '$fileName'." -Level WARN
        return $true
    }

    try {
        $manifest   = Get-Content -Path $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $protection = Get-Content -Path $protectionPath -Raw -Encoding UTF8 | ConvertFrom-Json
    } catch {
        Write-N4Log -Message "Impossible de lire les fichiers de protection : $($_.Exception.Message). Verification ignoree." -Level WARN
        return $true
    }

    $expectedHash = $manifest.Files.$fileName
    if (-not $expectedHash) {
        Write-N4Log -Message "Aucune empreinte de reference pour '$fileName' dans le manifeste. Verification ignoree pour ce fichier." -Level WARN
        return $true
    }

    $actualHash = (Get-FileHash -Path $ScriptPath -Algorithm SHA256).Hash

    if ($actualHash -ieq $expectedHash) {
        return $true
    }

    Write-N4Log -Message "ALERTE INTEGRITE : '$fileName' ne correspond plus a son empreinte de reference (modification detectee, y compris eventuellement le retrait de la mention de copyright)." -Level ERROR

    # Indices disponibles nativement (sans configuration d'audit prealable) :
    # ne garantissent PAS d'identifier le dernier editeur avec certitude,
    # mais donnent un premier indice immediat.
    try {
        $fileInfo = Get-Item -Path $ScriptPath -ErrorAction Stop
        $owner = (Get-Acl -Path $ScriptPath -ErrorAction Stop).Owner
        Write-N4Log -Message "Derniere modification du fichier : $($fileInfo.LastWriteTime)" -Level WARN
        Write-N4Log -Message "Proprietaire NTFS actuel : $owner (peut ne pas etre le dernier editeur - voir Get-N4FileModificationEvents.ps1 pour une tracabilite precise si l'audit de securite est active)." -Level WARN
    } catch {
        Write-N4Log -Message "Impossible de lire les metadonnees du fichier : $($_.Exception.Message)" -Level WARN
    }
    Write-Host ""
    Write-Host "=====================================================" -ForegroundColor Red
    Write-Host " CE FICHIER A ETE MODIFIE - CODE PIN REQUIS POUR CONTINUER" -ForegroundColor Red
    Write-Host "=====================================================" -ForegroundColor Red

    $maxAttempts = if ($protection.MaxAttempts) { [int]$protection.MaxAttempts } else { 3 }

    for ($i = 1; $i -le $maxAttempts; $i++) {
        $securePin = Read-Host -Prompt "Code PIN (6 chiffres) - tentative $i/$maxAttempts" -AsSecureString
        $enteredHash = Get-N4PinHash -SecurePin $securePin

        if ($enteredHash -ieq $protection.CopyrightPinHash) {
            Write-N4Log -Message "Code PIN valide. Execution autorisee malgre la modification de '$fileName'. Derogation enregistree a des fins d'audit." -Level WARN
            return $true
        } else {
            Write-Host "Code incorrect." -ForegroundColor Red
        }
    }

    Write-N4Log -Message "Echec de validation du code PIN apres $maxAttempts tentative(s) pour '$fileName'. ARRET DU SCRIPT." -Level ERROR
    return $false
}

Export-ModuleMember -Function Write-N4Log, Write-N4Instruction, New-N4LogSession, Format-N4Duration, `
    Get-N4LocalIPAddress, Confirm-N4Action, Confirm-N4ContinueOnUnknown, `
    Get-N4ReadinessSettings, Get-N4LogReaderScriptBlock, Get-N4LogResolverScriptBlock, New-N4LogWatchpoint, `
    Wait-N4ServiceRunning, Wait-N4LogReady, Wait-N4ComponentReady, Write-N4ReadinessOutcome, `
    Start-N4Component, Stop-N4Component, Wait-N4ServiceStatus, `
    Get-N4PendingUpdatesScriptBlock, Get-N4InstallUpdatesScriptBlock, `
    Test-N4ServerReachable, Confirm-N4AllServicesStopped, Test-N4TcpPort, `
    Test-N4Integrity, Get-N4PinHash -Variable N4Config
