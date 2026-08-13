<#
.SYNOPSIS
    Cree un environnement N4 simule : arborescence de journaux, configuration
    importable, et optionnellement de faux services Windows.

.DESCRIPTION
    N4 Sentinel doit pouvoir etre developpe et recette sans acces a un
    environnement Navis reel. Ce simulateur fournit une cible complete :
    sept composants, leurs journaux, leurs marqueurs de demarrage et leurs
    services.

    Il ne remplace pas une recette contre un vrai N4 avant mise en production.
    Il remplace la DEPENDANCE a un vrai N4 pendant le developpement, ce qui
    n'est pas la meme chose.

    DEUX NIVEAUX
      Niveau 1 - journaux seuls (aucun privilege)
        Cree l'arborescence des journaux et la configuration. Suffit a
        valider tout le mecanisme de preuve de demarrage : marqueurs,
        lecture incrementale, rotation, signatures d'echec, motifs generiques.
        C'est le coeur du produit.

      Niveau 2 - faux services Windows (-AvecServices, ADMINISTRATEUR requis)
        Cree en plus de vrais services Windows portant les noms N4, pointant
        vers un executable inoffensif. Permet d'exercer reellement l'arret,
        le demarrage et le sequencement.

.PARAMETER Racine
    Dossier ou l'environnement simule est cree.
    Defaut : C:\N4Simulateur

.PARAMETER AvecServices
    Cree aussi de faux services Windows. Necessite une console ADMINISTRATEUR.

.PARAMETER NombreNoeudsCluster
    Nombre de noeuds Cluster a simuler. Defaut : 3.

.EXAMPLE
    .\New-N4Simulateur.ps1
        Niveau 1 : journaux et configuration, sans privilege particulier.

.EXAMPLE
    .\New-N4Simulateur.ps1 -AvecServices
        Niveau 2, en console administrateur.

.NOTES
    Copyright : (c) KMKernel
    Tout ce que cree ce script se supprime avec Remove-N4Simulateur.ps1.
#>

[CmdletBinding()]
param(
    [string]$Racine = "C:\N4Simulateur",
    [switch]$AvecServices,
    [ValidateRange(1, 10)][int]$NombreNoeudsCluster = 3
)

$ErrorActionPreference = "Stop"

function Ecrire-Etape { param([string]$m) Write-Host "`n=== $m ===" -ForegroundColor Cyan }
function Ecrire-Ok    { param([string]$m) Write-Host "  OK   $m" -ForegroundColor Green }
function Ecrire-Info  { param([string]$m) Write-Host "  ...  $m" -ForegroundColor Gray }
function Ecrire-Alerte{ param([string]$m) Write-Host "  !    $m" -ForegroundColor Yellow }

# ---------------------------------------------------------------------------
# Definition des composants simules
# ---------------------------------------------------------------------------
# Les marqueurs reprennent ce que la documentation editeur confirme pour les
# noeuds N4 et le Center - "Web tier servlet 'action' initialized" - et des
# formulations plausibles pour les autres. Le but n'est pas d'imiter Navis a
# la ligne pres : c'est de fournir une cible qui se comporte comme lui, avec
# des marqueurs uniques par demarrage et des delais realistes.

$composants = @()

for ($i = 1; $i -le $NombreNoeudsCluster; $i++) {
    $composants += [PSCustomObject]@{
        Cle          = "Cluster"
        Nom          = "N4SIM-CLUSTER$('{0:00}' -f $i)"
        Service      = "N4Sim Cluster Node $i"
        DossierLog   = "ProgramData\Navis\cluster$i\logs"
        FichierLog   = "navis-apex.log"
        Motif        = $null
        Marqueur     = "Web tier servlet 'action' initialized in {0} ms"
        DureeSecondes= 45
    }
}

$composants += [PSCustomObject]@{
    Cle = "Center"; Nom = "N4SIM-CENTER01"; Service = "N4Sim Center Node"
    DossierLog = "ProgramData\Navis\center\logs"; FichierLog = "navis-apex.log"; Motif = $null
    Marqueur = "Web tier servlet 'action' initialized in {0} ms"; DureeSecondes = 60
}
$composants += [PSCustomObject]@{
    Cle = "Standby"; Nom = "N4SIM-STANDBY01"; Service = "N4Sim Standby Center Node"
    DossierLog = "ProgramData\Navis\standby\logs"; FichierLog = "navis-apex.log"; Motif = $null
    Marqueur = "Standby mode active - waiting for master lock"; DureeSecondes = 30
}
$composants += [PSCustomObject]@{
    Cle = "Bridge"; Nom = "N4SIM-XPSBRIDGE01"; Service = "N4Sim XPS Bridge Daemon"
    DossierLog = "ProgramData\Navis\bridge\logs"; FichierLog = "navis-bridged_{0:yyyyMMdd}.log"
    Motif = "navis-bridged_*.log"
    Marqueur = "Connection established to Center node - bridge is ACTIVE"; DureeSecondes = 50
}
$composants += [PSCustomObject]@{
    # Le journal XPS est horodate dans son nom ET repart a zero a chaque
    # demarrage : c'est ce cas qui justifie la resolution par motif generique.
    Cle = "XPS"; Nom = "N4SIM-XPSBRIDGE01"; Service = "N4Sim XPS Service"
    DossierLog = "ProgramData\Navis\xps\log"; FichierLog = "xps_{0:yyyyMMddHHmmss}.log"
    Motif = "xps_*.log"
    Marqueur = "XPS initialization complete - {0} equipment loaded"; DureeSecondes = 90
}
$composants += [PSCustomObject]@{
    Cle = "ECN4"; Nom = "N4SIM-ECN401"; Service = "N4Sim ECN4 Daemon"
    DossierLog = "ProgramData\Navis\ecn4\logs"; FichierLog = "navis-ecn4_{0:yyyyMMdd}.log"
    Motif = "navis-ecn4_*.log"
    Marqueur = "ECN4 startup complete - listening for equipment"; DureeSecondes = 35
}
$composants += [PSCustomObject]@{
    Cle = "ECN4Web"; Nom = "N4SIM-ECN401"; Service = "N4Sim ECN4web"
    DossierLog = "ProgramData\Navis\ecn4web\logs"; FichierLog = "navis-ecn4web_{0:yyyyMMdd}.log"
    Motif = "navis-ecn4web_*.log"
    Marqueur = "Server startup in {0} ms"; DureeSecondes = 25
}

# ---------------------------------------------------------------------------
Ecrire-Etape "Environnement N4 simule"

$estAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
            ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if ($AvecServices -and -not $estAdmin) {
    Write-Host "-AvecServices exige une console PowerShell ADMINISTRATEUR." -ForegroundColor Red
    Write-Host "Relancez sans ce parametre pour le niveau 1 (journaux seuls), qui suffit" -ForegroundColor Yellow
    Write-Host "a valider toute la preuve de demarrage." -ForegroundColor Yellow
    return
}

New-Item -ItemType Directory -Path $Racine -Force | Out-Null
Ecrire-Ok "Racine : $Racine"

# ---------------------------------------------------------------------------
Ecrire-Etape "Arborescence des journaux"

$maintenant = Get-Date
$readiness = [ordered]@{}

foreach ($c in $composants) {
    $dossier = Join-Path $Racine $c.DossierLog
    New-Item -ItemType Directory -Path $dossier -Force | Out-Null

    $nomFichier = if ($c.FichierLog -like "*{0*") { $c.FichierLog -f $maintenant } else { $c.FichierLog }
    $chemin = Join-Path $dossier $nomFichier

    # Un journal preexistant portant DEJA le marqueur d'un demarrage anterieur.
    # C'est volontaire : il permet de verifier que le point de reference
    # empeche bien de relire une preuve perimee.
    if (-not (Test-Path $chemin)) {
        $anterieur = $maintenant.AddDays(-2)
        @(
            "$($anterieur.ToString('yyyy-MM-dd HH:mm:ss,fff')) INFO  [main] Starting component"
            "$($anterieur.AddSeconds(30).ToString('yyyy-MM-dd HH:mm:ss,fff')) INFO  [main] $($c.Marqueur -f 42000)"
            "$($anterieur.AddHours(6).ToString('yyyy-MM-dd HH:mm:ss,fff')) INFO  [sched] heartbeat ok"
        ) | Set-Content -Path $chemin -Encoding UTF8
    }

    $cheminMotif = if ($c.Motif) { Join-Path $dossier $c.Motif } else { $chemin }

    if (-not $readiness.Contains($c.Cle)) {
        # Construction du motif : on echappe les parties litterales du modele
        # et on place \d+ la ou le modele porte un nombre variable. Passer par
        # un jeton intermediaire ne fonctionne pas - [regex]::Escape echappe
        # aussi le jeton, qui devient alors introuvable.
        $parties = $c.Marqueur -split '\{0\}'
        $motifRegex = ($parties | ForEach-Object { [regex]::Escape($_) }) -join '\d+'

        $readiness[$c.Cle] = [ordered]@{
            # Modele du marqueur, lu par Invoke-N4Scenario.ps1 pour ecrire une
            # ligne realiste. Prefixe par un tiret bas : N4 Sentinel ignore les
            # cles de ce type a l'import.
            _marqueurModele = $c.Marqueur
            LogPath       = $cheminMotif
            ReadyPatterns = @($motifRegex)
            ErrorPatterns = @("OutOfMemoryError", "FATAL", "Unable to (start|connect)", "NegativeArraySizeException")
            LogReadyTimeoutSeconds = [int]($c.DureeSecondes * 4)
            PollIntervalSeconds    = 5
            ProgressEverySeconds   = 15
        }
    }

    Ecrire-Info "$($c.Service) -> $cheminMotif"
}
Ecrire-Ok "$($composants.Count) journaux prepares"

# ---------------------------------------------------------------------------
Ecrire-Etape "Configuration importable dans N4 Sentinel"

$noeuds = @($composants | Where-Object { $_.Cle -eq "Cluster" } | ForEach-Object { $_.Nom })

$config = [ordered]@{
    _comment    = "Environnement N4 SIMULE, genere par New-N4Simulateur.ps1. Ne designe aucun serveur reel."
    CenterNode  = ($composants | Where-Object Cle -eq "Center").Nom
    StandbyNode = ($composants | Where-Object Cle -eq "Standby").Nom
    ClusterNodes= $noeuds
    BridgeHost  = ($composants | Where-Object Cle -eq "Bridge").Nom
    XPSHost     = ($composants | Where-Object Cle -eq "XPS").Nom
    ECN4Host    = ($composants | Where-Object Cle -eq "ECN4").Nom
    ServiceNames= [ordered]@{
        Center  = ($composants | Where-Object Cle -eq "Center").Service
        Cluster = ($composants | Where-Object Cle -eq "Cluster" | Select-Object -First 1).Service
        Standby = ($composants | Where-Object Cle -eq "Standby").Service
        Bridge  = ($composants | Where-Object Cle -eq "Bridge").Service
        XPS     = ($composants | Where-Object Cle -eq "XPS").Service
        ECN4    = ($composants | Where-Object Cle -eq "ECN4").Service
        ECN4Web = ($composants | Where-Object Cle -eq "ECN4Web").Service
    }
    SharedFolder  = (Join-Path $Racine "NavisShared")
    DatabaseHost  = "localhost"
    DatabasePort  = 1433
    DatabaseEngine= "SQL Server"
    LocalLogFolder= (Join-Path $Racine "SentinelLogs")
    Readiness     = [ordered]@{ Components = $readiness }
}

New-Item -ItemType Directory -Path $config.SharedFolder -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $config.SharedFolder "amq") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $config.SharedFolder "conf") -Force | Out-Null
New-Item -ItemType Directory -Path $config.LocalLogFolder -Force | Out-Null

$cheminConfig = Join-Path $Racine "Navis-Config.simulateur.json"
$config | ConvertTo-Json -Depth 8 | Set-Content -Path $cheminConfig -Encoding UTF8
Ecrire-Ok "Configuration : $cheminConfig"

# ---------------------------------------------------------------------------
if ($AvecServices) {
    Ecrire-Etape "Faux services Windows"
    Ecrire-Alerte "Ces services ne font rien. Ils existent pour etre demarres et arretes."

    # cmd.exe en attente indefinie : un processus qui vit, ne consomme rien,
    # et repond proprement a une demande d'arret.
    $binaire = "$env:SystemRoot\System32\cmd.exe /c pause"

    foreach ($c in ($composants | Sort-Object Service -Unique)) {
        if (Get-Service -Name $c.Service -ErrorAction SilentlyContinue) {
            Ecrire-Info "$($c.Service) existe deja"
            continue
        }
        New-Service -Name $c.Service -BinaryPathName $binaire `
                    -DisplayName $c.Service -StartupType Manual `
                    -Description "Service simule N4 Sentinel - aucun effet reel" | Out-Null
        Ecrire-Ok "$($c.Service)"
    }
} else {
    Ecrire-Etape "Faux services Windows - ignores"
    Ecrire-Info "Niveau 1 : journaux seuls. Toute la preuve de demarrage est validable ainsi."
    Ecrire-Info "Pour le niveau 2, relancer en administrateur avec -AvecServices."
}

# ---------------------------------------------------------------------------
Ecrire-Etape "Termine"
Write-Host "  1. Dans N4 Sentinel, creez un environnement de type Test." -ForegroundColor White
Write-Host "  2. Importez : $cheminConfig" -ForegroundColor White
Write-Host "  3. Jouez un scenario :" -ForegroundColor White
Write-Host "         .\Invoke-N4Scenario.ps1 -Scenario DemarrageNominal" -ForegroundColor White
Write-Host ""
Ecrire-Alerte "Le simulateur ne remplace pas une recette contre un vrai N4 avant"
Ecrire-Alerte "toute mise en production. Il remplace la dependance a un vrai N4"
Ecrire-Alerte "pendant le developpement."
Write-Host ""
