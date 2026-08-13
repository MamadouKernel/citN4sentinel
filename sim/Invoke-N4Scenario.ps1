<#
.SYNOPSIS
    Joue un scenario de demarrage sur l'environnement N4 simule : ecrit dans
    les journaux, avec les delais et les marqueurs correspondants.

.DESCRIPTION
    C'est ce script qui donne vie au simulateur. Il reproduit ce qu'un
    composant N4 ecrit reellement pendant son initialisation : des lignes de
    progression pendant plusieurs dizaines de secondes, puis - ou non - le
    marqueur de fin.

    Chaque scenario correspond a un cas de recette du cahier des charges.
    Lancer le scenario d'un cote, observer N4 Sentinel de l'autre.

.PARAMETER Scenario
    DemarrageNominal   Tous les composants s'initialisent normalement.
                       Recette AC-02.

    NoeudLent          Le premier noeud Cluster n'atteint jamais son marqueur.
                       N4 Sentinel doit refuser de demarrer le suivant.
                       Recette AC-03.

    BridgeEnEchec      Le Bridge ecrit une signature d'echec. N4 Sentinel doit
                       couper court sans attendre le timeout, et ne pas lancer XPS.
                       Recette AC-04.

    CenterCorrompu     Le Center ecrit une IOException sur le dossier amq.
                       Doit orienter vers la Fiche A, pas vers un redemarrage.

    RotationJournal    Le journal repart a zero en pleine lecture. La preuve
                       doit survivre a la rotation.

    XPSNouveauFichier  XPS cree un nouveau journal horodate, comme en reel.
                       Le motif generique doit suivre.

.PARAMETER Racine
    Racine du simulateur. Defaut : C:\N4Simulateur

.PARAMETER Composant
    Restreint le scenario a un composant (Cluster1, Center, Bridge, XPS...).

.PARAMETER Acceleration
    Divise les delais. 1 = temps reel (le plus fidele), 10 = dix fois plus
    rapide (pratique pour un aller-retour de developpement).
    Defaut : 1.

.EXAMPLE
    .\Invoke-N4Scenario.ps1 -Scenario DemarrageNominal

.EXAMPLE
    .\Invoke-N4Scenario.ps1 -Scenario BridgeEnEchec -Acceleration 5

.NOTES
    Copyright : (c) KMKernel
    N'ecrit que dans les journaux du simulateur. Ne touche a aucun service.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("DemarrageNominal", "NoeudLent", "BridgeEnEchec",
                 "CenterCorrompu", "RotationJournal", "XPSNouveauFichier")]
    [string]$Scenario,

    [string]$Racine = "C:\N4Simulateur",
    [string]$Composant,
    [ValidateRange(1, 60)][int]$Acceleration = 1
)

$ErrorActionPreference = "Stop"

$cheminConfig = Join-Path $Racine "Navis-Config.simulateur.json"
if (-not (Test-Path $cheminConfig)) {
    Write-Host "Simulateur introuvable dans $Racine." -ForegroundColor Red
    Write-Host "Lancez d'abord : .\New-N4Simulateur.ps1" -ForegroundColor Yellow
    return
}

$config = Get-Content $cheminConfig -Raw -Encoding UTF8 | ConvertFrom-Json

function Horodatage { (Get-Date).ToString('yyyy-MM-dd HH:mm:ss,fff') }

function Resoudre-Journal {
    param([string]$Motif)
    # Un motif generique designe le fichier le plus recent - exactement ce que
    # fait le connecteur.
    if ($Motif -match '[\*\?]') {
        $recent = Get-ChildItem -Path $Motif -File -ErrorAction SilentlyContinue |
                  Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($recent) { return $recent.FullName }
        $dossier = Split-Path $Motif -Parent
        $nom = (Split-Path $Motif -Leaf) -replace '\*', (Get-Date -Format 'yyyyMMddHHmmss')
        return Join-Path $dossier $nom
    }
    return $Motif
}

function Ecrire-Ligne {
    param([string]$Chemin, [string]$Niveau, [string]$Thread, [string]$Message)
    $ligne = "{0} {1,-5} [{2}] {3}" -f (Horodatage), $Niveau, $Thread, $Message
    Add-Content -Path $Chemin -Value $ligne -Encoding UTF8
    Write-Host "  $ligne" -ForegroundColor DarkGray
}

function Jouer-Demarrage {
    param(
        [string]$Cle,
        [switch]$SansMarqueur,
        [string]$SignatureEchec,
        [switch]$NouveauFichier,
        [switch]$AvecRotation
    )

    $r = $config.Readiness.Components.$Cle
    if (-not $r) { Write-Host "  Composant '$Cle' inconnu du simulateur." -ForegroundColor Yellow; return }

    $chemin = Resoudre-Journal -Motif $r.LogPath

    # XPS recree son journal a chaque demarrage : on reproduit ce comportement.
    if ($NouveauFichier -and $r.LogPath -match '[\*\?]') {
        $dossier = Split-Path $r.LogPath -Parent
        $nom = (Split-Path $r.LogPath -Leaf) -replace '\*', (Get-Date -Format 'yyyyMMddHHmmss')
        $chemin = Join-Path $dossier $nom
        Set-Content -Path $chemin -Value "$(Horodatage) INFO  [main] New log file created at startup" -Encoding UTF8
        Write-Host "`n  --- $Cle : nouveau journal $([System.IO.Path]::GetFileName($chemin)) ---" -ForegroundColor Cyan
    } else {
        Write-Host "`n  --- $Cle : $([System.IO.Path]::GetFileName($chemin)) ---" -ForegroundColor Cyan
    }

    $duree = [Math]::Max(3, [int]($r.LogReadyTimeoutSeconds / 4 / $Acceleration))
    $pas = [Math]::Max(1, [int]($duree / 5))

    Ecrire-Ligne $chemin "INFO" "main" "Starting component, reading configuration"
    Start-Sleep -Seconds $pas
    Ecrire-Ligne $chemin "INFO" "main" "Database connection pool initialized (20 connections)"
    Start-Sleep -Seconds $pas

    if ($AvecRotation) {
        # Le journal repart a zero en pleine lecture : cas du log apex au-dela
        # de 10 Mo, ou de XPS qui se recree.
        Ecrire-Ligne $chemin "INFO" "main" "Log rotation triggered"
        Set-Content -Path $chemin -Value "$(Horodatage) INFO  [main] Log file rotated - continuing startup" -Encoding UTF8
        Write-Host "  >>> journal remis a zero <<<" -ForegroundColor Yellow
        Start-Sleep -Seconds $pas
    }

    Ecrire-Ligne $chemin "INFO" "main" "Loading cached data, joining cluster"
    Start-Sleep -Seconds $pas

    if ($SignatureEchec) {
        Ecrire-Ligne $chemin "ERROR" "main" $SignatureEchec
        Write-Host "  >>> signature d'echec : N4 Sentinel doit couper court <<<" -ForegroundColor Red
        return
    }

    Ecrire-Ligne $chemin "INFO" "main" "Compiling extensions"
    Start-Sleep -Seconds $pas

    if ($SansMarqueur) {
        Ecrire-Ligne $chemin "WARN" "main" "Still loading cache, this is taking longer than usual"
        Write-Host "  >>> aucun marqueur ne viendra : l'etat doit rester 'A confirmer' <<<" -ForegroundColor Yellow
        return
    }

    # La ligne est reconstruite depuis le MODELE, jamais depuis l'expression
    # reguliere : desechapper un motif pour en refaire du texte est fragile,
    # et une erreur ferait ecrire une ligne qui correspond au motif par
    # accident - le simulateur validerait alors sa propre erreur.
    $valeur = Get-Random -Minimum 40000 -Maximum 260000
    $modele = $r._marqueurModele
    $marqueur = if ($modele -and $modele -match '\{0\}') { $modele -f $valeur }
                elseif ($modele) { $modele }
                else { "startup complete" }

    Ecrire-Ligne $chemin "INFO" "main" $marqueur
    Write-Host "  >>> marqueur ecrit : le composant est operationnel <<<" -ForegroundColor Green
}

# ---------------------------------------------------------------------------
Write-Host "`n=== Scenario : $Scenario ===" -ForegroundColor Cyan
if ($Acceleration -gt 1) { Write-Host "    Delais divises par $Acceleration" -ForegroundColor DarkGray }

$cles = if ($Composant) { @($Composant) } else { @("Cluster", "Center", "Standby", "Bridge", "XPS", "ECN4", "ECN4Web") }

switch ($Scenario) {

    "DemarrageNominal" {
        foreach ($c in $cles) { Jouer-Demarrage -Cle $c }
        Write-Host "`n  Tous les composants ont ecrit leur marqueur." -ForegroundColor Green
    }

    "NoeudLent" {
        Jouer-Demarrage -Cle "Cluster" -SansMarqueur
        Write-Host "`n  Le noeud n'a pas confirme. N4 Sentinel ne doit PAS demarrer le suivant (AC-03)." -ForegroundColor Yellow
    }

    "BridgeEnEchec" {
        Jouer-Demarrage -Cle "Bridge" -SignatureEchec "java.net.SocketTimeoutException: Unable to connect to Center node after 3 attempts"
        Write-Host "`n  N4 Sentinel doit couper court sans attendre le timeout, et ne pas lancer XPS (AC-04)." -ForegroundColor Yellow
    }

    "CenterCorrompu" {
        Jouer-Demarrage -Cle "Center" -SignatureEchec "java.io.IOException: Failed to open store at \\NavisShared\amq\db.data - NegativeArraySizeException"
        Write-Host "`n  Doit orienter vers la Fiche A - sauvegarde puis reconstitution - pas vers un redemarrage." -ForegroundColor Yellow
    }

    "RotationJournal" {
        Jouer-Demarrage -Cle "Center" -AvecRotation
        Write-Host "`n  La rotation ne doit pas faire perdre la preuve." -ForegroundColor Yellow
    }

    "XPSNouveauFichier" {
        Jouer-Demarrage -Cle "XPS" -NouveauFichier
        Write-Host "`n  Le motif generique doit suivre le nouveau fichier et l'analyser depuis son debut." -ForegroundColor Yellow
    }
}

Write-Host ""
