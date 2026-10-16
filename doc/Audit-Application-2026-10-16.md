# Audit de l'application N4Sentinel

**Date :** 2026-10-16 · **Branche :** main · **Périmètre :** code source, configuration, sécurité, build, tests, maintenabilité.

## Verdict global

Codebase **mature et saine**. .NET 10, Blazor Server, architecture en couches Domain / Infrastructure / Web propre. ~37 k lignes C# applicatives (hors migrations EF), **633 tests (631 réussis, 0 échec, 2 ignorés — WinRM réel)**, build **sans aucun avertissement**. La sécurité a déjà été auditée en interne (références SEC-A2…A9, SEC-001…008) et le résultat est solide. Les points ci-dessous sont surtout de la **maintenabilité et de l'outillage**, pas des défauts bloquants.

---

## Ce qui est bien fait (à conserver)

- **Gestion des secrets** : aucun secret en dur (scan vierge). `appsettings.Production.json` est un gabarit vide, secrets via variables d'environnement / DPAPI. Chaînes de connexion en authentification Windows par défaut.
- **Sécurité HTTP** (`Program.cs`) : CSP avec nonce par requête, `X-Content-Type-Options`, `Referrer-Policy`, `Permissions-Policy`, HSTS, `AddServerHeader=false`, rate limiting par IP, HTTPS redirect, antiforgery.
- **Identité** : mot de passe 12 car. + complexité, verrouillage 5 tentatives / 15 min, email unique, MFA TOTP, historique de mots de passe, compte confirmé requis.
- **Webhook ticketing entrant** : `AllowAnonymous` mais **fail-closed** par HMAC SHA256 time-safe — correctement conçu et documenté.
- **DataProtection** : clés persistées sur disque + DPAPI machine, avec avertissement sauvegarde. `/metrics` et `/health/detail` authentifiés, `/health` anonyme minimal.
- **Code propre** : nullable activé partout, 1 seul TODO, 0 `catch {}` en C# (les `} catch { }` trouvés sont dans des scripts PowerShell embarqués — best-effort télémétrie, acceptable), 0 appel bloquant `.Result`/`.Wait()` (faux positifs sur `ResultingExecutionId`).
- **Documentation** riche : ADR, MERISE, guides de déploiement, PV de livraison, plans de remédiation d'audits antérieurs.

---

## Points d'attention

### MEDIUM — Outillage / cohérence dépôt

1. **Pas de CI** (`.github/workflows` absent). Build + 586 tests ne tournent qu'en local. → Ajouter un workflow `dotnet build` + `dotnet test` sur push/PR. **Plus fort levier de l'audit.**
2. **Pas de `Directory.Packages.props`** : versions de paquets répétées dans chaque `.csproj` (EF Core 10.0.11 x3, etc.). Risque de dérive de versions. → Central Package Management.
3. **Pas de `Directory.Build.props` ni `.editorconfig`** : `Nullable`/`TargetFramework` dupliqués, pas de `TreatWarningsAsErrors`. Comme le build est déjà à 0 warning, activer `TreatWarningsAsErrors` verrouille cet acquis à coût nul.
4. **Deux historiques de migrations** pour le même modèle (62 migrations SqlServer vs 4 SQLite). Les snapshots concordent aujourd'hui, mais c'est un risque de **dérive de schéma inter-provider** à surveiller. → Un test qui compare les deux `ModelSnapshot`, ou un script de vérification au CI.

### MEDIUM — Taille de fichiers (plafond interne 800 lignes)

12 fichiers dépassent le seuil. Les plus gros valent une extraction :

| Fichier | Lignes |
|---|---|
| `Procedures/SopService.cs` | 1420 |
| `Persistence/N4SentinelDbContext.cs` | 1329 |
| `Diagnostic/LogAnalysisService.cs` | 1314 |
| `Orchestration/WorkflowService.cs` | 1102 |
| `Connectors/PowerShellConnector.cs` | 1082 |
| `Orchestration/PreflightService.cs` | 1049 |
| `Diagnostic/DiagnosticSessionService.cs` | 1027 |

Le `DbContext` à 1329 lignes est normal pour un modèle MERISE large (configuration Fluent) — acceptable. `SopService` et `LogAnalysisService` sont les meilleurs candidats à découpage par responsabilité.

### LOW

5. **Travail non commité** : 6 fichiers modifiés + 2 non suivis (`Deployer-N4Sentinel-Sqlite.ps1`, un `.xlsx`). À committer ou nettoyer pour ne pas perdre le contexte.
6. **Fins de ligne** : avertissements LF→CRLF au `git diff`. Un `.gitattributes` (`*.cs text`, `*.ps1 text`) fige la normalisation.
7. **`TrustServerCertificate=True`** en prod par défaut (déjà documenté dans le commentaire SEC-005) — à repasser à `False` dès qu'un certificat de confiance est posé sur le serveur SQL.

---

## Recommandations prioritaires

1. Ajouter la **CI GitHub Actions** (build + tests) — verrouille toute la qualité déjà en place.
2. `Directory.Packages.props` + `Directory.Build.props` avec `TreatWarningsAsErrors` + `.editorconfig`.
3. Test de **parité des snapshots** SqlServer/SQLite.
4. Découper `SopService` et `LogAnalysisService`.

Aucun défaut CRITICAL ou HIGH. L'application est en bon état de livraison.
