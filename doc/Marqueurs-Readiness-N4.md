# Marqueurs de preuve (Readiness) par composant Navis N4

> Source : Kaleris **N4 IT Administrator 4.x — Day 1: Installation & Startup** (modules 1.6/1.7),
> distillée dans `Corpus_Complet_Support_Navis_N4` et la MERISE (RG-0201, RG-DÉMARRAGE-01).
> À reporter dans la fiche composant (bloc « Preuve de Démarrage ») ou dans
> `Navis-Config.json > Readiness > Components > <Clé>`.

## Principe (RG-0201)

L'état d'un composant n'est **jamais** établi par le seul « service Running ». Il faut :
service = Running **ET** journal écrit **ET** motif `ReadyPattern` détecté (ou service up stable
au-delà de la fenêtre de preuve, car le marqueur est écrit une seule fois puis chassé par la rotation).

## Chemins de log (installation standard)

| Composant | Chemin local (vu depuis le serveur, jamais UNC) |
|---|---|
| Cluster Node | `C:\ProgramData\Navis\<nom-du-nœud>\logs\navis-apex.log` |
| Center / Standby | `C:\ProgramData\Navis\<nom-du-nœud>\logs\navis-apex.log` |
| XPS | `C:\ProgramData\Navis\xps\log\xps_*` |
| Bridge | `C:\ProgramData\Navis\bridge\logs\navis-bridged*` |
| ECN4 Daemon | `C:\ProgramData\Navis\ecn4\logs\navis-ecn4*` |
| ECN4Web | `C:\ProgramData\Navis\ecn4web\logs\navis-ecn4web*` |

> Le `*` est **indispensable** pour XPS / Bridge / ECN4 / ECN4Web : ils créent un
> **nouveau fichier horodaté à chaque démarrage**. Le `*` résout vers le plus récent.
> Vérifier le nom exact du sous-dossier de chaque nœud sur vos serveurs.

## Marqueurs « prêt » (ReadyPatterns)

| Composant | ReadyPattern | Statut |
|---|---|---|
| **Cluster Node** | `Web tier servlet .*initialized` | ✅ **documenté éditeur** |
| **Center Node** | `Web tier servlet .*initialized` | ✅ **documenté éditeur** |
| **ECN4Web** | `Web tier servlet .*initialized` | 🔶 probable (tier web) — à confirmer par relevé |
| **Standby Center** | *(vide)* | ✅ **volontairement aucun** — un Standby sain n'écrit pas « initialized » ; s'il l'écrit pendant que le primaire est actif = conflit de rôle |
| **Bridge** | *(à relever)* — préférer la ligne de **connexion au Center** | 🔶 pas de ligne fixe ; doc → statut **ACTIVE** |
| **XPS** | *(à relever)* | 🔶 pas de ligne fixe ; doc → statut **ACTIVE** |
| **ECN4 Daemon** | *(à relever)* — n'écrit **pas** « Web tier servlet » | 🔶 pas de ligne fixe |

### Relever les marqueurs manquants (Bridge / XPS / ECN4)

Depuis le serveur Sentinel, sur un **démarrage réussi** :
```powershell
.\Find-N4ReadinessPattern.ps1 -ComponentKey Bridge -ComputerName <SRV>.<domaine>
```
Il affiche les lignes ressemblant à une fin d'initialisation → choisir une ligne :
- écrite **une seule fois** en fin d'init (pas une ligne périodique) ;
- **sans valeur variable** (neutraliser les chiffres : `Server startup in \d+ ms`) ;
- pour le **Bridge** : la ligne de **connexion au Center** (conditionne XPS).

## Marqueurs d'échec précoce (ErrorPatterns) — déjà dans le catalogue de signatures

Ces motifs sont déjà reconnus par le diagnostic de N4 Sentinel (`SignatureCatalogue`) ;
utiles aussi en `ErrorPatterns` de la preuve :

| Cas | Motif |
|---|---|
| Échec de démarrage applicatif | `Failed to start\|Startup failed\|Application startup aborted\|Context initialization failed\|SEVERE.{0,60}start` |
| Split-brain / partition Hazelcast | `split.?brain\|Merging nodes\|cluster partition\|This node is not the master\|master changed` |
| Bridge bloqué avant ACTIVE | `[Bb]ridge.{0,80}(WAITING\|LOADING)\|WAITING.{0,40}Center\s*Node` |
| Nœud INACTIVE (heartbeat perdu) | `\bINACTIVE\b` |

## Statuts applicatifs N4 (vue Cluster Services — lus dans N4, pas dans le log Windows)

| Statut | Sens |
|---|---|
| `INITIALIZING` / `LOADING` | démarrage normal |
| `WAITING` | attend que le 1er nœud charge le cache |
| **`ACTIVE`** | **fonctionnement normal (up)** |
| `RECOVERING` | reprise après erreur (ex. crash) |
| `SHUTDOWN` | arrêt propre |
| `INACTIVE` | aucun heartbeat depuis 2 min |
| `DISCONNECTED` | heartbeat présent mais n'atteint pas le Center (normal tant que le Center n'est pas démarré) |

## Bloc `Readiness` prêt à coller dans Navis-Config.json

Les marqueurs Cluster/Center sont documentés ; Bridge/XPS/ECN4 sont des **candidats à relever**
(laissés vides à compléter).

```json
"Readiness": {
  "Defaults": { "LogReadyTimeoutSeconds": 1800 },
  "Components": {
    "Cluster": {
      "LogPath": "C:\\ProgramData\\Navis\\<noeud>\\logs\\navis-apex.log",
      "ReadyPatterns": ["Web tier servlet .*initialized"],
      "ErrorPatterns": ["Failed to start", "Application startup aborted", "split.?brain"]
    },
    "Center": {
      "LogPath": "C:\\ProgramData\\Navis\\<noeud>\\logs\\navis-apex.log",
      "ReadyPatterns": ["Web tier servlet .*initialized"],
      "ErrorPatterns": ["Failed to start", "Application startup aborted"]
    },
    "Standby": {
      "LogPath": "C:\\ProgramData\\Navis\\<noeud>\\logs\\navis-apex.log",
      "ReadyPatterns": [],
      "ErrorPatterns": ["Web tier servlet .*initialized"]
    },
    "ECN4Web": {
      "LogPath": "C:\\ProgramData\\Navis\\ecn4web\\logs\\navis-ecn4web*",
      "ReadyPatterns": ["Web tier servlet .*initialized"]
    },
    "Bridge": {
      "LogPath": "C:\\ProgramData\\Navis\\bridge\\logs\\navis-bridged*",
      "ReadyPatterns": [],
      "ErrorPatterns": ["[Bb]ridge.{0,80}(WAITING|LOADING)"]
    },
    "XPS": {
      "LogPath": "C:\\ProgramData\\Navis\\xps\\log\\xps_*",
      "ReadyPatterns": []
    },
    "ECN4": {
      "LogPath": "C:\\ProgramData\\Navis\\ecn4\\logs\\navis-ecn4*",
      "ReadyPatterns": []
    }
  }
}
```

> Pour Standby, le marqueur « initialized » est volontairement placé en **ErrorPattern** :
> s'il apparaît alors que le primaire est actif, c'est un conflit de rôle, pas un bon démarrage.
> Pour Bridge/XPS/ECN4, compléter `ReadyPatterns` après relevé avec `Find-N4ReadinessPattern.ps1`.
