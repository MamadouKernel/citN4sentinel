# Observabilité : Métriques et Santé (Phase IX)

## 1. Endpoint `/health` (T067)
N4 Sentinel expose un endpoint standard ASP.NET Core Health Checks :
- **URL** : `https://<serveur>/health`
- **Contenu vérifié** :
  - **Base de données (`N4SentinelDbContext`)** : Vérifie que la base SQL Server est joignable et accepte les requêtes (via `AddDbContextCheck`).

Ce point de terminaison est destiné aux load balancers (F5, HAProxy) ou à la supervision externe (PRTG, Datadog) pour vérifier le liveness/readiness de l'application Web.

## 2. Endpoint `/metrics` (T070)
*Note: Cet endpoint nécessite l'intégration d'OpenTelemetry ou prometheus-net, qui est prévue si l'infrastructure cible (ex: Prometheus/Grafana) est mise en place par la DSI.*

Si activé via `app.UseOpenTelemetryPrometheusScrapingEndpoint()`, il exposera :
- Les compteurs HTTP (requêtes/sec, latence).
- Les métriques CLR (GC, ThreadPool).
- Les compteurs d'orchestration N4 Sentinel (workflows actifs, erreurs).

## 3. Configuration Serilog UAT / Seq (T068, T069)
L'environnement UAT utilise une configuration Serilog spécifique (via `appsettings.UAT.json`) :
- **Seq** : Activé optionnellement (port 5341 par défaut) pour centraliser et requêter les journaux structurés.
- **Fichiers** : Rotation journalière (`Logs/n4sentinel-uat-.txt`).
- **Niveau** : `Information` par défaut, avec `Warning` pour les bruits de fond Microsoft/EF Core.
