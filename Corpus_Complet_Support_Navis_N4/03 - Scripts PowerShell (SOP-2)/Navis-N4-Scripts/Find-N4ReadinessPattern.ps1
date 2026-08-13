<#
.SYNOPSIS
    Aide a identifier, dans un log N4 reel, le marqueur de fin d'initialisation
    a declarer dans Navis-Config.json (section Readiness > ReadyPatterns).

.DESCRIPTION
    Les scripts de sequence n'attendent plus un statut Windows, mais une preuve
    ecrite dans le log applicatif. Encore faut-il savoir CE QUE votre version de
    N4 ecrit reellement quand un composant a fini de demarrer - et cela varie
    selon la version, le composant et la configuration de journalisation.

    Ce script ne devine rien a votre place : il lit la fin du log d'un composant
    et vous montre trois choses :
      1. les motifs actuellement configures et s'ils apparaissent dans ce log ;
      2. les lignes qui ressemblent a une fin d'initialisation (heuristique) ;
      3. les lignes qui ressemblent a une erreur.
    A vous de choisir le marqueur pertinent et de le reporter dans la config.

    METHODE RECOMMANDEE
      1. Redemarrer le composant hors production, ou recuperer un log dont vous
         savez que le demarrage a REUSSI.
      2. Lancer ce script sur ce log.
      3. Reperer la derniere ligne significative du demarrage (typiquement une
         ligne d'initialisation terminee, de tier web pret, ou de connexion
         etablie pour le Bridge).
      4. Reporter cette ligne, sous forme d'expression reguliere courte et
         stable, dans Navis-Config.json > Readiness > Components > <Cle> >
         ReadyPatterns.
      5. Relancer ce script pour verifier que le motif est bien reconnu.

    CHOISIR UN BON MARQUEUR
      - Stable d'une version a l'autre et d'un demarrage a l'autre.
      - Ecrit UNE SEULE FOIS, a la fin de l'initialisation - pas une ligne
        periodique qui apparaitrait meme sur un composant a moitie demarre.
      - Sans element variable (numero de port, duree, identifiant de session).
        Neutraliser les chiffres avec \d+ : "Server startup in \d+ ms".
      - Pour le Bridge, preferer la ligne de CONNEXION AU CENTER a la ligne de
        demarrage du daemon : c'est la connexion qui conditionne XPS.
      - Pour le Standby, choisir un marqueur de MODE VEILLE, jamais celui d'un
        Center actif.

.PARAMETER ComponentKey
    Composant a inspecter : Cluster, Center, Standby, Bridge, XPS, ECN4, ECN4Web.
    Le chemin du log est lu depuis Navis-Config.json.

.PARAMETER ComputerName
    Serveur portant le log. Par defaut, deduit du composant et de la config.
    Obligatoire pour Cluster (plusieurs noeuds possibles).

.PARAMETER LogPath
    Force un autre chemin de log que celui de la configuration (utile pour
    inspecter un log archive ou un fichier deja rapatrie localement).

.PARAMETER TailLines
    Nombre de lignes lues en fin de fichier (defaut 500).

.PARAMETER Local
    Lit le fichier sur la machine courante au lieu d'un serveur distant.

.EXAMPLE
    .\Find-N4ReadinessPattern.ps1 -ComponentKey Bridge

.EXAMPLE
    .\Find-N4ReadinessPattern.ps1 -ComponentKey Cluster -ComputerName N4CLUSTER01 -TailLines 1000

.EXAMPLE
    .\Find-N4ReadinessPattern.ps1 -ComponentKey Center -LogPath "C:\Temp\navis-apex.log" -Local
        Inspecte un log deja rapatrie sur le poste de l'operateur.

.NOTES
    Copyright : (c) KMKernel
    Script de LECTURE SEULE : il ne demarre, n'arrete et ne modifie rien.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("Cluster", "Center", "Standby", "Bridge", "XPS", "ECN4", "ECN4Web")]
    [string]$ComponentKey,

    [string]$ComputerName,
    [string]$LogPath,
    [int]$TailLines = 500,
    [switch]$Local,
    [System.Management.Automation.PSCredential]$Credential
)

Import-Module "$PSScriptRoot\Navis-Common.psm1" -Force
$logFile = New-N4LogSession -ScriptName "Find-N4ReadinessPattern"

if (-not (Test-N4Integrity -ScriptPath $PSCommandPath)) { return }

$cfg = $Global:N4Config
$icmParams = @{}
if ($Credential) { $icmParams["Credential"] = $Credential }

$settings = Get-N4ReadinessSettings -ComponentKey $ComponentKey
if (-not $LogPath) { $LogPath = $settings.LogPath }

if ([string]::IsNullOrWhiteSpace($LogPath)) {
    Write-N4Log -Message "Aucun chemin de log connu pour '$ComponentKey'. Renseigner Readiness > Components > $ComponentKey > LogPath dans Navis-Config.json, ou passer -LogPath." -Level ERROR
    return
}

if (-not $Local -and -not $ComputerName) {
    switch ($ComponentKey) {
        "Center"  { $ComputerName = $cfg.CenterNode }
        "Standby" { $ComputerName = $cfg.StandbyNode }
        "Bridge"  { $ComputerName = $cfg.BridgeHost }
        "XPS"     { $ComputerName = $cfg.XPSHost }
        "ECN4"    { $ComputerName = $cfg.ECN4Host }
        "ECN4Web" { $ComputerName = $cfg.ECN4Host }
        "Cluster" {
            Write-N4Log -Message "Plusieurs noeuds Cluster sont configures : preciser lequel avec -ComputerName (valeurs possibles : $($cfg.ClusterNodes -join ', '))." -Level ERROR
            return
        }
    }
}

$cible = if ($Local) { "machine locale" } else { $ComputerName }
Write-N4Log -Message "===== ANALYSE DU LOG : $ComponentKey sur $cible =====" -Level ACTION
Write-N4Log -Message "Fichier : $LogPath (lecture des $TailLines dernieres lignes)" -Level INFO

# ---- Lecture du log ----
$readBlock = {
    param($p, $n)
    # Un LogPath peut contenir un caractere generique : plusieurs composants N4
    # horodatent le nom de leur fichier (xps_AAAAMMJJHHMMSS...). On retient
    # alors le fichier le plus recent.
    if ($p -match '[\*\?]') {
        $cand = Get-ChildItem -Path $p -File -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if (-not $cand) {
            return [PSCustomObject]@{ Exists = $false; Lines = @(); SizeMB = 0; LastWrite = $null; Error = $null; Resolved = $null }
        }
        $p = $cand.FullName
    }
    if (-not (Test-Path -LiteralPath $p)) {
        return [PSCustomObject]@{ Exists = $false; Lines = @(); SizeMB = 0; LastWrite = $null; Error = $null; Resolved = $p }
    }
    try {
        $item = Get-Item -LiteralPath $p
        # FileShare ReadWrite : le composant garde son log ouvert en ecriture.
        $share = [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete
        $fs = [System.IO.File]::Open($p, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, $share)
        try {
            $maxBytes = 4MB
            $start = [Math]::Max(0, $fs.Length - $maxBytes)
            $count = [int]($fs.Length - $start)
            $fs.Seek($start, [System.IO.SeekOrigin]::Begin) | Out-Null
            $buf = New-Object byte[] $count
            $read = $fs.Read($buf, 0, $count)
            $text = [System.Text.Encoding]::UTF8.GetString($buf, 0, $read)
        } finally {
            $fs.Dispose()
        }
        $all = $text -split "`r?`n"
        $tail = if ($all.Count -gt $n) { $all[($all.Count - $n)..($all.Count - 1)] } else { $all }
        [PSCustomObject]@{
            Exists = $true; Lines = $tail
            SizeMB = [Math]::Round($item.Length / 1MB, 2)
            LastWrite = $item.LastWriteTime; Error = $null; Resolved = $p
        }
    } catch {
        [PSCustomObject]@{ Exists = $true; Lines = @(); SizeMB = 0; LastWrite = $null; Error = $_.Exception.Message; Resolved = $p }
    }
}

try {
    if ($Local) {
        $data = & $readBlock $LogPath $TailLines
    } else {
        $data = Invoke-Command -ComputerName $ComputerName @icmParams -ScriptBlock $readBlock -ArgumentList $LogPath, $TailLines -ErrorAction Stop
    }
} catch {
    Write-N4Log -Message "Lecture impossible : $($_.Exception.Message)" -Level ERROR
    Write-N4Log -Message "Verifier WinRM, les droits du compte sur ce fichier, et que le chemin est bien LOCAL au serveur (pas un chemin UNC vu depuis votre poste)." -Level WARN
    return
}

if (-not $data.Exists) {
    Write-N4Log -Message "Fichier introuvable : $LogPath sur $cible." -Level ERROR
    Write-N4Log -Message "Rappel : LogPath doit etre le chemin tel que le SERVEUR le voit (ex. D:\Navis\N4\logs\navis-apex.log), pas un chemin reseau." -Level WARN
    return
}
if ($data.Error) {
    Write-N4Log -Message "Fichier present mais illisible : $($data.Error)" -Level ERROR
    return
}

$lines = @($data.Lines | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
if ($data.Resolved -and $data.Resolved -ne $LogPath) {
    Write-N4Log -Message "Motif '$LogPath' resolu vers le fichier le plus recent : $($data.Resolved)" -Level INFO
}
Write-N4Log -Message "Fichier lu : $($data.SizeMB) Mo, derniere ecriture $($data.LastWrite), $($lines.Count) ligne(s) non vide(s) analysee(s)." -Level OK

# ---- 1. Les motifs configures apparaissent-ils ? ----
Write-N4Log -Message "--- 1/3 : Motifs actuellement configures pour '$ComponentKey' ---" -Level ACTION

if ($settings.ReadyPatterns.Count -eq 0) {
    Write-N4Log -Message "Aucun ReadyPattern configure pour ce composant : les scripts de sequence retourneront 'A CONFIRMER'." -Level WARN
} else {
    foreach ($p in $settings.ReadyPatterns) {
        $hits = @($lines | Where-Object { $_ -match $p })
        if ($hits.Count -gt 0) {
            Write-N4Log -Message "ReadyPattern '$p' : $($hits.Count) correspondance(s). Exemple : $($hits[-1].Trim())" -Level OK
        } else {
            Write-N4Log -Message "ReadyPattern '$p' : AUCUNE correspondance dans cette portion de log." -Level WARN
        }
    }
}

foreach ($p in $settings.ErrorPatterns) {
    $hits = @($lines | Where-Object { $_ -match $p })
    if ($hits.Count -gt 0) {
        Write-N4Log -Message "ErrorPattern '$p' : $($hits.Count) correspondance(s) - verifier qu'il ne s'agit pas de faux positifs (sinon, ajouter une entree dans IgnorePatterns)." -Level WARN
    }
}

# ---- 2. Candidats "fin d'initialisation" ----
Write-N4Log -Message "--- 2/3 : Lignes ressemblant a une fin d'initialisation ---" -Level ACTION
$candidats = @(
    "servlet initialized", "startup in", "started in", "initialization complete",
    "initialized successfully", "ready to accept", "listening on", "bound to",
    "started successfully", "is now active", "connected to", "connection established",
    "startup complete", "server started", "deployment .*finished", "waiting for"
)
$trouves = @()
foreach ($c in $candidats) {
    foreach ($l in $lines) {
        if ($l -match $c) { $trouves += [PSCustomObject]@{ Motif = $c; Ligne = $l.Trim() } }
    }
}

if ($trouves.Count -eq 0) {
    Write-N4Log -Message "Aucun candidat evident dans cette portion de log." -Level WARN
    Write-N4Log -Message "Elargir la fenetre (-TailLines 2000) ou relire un log couvrant un demarrage complet : la ligne recherchee est ecrite AU MOMENT du demarrage, pas pendant le fonctionnement normal." -Level INFO
} else {
    $groupes = $trouves | Group-Object Motif
    foreach ($g in $groupes) {
        $exemple = $g.Group[-1].Ligne
        if ($exemple.Length -gt 200) { $exemple = $exemple.Substring(0, 200) + "..." }
        Write-N4Log -Message "[$($g.Count) occurrence(s)] motif '$($g.Name)'" -Level INFO
        Write-N4Log -Message "    $exemple" -Level INFO
    }
    Write-N4Log -Message "Un motif avec UNE SEULE occurrence par demarrage est un bon candidat. Un motif tres repete ne prouve rien." -Level WARN
}

# ---- 3. Erreurs presentes ----
Write-N4Log -Message "--- 3/3 : Lignes ressemblant a une erreur ---" -Level ACTION
$erreurs = @($lines | Where-Object { $_ -match "(ERROR|SEVERE|FATAL|Exception|Caused by)" })
if ($erreurs.Count -eq 0) {
    Write-N4Log -Message "Aucune ligne d'erreur evidente dans cette portion de log." -Level OK
} else {
    Write-N4Log -Message "$($erreurs.Count) ligne(s) d'erreur reperee(s). Les 10 dernieres :" -Level WARN
    foreach ($e in ($erreurs | Select-Object -Last 10)) {
        $t = $e.Trim()
        if ($t.Length -gt 200) { $t = $t.Substring(0, 200) + "..." }
        Write-N4Log -Message "    $t" -Level WARN
    }
    Write-N4Log -Message "Les erreurs recurrentes et sans consequence connue se neutralisent via IgnorePatterns ; celles qui empechent reellement le demarrage se declarent dans ErrorPatterns (le script cesse alors d'attendre au lieu de consommer tout le timeout)." -Level INFO
}

Write-N4Instruction -Title "REPORTER LE RESULTAT DANS LA CONFIGURATION" -Level WARN -Lines @(
    "Editer Navis-Config.json, section :",
    "    Readiness > Components > $ComponentKey",
    "  LogPath       : $LogPath",
    "  ReadyPatterns : le ou les motifs retenus ci-dessus (expressions regulieres)",
    "  ErrorPatterns : les signatures d'echec reelles de ce composant",
    "  IgnorePatterns: les erreurs connues et benignes a ne pas prendre pour un echec",
    "Puis relancer ce script pour confirmer que le motif retenu est bien reconnu.",
    "Aucune regeneration du manifeste d'integrite n'est necessaire : les fichiers .json",
    "de configuration ne sont pas proteges, seuls les scripts le sont."
)
Write-N4Log -Message "Log complet de cette session : $logFile" -Level INFO
