# N4 Sentinel

Application interne de pilotage, de supervision et de diagnostic de l'écosystème
**Navis N4** de Côte d'Ivoire Terminal.

Référence du cahier des charges : `CIT-CIV-DSI-RFP-0010` — DSI / Équipe Solutions IT et Projets.

## Ce que fait la solution

N4 Sentinel encadre les opérations sur l'écosystème N4 (nœuds Cluster, Center et
Standby, Bridge daemon, XPS, ECN4/ECN4Web, SQL Server, ActiveMQ/KahaDB, dossiers
partagés, interfaces EDI) autour de trois fonctions :

- **Orchestration contrôlée** des séquences d'arrêt, de démarrage et de
  redémarrage, dans l'ordre validé, avec vérification des prérequis à chaque étape ;
- **Supervision** de l'état réel des serveurs, services et composants ;
- **Diagnostic** explicable, appuyé sur les journaux applicatifs et une base
  documentaire interrogeable.

### Le principe qui structure tout le reste

Un service Windows `Running` ne prouve pas qu'un composant N4 est opérationnel.
Le service passe `Running` en quelques secondes ; la JVM N4, elle, met souvent
plusieurs minutes à charger sa configuration, ouvrir la base, rejoindre le
cluster Hazelcast et initialiser son tier web.

Toute attente de démarrage se fait donc en deux temps — statut du service, puis
**marqueur d'initialisation dans le journal applicatif** — et retourne un état à
trois valeurs : `Opérationnel`, `Échec`, ou `À confirmer`. Ce troisième état est
délibéré : la solution n'affirme pas ce qu'elle n'a pas prouvé.

## Structure du dépôt

| Chemin | Contenu |
|---|---|
| `src/N4Sentinel.Domain` | Entités, énumérations, rôles et politiques. Aucune dépendance. |
| `src/N4Sentinel.Infrastructure` | EF Core, Identity, interception d'audit, amorçage de la base. |
| `src/N4Sentinel.Web` | Interface Blazor Server et authentification. |
| `db/` | Scripts de préparation de la base et du compte applicatif. |
| `Corpus_Complet_Support_Navis_N4/` | SOP, playbook de diagnostic et scripts PowerShell d'exploitation. |
| `doc/` | Cahier des charges et guides éditeur — **hors dépôt** (voir `.gitignore`). |

## Prérequis

- .NET SDK 10
- SQL Server (instance par défaut), base `n4sentinel`
- PowerShell 5.1 ou supérieur pour les scripts d'exploitation

## Mise en route

```bash
# 1. Créer la base et le compte applicatif, et renseigner la chaîne de connexion
powershell -ExecutionPolicy Bypass -File db\02_configurer_acces_sql.ps1

# 2. Appliquer le schéma
dotnet ef database update --project src/N4Sentinel.Infrastructure --startup-project src/N4Sentinel.Web

# 3. Lancer l'application
dotnet run --project src/N4Sentinel.Web
```

Le premier administrateur est créé au démarrage à partir de la section
`N4Sentinel:FirstAdmin` de `appsettings.json`. Videz ces deux valeurs une fois
un second administrateur créé.

### Sur les secrets

La chaîne de connexion figure actuellement en clair dans `appsettings.json`, qui
est versionné. C'est un choix assumé pour le poste de développement. **En UAT et
en Production**, surchargez-la sans toucher au fichier :

```bash
setx ConnectionStrings__N4Sentinel "Server=...;User Id=...;Password=..."
```

ou, préférablement, faites tourner l'application sous un compte de service
Windows en `Trusted_Connection=True` : il n'y a alors plus aucun mot de passe à
protéger (SEC-003 du cahier des charges).

## Scripts d'exploitation

`Corpus_Complet_Support_Navis_N4/03 - Scripts PowerShell (SOP-2)/` contient les
séquences d'arrêt et de démarrage, le rolling restart du cluster, le pré-check
d'incident et l'outil de relevé des marqueurs de démarrage. Ils restent
utilisables seuls : l'indisponibilité de N4 Sentinel ne doit pas empêcher
d'appliquer les procédures manuelles d'urgence (SEC-010).

**Ces scripts n'ont pas été validés sur un environnement N4 réel.** Ils ont été
écrits à partir de la documentation éditeur et des procédures internes. À
éprouver hors production avant tout usage.

## Documentation

- [Procédures d'Orchestration (SOP)](docs/SOP/)
  - [SOP-N4-001 : Démarrage et Arrêt complet](docs/SOP/SOP-N4-001.md)
  - [SOP-N4-002 : Bascule Center (Failover)](docs/SOP/SOP-N4-002.md)
- [Architecture Décisions (ADR)](docs/ADR/)
  - [ADR-MFA-001 : Choix du MFA TOTP (SEC-001)](docs/ADR/ADR-MFA-001.md)
- Le journal de décision initial se trouve dans `spec.md`.

## État d'avancement

Sprint 1 en cours : socle applicatif, référentiel technique, authentification et
journal d'audit. Le plan de réalisation détaillé couvre huit itérations
hebdomadaires jusqu'à la livraison.
