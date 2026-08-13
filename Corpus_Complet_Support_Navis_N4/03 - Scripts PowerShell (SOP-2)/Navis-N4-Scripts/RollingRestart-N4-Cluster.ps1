<#
.SYNOPSIS
    Redemarre les noeuds du Cluster N4 UN PAR UN (rolling restart), sans
    jamais tout arreter en meme temps. Utilise pour : "slow consumer"
    (Fiche B/Annexe), incident ILOG, planificateur automatisation incoherent
    (Fiche F).

.DESCRIPTION
    Reference : SOP-2, Fiche F et manuel de diagnostic 7.8, 5.1.

    Chaque noeud est arrete, son arret est CONFIRME, il est redemarre, puis
    le script attend la PREUVE de sa reinitialisation dans le log applicatif
    avant de passer au noeud suivant.

    CE QUI A CHANGE PAR RAPPORT A LA VERSION PRECEDENTE
    ---------------------------------------------------
    L'ancienne version attendait au maximum 3 minutes (18 x 10 s) que le
    service repasse "Running", puis considerait le noeud redemarre. Un noeud
    N4 qui rejoint le cluster Hazelcast et reconstruit son cache depasse
    couramment ce delai : le script declarait alors un echec sur un noeud
    parfaitement sain, ou pire, passait au noeud suivant alors que le
    precedent n'avait pas fini de s'initialiser - exactement ce qu'un rolling
    restart doit eviter.
    Desormais l'attente porte sur le marqueur d'initialisation ecrit dans le
    log du noeud, avec un timeout configurable (30 min par defaut) dans
    Navis-Config.json, section Readiness > Components > Cluster.

.PARAMETER NodesToRestart
    Liste optionnelle des noeuds a redemarrer. Par defaut, tous les noeuds
    Cluster de la configuration. Utile pour ne cibler qu'un seul noeud
    identifie comme lent via JMX.

.PARAMETER PauseBetweenNodesSeconds
    Temps d'observation entre deux noeuds, une fois le precedent confirme
    operationnel (defaut 30 s). Laisse au cluster le temps de rebalancer.

.PARAMETER Unattended
    Ne pose aucune question. Un etat non prouve arrete la sequence au lieu
    d'etre soumis a l'operateur.

.EXAMPLE
    .\RollingRestart-N4-Cluster.ps1

.EXAMPLE
    .\RollingRestart-N4-Cluster.ps1 -NodesToRestart "N4CLUSTER02"
        Ne redemarre que le noeud identifie comme lent via JMX.

.NOTES
    Copyright : (c) KMKernel
#>

[CmdletBinding()]
param(
    [string[]]$NodesToRestart,
    [System.Management.Automation.PSCredential]$Credential,
    [int]$PauseBetweenNodesSeconds = 30,
    [switch]$Unattended
)

Import-Module "$PSScriptRoot\Navis-Common.psm1" -Force
$logFile = New-N4LogSession -ScriptName "RollingRestart-N4-Cluster"

if (-not (Test-N4Integrity -ScriptPath $PSCommandPath)) { return }
$cfg = $Global:N4Config
$icmParams = @{}
if ($Credential) { $icmParams["Credential"] = $Credential }

if (-not $NodesToRestart -or $NodesToRestart.Count -eq 0) {
    $NodesToRestart = $cfg.ClusterNodes
}

# Garde-fou : un noeud cible doit exister dans la configuration.
$inconnus = @($NodesToRestart | Where-Object { $cfg.ClusterNodes -notcontains $_ })
if ($inconnus.Count -gt 0) {
    Write-N4Log -Message "Noeud(s) inconnu(s) de la configuration : $($inconnus -join ', '). Verifier ClusterNodes dans Navis-Config.json. Arret du script." -Level ERROR
    return
}

Write-N4Log -Message "===== ROLLING RESTART - Noeuds cibles : $($NodesToRestart -join ', ') =====" -Level ACTION
Write-N4Instruction -Title "AVANT DE LANCER UN ROLLING RESTART" -Level WARN -Lines @(
    "Avez-vous identifie le ou les noeuds reellement en cause via JMX",
    "(thread bloque, ConsumerCount a zero, DequeueCount plat) ? Reference : SOP-2 Fiche F / manuel 5.1.",
    "Redemarrer tous les noeuds quand un seul est en cause allonge inutilement la degradation.",
    "",
    "Deroulement : pour chaque noeud, arret confirme -> redemarrage -> attente de la",
    "preuve de reinitialisation dans son log -> pause d'observation -> noeud suivant.",
    "Un seul noeud est indisponible a la fois : le cluster continue de servir.",
    "Si un noeud echoue, la sequence S'ARRETE : on ne degrade pas un cluster deja fragilise.",
    "Duree : comptez plusieurs dizaines de minutes pour l'ensemble des noeuds."
)

if (-not $Unattended) {
    if (-not (Confirm-N4Action -Prompt "Confirmez-vous le redemarrage progressif de ces noeuds ?")) {
        Write-N4Log -Message "Action annulee par l'utilisateur." -Level WARN
        return
    }
}

$serviceName = $cfg.ServiceNames.Cluster
$compteur = 0
$total = $NodesToRestart.Count
$reussis = @()
$interrompu = $false

foreach ($node in $NodesToRestart) {
    $compteur++
    Write-N4Log -Message "--- Noeud $compteur/$total : $node ---" -Level ACTION

    # 1. Arret confirme
    $stop = Stop-N4Component -ComputerName $node -ServiceName $serviceName `
                -ComponentKey "Cluster" -Label "Cluster Node ($node)" -IcmParams $icmParams
    if (-not $stop.Stopped) {
        Write-N4Instruction -Title "ROLLING RESTART INTERROMPU" -Level ERROR -Lines @(
            "Le noeud $node n'a pas confirme son arret (statut : $($stop.Status)).",
            "Aucun autre noeud ne sera touche : redemarrer un second noeud alors que le",
            "premier est dans un etat indetermine reduit le cluster sans garantie de retour.",
            "Traiter ce noeud manuellement avant de relancer ce script sur les noeuds restants.",
            "Noeuds deja traites avec succes : $(if ($reussis.Count) { $reussis -join ', ' } else { 'aucun' })"
        )
        $interrompu = $true
        break
    }

    # 2. Redemarrage + preuve reelle
    $start = Start-N4Component -ComputerName $node -ServiceName $serviceName `
                -ComponentKey "Cluster" -Label "Cluster Node ($node)" -IcmParams $icmParams

    if ($start.Status -eq "Failed") {
        Write-N4Instruction -Title "ROLLING RESTART INTERROMPU" -Level ERROR -Lines @(
            "Le noeud $node n'est pas reparti correctement : $($start.Reason)",
            "Les noeuds restants ne seront PAS touches.",
            "Le cluster tourne actuellement avec un noeud en moins : traiter ce point en priorite.",
            "Noeuds deja traites avec succes : $(if ($reussis.Count) { $reussis -join ', ' } else { 'aucun' })"
        )
        $interrompu = $true
        break
    }

    if ($start.Status -ne "Ready") {
        Write-N4Log -Message "Le noeud $node est demarre mais son etat n'est pas prouve." -Level WARN
        if (-not (Confirm-N4ContinueOnUnknown -Result $start -Unattended:$Unattended)) {
            Write-N4Log -Message "Sequence interrompue. Noeuds restants non touches." -Level WARN
            $interrompu = $true
            break
        }
    }

    $reussis += $node
    Write-N4Log -Message "Noeud $node traite ($compteur/$total)." -Level OK
    Write-N4Log -Message "Verification recommandee dans N4 : ce noeud doit apparaitre ACTIVE dans Cluster Services avant de passer au suivant." -Level WARN

    if ($compteur -lt $total) {
        Write-N4Log -Message "Pause d'observation de $PauseBetweenNodesSeconds s avant le noeud suivant (rebalancement du cluster)..." -Level INFO
        Start-Sleep -Seconds $PauseBetweenNodesSeconds
    }
}

Write-N4Log -Message "===== FIN ROLLING RESTART =====" -Level ACTION
if ($interrompu) {
    Write-N4Log -Message "Sequence INCOMPLETE : $($reussis.Count)/$total noeud(s) traite(s)." -Level ERROR
} else {
    Write-N4Log -Message "Tous les noeuds cibles ont ete traites ($($reussis.Count)/$total)." -Level OK
    Write-N4Instruction -Title "CONTROLES DE SORTIE" -Lines @(
        "1. Cluster Services : tous les noeuds ACTIVE.",
        "2. JMX : ConsumerCount > 0 et DequeueCount qui progresse de nouveau.",
        "3. Le symptome initial (lenteur, file qui monte) a-t-il disparu ?",
        "Si le symptome persiste apres un rolling restart complet, la cause n'est",
        "probablement pas dans les noeuds : reprendre le diagnostic (base, reseau, Bridge)."
    )
}
Write-N4Log -Message "Log complet de cette session : $logFile" -Level INFO
