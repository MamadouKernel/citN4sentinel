<#
.SYNOPSIS
    Demarre l'ensemble des composants Navis N4 dans l'ordre requis :
    Cluster (un par un) -> Center -> [Standby en option] -> Bridge -> XPS
    -> ECN4 -> ECN4Web.

.DESCRIPTION
    Reference : SOP-2 (Niveau 2/3), section "Sequence de demarrage".

    CE QUI A CHANGE PAR RAPPORT A LA VERSION PRECEDENTE
    ---------------------------------------------------
    Avant, chaque etape se contentait d'attendre que le service Windows
    passe "Running" pendant 180 secondes, puis passait a la suivante.
    C'etait faux sur le fond et trop court dans les faits :
      - "Running" signifie que Windows a lance le processus, PAS que la JVM
        N4 a fini de charger sa configuration, d'ouvrir la base, de rejoindre
        le cluster Hazelcast et d'initialiser son tier web ;
      - 180 secondes ne suffisent pas a un demarrage a froid.
    Le resultat : le script demarrait le noeud suivant (ou XPS avant que le
    Bridge soit reellement connecte) pendant que le precedent chargeait encore.

    Desormais chaque etape attend une PREUVE REELLE, en deux temps :
      1. le service Windows atteint Running                (timeout 5 min)
      2. le marqueur de fin d'initialisation apparait dans le log applicatif
         du composant                                      (timeout 30 min par defaut)
    Tous ces delais et marqueurs se reglent dans Navis-Config.json, section
    Readiness - aucune edition de code n'est necessaire.

    Trois issues possibles par etape, jamais un simple vrai/faux :
      OPERATIONNEL   -> on passe a l'etape suivante
      ECHEC          -> la sequence s'arrete, la dependance n'est pas satisfaite
      A CONFIRMER    -> l'etat n'est pas etabli ; l'operateur tranche
                        (ou la sequence s'arrete avec -Unattended)

.PARAMETER Credential
    Compte a utiliser sur les serveurs cibles. Sans ce parametre, le contexte
    de securite courant est utilise.

.PARAMETER Unattended
    Mode non surveille : aucune question n'est posee. Tout etat "A CONFIRMER"
    est traite comme bloquant et arrete la sequence. A utiliser pour une
    execution planifiee, jamais pour un demarrage de crise ou l'operateur
    est devant l'ecran.

.PARAMETER StartStandby
    Demarre aussi le Standby Center Node, apres le Center. DESACTIVE PAR
    DEFAUT : demarrer un Standby sans avoir confirme qu'un seul Center detient
    le role actif expose au conflit "deux Center actifs" (manuel 5.7).
    Ne l'activer qu'apres avoir verifie le role effectif du Center primaire.

.NOTES
    Copyright : (c) KMKernel

    PREREQUIS AVANT PREMIERE UTILISATION
      1. Renseigner les vrais noms de serveurs dans Navis-Config.json.
      2. Renseigner la section Readiness (LogPath + ReadyPatterns par
         composant). Sans elle, le script fonctionne mais ne peut rien
         prouver : il retournera "A CONFIRMER" a chaque etape.
         Utiliser Find-N4ReadinessPattern.ps1 sur un log de demarrage
         REUSSI pour relever les marqueurs reels de votre version N4.
      3. Verifier la connectivite WinRM : Test-WSMan <NomServeur>
      4. Executer depuis un compte Administrateur local sur chaque serveur
         cible (ou fournir -Credential).

.EXAMPLE
    .\Start-N4Sequence.ps1
        Demarrage complet supervise, l'operateur tranche les cas douteux.

.EXAMPLE
    .\Start-N4Sequence.ps1 -Credential (Get-Credential) -Unattended
        Demarrage planifie : s'arrete des qu'un etat n'est pas prouve.

.EXAMPLE
    .\Start-N4Sequence.ps1 -StartStandby
        Inclut le Standby dans la sequence (a n'utiliser qu'en connaissance
        de cause, apres verification du role du Center primaire).
#>

[CmdletBinding()]
param(
    [System.Management.Automation.PSCredential]$Credential,
    [switch]$Unattended,
    [switch]$StartStandby
)

Import-Module "$PSScriptRoot\Navis-Common.psm1" -Force
$logFile = New-N4LogSession -ScriptName "Start-N4Sequence"

if (-not (Test-N4Integrity -ScriptPath $PSCommandPath)) { return }

if (-not $Credential) {
    Write-N4Log -Message "Aucune credential fournie : utilisation du contexte de securite courant ($env:USERNAME)." -Level WARN
}

$cfg = $Global:N4Config
$icmParams = @{}
if ($Credential) { $icmParams["Credential"] = $Credential }

# ------------------------------------------------------------
# Portillon commun : que fait-on du resultat d'une etape ?
# ------------------------------------------------------------
function Test-N4Gate {
    <#
        Retourne $true si la sequence peut continuer, $false sinon.
        C'est le seul endroit qui decide - les etapes ne decident pas
        elles-memes, ce qui garantit un traitement homogene.
    #>
    param(
        [Parameter(Mandatory = $true)]$Result,
        [Parameter(Mandatory = $true)][string]$NextStepLabel
    )

    switch ($Result.Status) {
        "Ready" { return $true }
        "Failed" {
            Write-N4Instruction -Title "SEQUENCE INTERROMPUE" -Level ERROR -Lines @(
                "Etape en echec : $($Result.Component) sur $($Result.ComputerName).",
                "Etape suivante NON lancee : $NextStepLabel.",
                "Motif : $($Result.Reason)",
                "Rien d'autre ne sera demarre : demarrer la suite sur une dependance non satisfaite",
                "transforme un incident localise en incident general.",
                "Log complet de la session : $Global:N4CurrentLogFile"
            )
            return $false
        }
        default {
            # Unknown ou AlreadyRunning : l'etat n'est pas etabli.
            Write-N4Log -Message "Etape suivante concernee si vous poursuivez : $NextStepLabel." -Level WARN
            return (Confirm-N4ContinueOnUnknown -Result $Result -Unattended:$Unattended)
        }
    }
}

Write-N4Log -Message "===== DEBUT SEQUENCE DE DEMARRAGE N4 =====" -Level ACTION
Write-N4Instruction -Title "CE QUE CE SCRIPT VA FAIRE" -Lines @(
    "Ordre impose : Cluster (un par un) -> Center -> $(if ($StartStandby) { 'Standby -> ' })Bridge -> XPS -> ECN4 -> ECN4Web.",
    "Chaque etape attend une preuve reelle dans le log applicatif avant de passer a la suivante.",
    "Une etape peut donc durer plusieurs dizaines de minutes : c'est normal, ce n'est pas un blocage.",
    "Des messages de progression sont emis regulierement avec la derniere ligne de log observee.",
    "Mode : $(if ($Unattended) { 'NON SURVEILLE - tout etat non prouve arrete la sequence' } else { 'SUPERVISE - vous serez consulte sur les cas douteux' })",
    "Pendant le demarrage des noeuds : ne creez ni ne supprimez aucun job ou groupe de jobs dans N4.",
    "Interrompre ce script (Ctrl+C) n'arrete PAS les services deja lances : il faudra reprendre la main manuellement."
)

# ============================================================
# ETAPE 0 : tous les serveurs repondent-ils ?
# ============================================================
Write-N4Log -Message "--- Etape 0/7 : Verification que tous les serveurs sont joignables ---" -Level ACTION
$allServersForCheck = @($cfg.CenterNode, $cfg.StandbyNode, $cfg.BridgeHost, $cfg.XPSHost, $cfg.ECN4Host) + $cfg.ClusterNodes
$allServersForCheck = $allServersForCheck | Sort-Object -Unique

$unreachable = @()
foreach ($server in $allServersForCheck) {
    $test = Test-N4ServerReachable -ComputerName $server -IcmParams $icmParams
    if ($test.Reachable) {
        $latencyInfo = if ($test.LatencyMs) { "$($test.LatencyMs) ms" } else { "ping indisponible" }
        Write-N4Log -Message "$server : joignable (WinRM OK, ping $latencyInfo)." -Level OK
    } else {
        Write-N4Log -Message "$server : NON joignable (WinRM KO, ping = $($test.PingOk))." -Level ERROR
        $unreachable += $server
    }
}

if ($unreachable.Count -gt 0) {
    Write-N4Instruction -Title "SERVEUR(S) NON JOIGNABLE(S) - AUCUN SERVICE NE SERA DEMARRE" -Level ERROR -Lines @(
        "Concerne(s) : $($unreachable -join ', ')",
        "Verifier dans cet ordre :",
        "  1. Le serveur est-il allume et sorti de son demarrage ? (console / hyperviseur)",
        "  2. Resolution de nom : Resolve-DnsName <serveur>",
        "  3. WinRM actif sur la cible : Enable-PSRemoting -Force (en Administrateur, sur le serveur)",
        "  4. Test depuis cette machine : Test-WSMan <serveur>",
        "  5. Pare-feu : port 5985 (HTTP) ou 5986 (HTTPS) ouvert entre cette machine et la cible ?",
        "Relancer ce script une fois les serveurs joignables.",
        "Log complet de cette session : $logFile"
    )
    return
}
Write-N4Log -Message "Tous les serveurs sont joignables." -Level OK

# ============================================================
# ETAPE 0 BIS : etat initial reel des composants
# ============================================================
# Un demarrage complet suppose que tout est a l'arret. Si des services
# tournent deja, on le dit AVANT de commencer plutot que de le decouvrir
# etape par etape.
Write-N4Log -Message "--- Etape 0 bis/7 : Etat initial des composants ---" -Level ACTION

$inventaire = @()
foreach ($node in $cfg.ClusterNodes) {
    $inventaire += [PSCustomObject]@{ Server = $node; Service = $cfg.ServiceNames.Cluster; Label = "Cluster Node ($node)" }
}
$inventaire += [PSCustomObject]@{ Server = $cfg.CenterNode;  Service = $cfg.ServiceNames.Center;  Label = "Center Node" }
$inventaire += [PSCustomObject]@{ Server = $cfg.StandbyNode; Service = $cfg.ServiceNames.Standby; Label = "Standby Center Node" }
$inventaire += [PSCustomObject]@{ Server = $cfg.BridgeHost;  Service = $cfg.ServiceNames.Bridge;  Label = "XPS Bridge Daemon" }
$inventaire += [PSCustomObject]@{ Server = $cfg.XPSHost;     Service = $cfg.ServiceNames.XPS;     Label = "Service XPS" }
$inventaire += [PSCustomObject]@{ Server = $cfg.ECN4Host;    Service = $cfg.ServiceNames.ECN4;    Label = "ECN4 Daemon" }
$inventaire += [PSCustomObject]@{ Server = $cfg.ECN4Host;    Service = $cfg.ServiceNames.ECN4Web; Label = "ECN4Web" }

$dejaActifs = @()
foreach ($item in $inventaire) {
    try {
        $st = Invoke-Command -ComputerName $item.Server @icmParams -ScriptBlock {
            param($n)
            $s = Get-Service -Name $n -ErrorAction SilentlyContinue
            if ($null -eq $s) { "INTROUVABLE" } else { [string]$s.Status }
        } -ArgumentList $item.Service -ErrorAction Stop
    } catch {
        $st = "INJOIGNABLE"
    }

    switch ($st) {
        "Stopped" { Write-N4Log -Message "[$($item.Server)] $($item.Label) : Stopped (attendu avant un demarrage complet)." -Level OK }
        "Running" {
            Write-N4Log -Message "[$($item.Server)] $($item.Label) : DEJA RUNNING." -Level WARN
            $dejaActifs += "$($item.Label) sur $($item.Server)"
        }
        "INTROUVABLE" {
            Write-N4Log -Message "[$($item.Server)] $($item.Label) : service INTROUVABLE - verifier le nom dans Navis-Config.json (ServiceNames)." -Level ERROR
        }
        default { Write-N4Log -Message "[$($item.Server)] $($item.Label) : statut $st." -Level WARN }
    }
}

if ($dejaActifs.Count -gt 0) {
    Write-N4Instruction -Title "COMPOSANTS DEJA ACTIFS" -Level WARN -Lines (@(
        "Un demarrage complet part normalement d'un ecosysteme totalement a l'arret.",
        "Les composants suivants tournent deja :"
    ) + ($dejaActifs | ForEach-Object { "  - $_" }) + @(
        "Ils ne seront PAS redemarres par ce script, et leur preuve de demarrage",
        "n'est plus rejouable : leur etat restera 'A CONFIRMER'.",
        "Si l'objectif est un redemarrage propre : lancer d'abord Stop-N4Sequence.ps1,",
        "puis relancer celui-ci sur un ecosysteme entierement arrete."
    ))
    if (-not (Confirm-N4ContinueOnUnknown -Result ([PSCustomObject]@{
            Component = "etat initial"; ComputerName = "ecosysteme"; Status = "Unknown" }) -Unattended:$Unattended)) {
        Write-N4Log -Message "Sequence non lancee. Log complet : $logFile" -Level WARN
        return
    }
}

# ============================================================
# ETAPE 1 : noeuds Cluster, UN PAR UN
# ============================================================
Write-N4Log -Message "--- Etape 1/7 : Noeuds Cluster (un par un) ---" -Level ACTION
Write-N4Instruction -Title "REGLE N4 - DEMARRAGE DES NOEUDS CLUSTER" -Level WARN -Lines @(
    "Les noeuds se demarrent STRICTEMENT un par un.",
    "Un noeud doit etre pleinement initialise avant le lancement du suivant :",
    "demarrer deux noeuds en parallele expose a des incoherences de cache et",
    "de synchronisation Hazelcast difficiles a diagnostiquer ensuite.",
    "Aucune creation ni suppression de job ou de groupe de jobs pendant cette phase."
)

$rang = 0
$total = $cfg.ClusterNodes.Count
foreach ($node in $cfg.ClusterNodes) {
    $rang++
    Write-N4Log -Message "--- Noeud Cluster $rang/$total : $node ---" -Level ACTION

    $res = Start-N4Component -ComputerName $node -ServiceName $cfg.ServiceNames.Cluster `
                -ComponentKey "Cluster" -Label "Cluster Node ($node)" -IcmParams $icmParams

    $suivant = if ($rang -lt $total) { "noeud Cluster $($rang + 1)/$total" } else { "Center Node" }
    if (-not (Test-N4Gate -Result $res -NextStepLabel $suivant)) {
        Write-N4Log -Message "Log complet de cette session : $logFile" -Level INFO
        return
    }
}

# ============================================================
# ETAPE 2 : Center Node
# ============================================================
Write-N4Log -Message "--- Etape 2/7 : Center Node ---" -Level ACTION
$res = Start-N4Component -ComputerName $cfg.CenterNode -ServiceName $cfg.ServiceNames.Center `
            -ComponentKey "Center" -Label "Center Node" -IcmParams $icmParams

if (-not (Test-N4Gate -Result $res -NextStepLabel "XPS Bridge Daemon")) {
    Write-N4Log -Message "Log complet de cette session : $logFile" -Level INFO
    return
}

Write-N4Instruction -Title "VERIFICATION MANUELLE - ROLE DU CENTER" -Level WARN -Lines @(
    "Le log confirme que le service Center a fini son initialisation.",
    "Il ne dit PAS lequel des deux Center detient le role actif.",
    "Avant de poursuivre, ouvrir dans N4 : Administration > Cluster Services",
    "et confirmer que CenterNode est bien ACTIVE (et pas LOADING ni WAITING).",
    "Verifier egalement qu'UN SEUL Center est actif : deux Center actifs",
    "simultanement est un incident (manuel de diagnostic 5.7), pas un etat sain."
)

# ============================================================
# ETAPE 3 : Standby Center Node (optionnel)
# ============================================================
if ($StartStandby) {
    Write-N4Log -Message "--- Etape 3/7 : Standby Center Node ---" -Level ACTION
    Write-N4Instruction -Title "AVANT DE DEMARRER LE STANDBY" -Level WARN -Lines @(
        "Confirmer que le Center primaire detient bien le role ACTIVE.",
        "Demarrer un Standby alors que le role actif n'est pas etabli est la voie",
        "directe vers deux Center actifs simultanement - et vers une corruption",
        "du verrou ActiveMQ partage.",
        "Un Standby sain n'affiche PAS le marqueur d'initialisation du tier web :",
        "c'est le comportement normal d'une instance en attente."
    )
    if ($Unattended -or (Confirm-N4Action -Prompt "Le Center primaire est-il confirme ACTIVE, seul actif ?")) {
        $res = Start-N4Component -ComputerName $cfg.StandbyNode -ServiceName $cfg.ServiceNames.Standby `
                    -ComponentKey "Standby" -Label "Standby Center Node" -IcmParams $icmParams
        if (-not (Test-N4Gate -Result $res -NextStepLabel "XPS Bridge Daemon")) {
            Write-N4Log -Message "Log complet de cette session : $logFile" -Level INFO
            return
        }
    } else {
        Write-N4Log -Message "Demarrage du Standby ignore a la demande de l'operateur. Poursuite de la sequence." -Level WARN
    }
} else {
    Write-N4Log -Message "--- Etape 3/7 : Standby Center Node - IGNOREE (parametre -StartStandby non fourni) ---" -Level INFO
    Write-N4Log -Message "Le Standby doit alors etre verifie et demarre separement par un operateur habilite." -Level WARN
}

# ============================================================
# ETAPE 4 : XPS Bridge Daemon
# ============================================================
Write-N4Log -Message "--- Etape 4/7 : XPS Bridge Daemon ---" -Level ACTION
Write-N4Instruction -Title "POURQUOI LE BRIDGE AVANT XPS" -Lines @(
    "XPS ne dialogue avec N4 qu'a travers le Bridge.",
    "Demarrer XPS avant que le Bridge soit connecte au Center produit une",
    "desynchronisation N4/XPS qui ne se resorbe pas toute seule : il faut ensuite",
    "redemarrer toute la chaine (Fiche C du SOP-2).",
    "Le script ne demarrera donc PAS XPS tant que le Bridge n'est pas prouve operationnel."
)

$res = Start-N4Component -ComputerName $cfg.BridgeHost -ServiceName $cfg.ServiceNames.Bridge `
            -ComponentKey "Bridge" -Label "XPS Bridge Daemon" -IcmParams $icmParams

if (-not (Test-N4Gate -Result $res -NextStepLabel "Service XPS")) {
    Write-N4Log -Message "XPS n'est PAS demarre : sa dependance Bridge n'est pas satisfaite." -Level ERROR
    Write-N4Log -Message "Log complet de cette session : $logFile" -Level INFO
    return
}

# ============================================================
# ETAPE 5 : Service XPS
# ============================================================
Write-N4Log -Message "--- Etape 5/7 : Service XPS ---" -Level ACTION
Write-N4Log -Message "Le chargement initial de XPS (plan de parc, equipements) est long : le timeout configure est volontairement eleve." -Level INFO

$res = Start-N4Component -ComputerName $cfg.XPSHost -ServiceName $cfg.ServiceNames.XPS `
            -ComponentKey "XPS" -Label "Service XPS" -IcmParams $icmParams

if (-not (Test-N4Gate -Result $res -NextStepLabel "ECN4 Daemon")) {
    Write-N4Log -Message "Log complet de cette session : $logFile" -Level INFO
    return
}

# ============================================================
# ETAPE 6 : ECN4 Daemon
# ============================================================
Write-N4Log -Message "--- Etape 6/7 : ECN4 Daemon ---" -Level ACTION
$res = Start-N4Component -ComputerName $cfg.ECN4Host -ServiceName $cfg.ServiceNames.ECN4 `
            -ComponentKey "ECN4" -Label "ECN4 Daemon" -IcmParams $icmParams

if (-not (Test-N4Gate -Result $res -NextStepLabel "ECN4Web")) {
    Write-N4Log -Message "Log complet de cette session : $logFile" -Level INFO
    return
}

# ============================================================
# ETAPE 7 : ECN4Web
# ============================================================
Write-N4Log -Message "--- Etape 7/7 : ECN4Web ---" -Level ACTION
$res = Start-N4Component -ComputerName $cfg.ECN4Host -ServiceName $cfg.ServiceNames.ECN4Web `
            -ComponentKey "ECN4Web" -Label "ECN4Web" -IcmParams $icmParams
if ($res.Status -ne "Ready") {
    Write-N4Log -Message "Derniere etape terminee sans preuve : l'etat d'ECN4Web reste a confirmer avant de declarer la sequence reussie." -Level WARN
}

# ============================================================
# RECETTE TECHNIQUE FINALE
# ============================================================
Write-N4Log -Message "===== FIN SEQUENCE DE DEMARRAGE N4 =====" -Level ACTION
Write-N4Instruction -Title "RECETTE A REALISER AVANT DE DECLARER LE DEMARRAGE TERMINE" -Level WARN -Lines @(
    "Ce script prouve que chaque composant a fini son initialisation.",
    "Il ne prouve pas que la chaine fonctionne de bout en bout. A verifier maintenant :",
    "  1. Cluster Services : tous les noeuds ACTIVE (ni LOADING, ni WAITING).",
    "  2. Un seul Center actif ; Standby en veille sans conflit de role.",
    "  3. Bridge : files bridge.* avec ConsumerCount > 0 et QueueSize qui ne s'accumule pas (JMX).",
    "  4. Synchronisation N4/XPS : modifier un champ simple dans N4 et chronometrer",
    "     son apparition dans XPS.",
    "  5. ECN4 : se connecter avec un CHE et confirmer que le poste repond.",
    "  6. Dossiers partages accessibles et EDI de nouveau consommes.",
    "  7. Relire les logs applicatifs des 15 dernieres minutes : aucune erreur critique recente.",
    "Tant que ces points ne sont pas verifies, l'incident ou la fenetre de maintenance",
    "ne doit pas etre cloture."
)
Write-N4Log -Message "Log complet de cette session : $logFile" -Level INFO
