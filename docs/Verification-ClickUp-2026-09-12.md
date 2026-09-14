# Vérification locale et suivi ClickUp — 12 septembre 2026

## Résultats vérifiés

- Connexion Windows à `localhost`, base `DbN4Sentinel`, vérifiée hors du bac à sable.
- Base initialement vide ; après migrations : 47 tables.
- Profil Development configuré pour cette base ; aucun changement des profils UAT/Production.
- Application démarrée sur http://localhost:5161 : `/health` = HTTP 200 Healthy ; `/Account/Login` = HTTP 200.
- Suite complète : 633 tests, 631 réussis, 0 échec, 2 non exécutés. Preuve : `tests/N4Sentinel.Tests/TestResults/verification-connexion.trx`.
- Non exécutés : `ConnecteurTests.Un_service_est_interroge_via_WinRM` et `ConnecteurTests.Le_chemin_WinRM_fonctionne_de_bout_en_bout`.

La connexion intégrée échoue dans le bac à sable (SSPI). Elle fonctionne sous l'identité Windows de l'utilisateur hors du bac à sable. L'échec antérieur ne démontre donc pas une incompatibilité de chiffrement du poste.

## Écarts confirmés par lecture du code

| Tâches concernées | Constat | Mise à jour ClickUp à prévoir |
|---|---|---|
| PB-24 / S3-08 — Azure AD | `AzureAdAuthProvider.AuthenticateSsoTokenAsync` retourne toujours null. Aucun raccordement OIDC réel trouvé dans Program.cs. | Implémentation incomplète ; pas seulement des identifiants manquants. Ne pas clôturer. |
| PB-25 / S3-09 — Ticketing | Aucun connecteur ticketing identifié dans le code source consulté. | À développer et à valider avec le contrat API du système CIT. |
| PB-26 / S3-09 — JMX | Aucune collecte JMX identifiée ; les mentions dans DatabaseSeeder sont du contenu documentaire. | À développer et à valider sur un endpoint N4 autorisé. |
| PB-22 / S3-06 — Notifications | `INotificationSender` expose un envoi de courriel ; cela ne prouve pas plusieurs canaux. | Couverture multicanal non démontrée. |
| PB-28 / S4-03 — Recette officielle | Tests locaux verts ; aucune preuve de recette des 23 AC sur N4 UAT obtenue pendant cette vérification. | Conserver ouvert ; joindre les résultats locaux comme preuve partielle. |

## Limites de l'audit

Les intitulés des 64 tâches ont été récupérés auparavant. La récupération de leurs descriptions a été partielle : plusieurs réponses du connecteur sont des erreurs. Le fichier temporaire `tmp/clickup-audit-snapshot.json` n'est pas une matrice complète.

ClickUp a ensuite répondu `RATE_LIMIT_EXCEEDED`, avec environ 688 minutes avant réessai. Aucune mise à jour ClickUp n'a été effectuée pendant cette vérification. Une reprise devra relire les descriptions avant de les modifier, afin de préserver le contenu existant.

Les tests réussis ne constituent ni une preuve de couverture intégrale du cahier des charges, ni une validation de tous les critères détaillés ClickUp. Les affirmations antérieures de conformité quasi complète étaient trop larges.

## Suite nécessaire

### Précision utilisateur et mise à jour à reporter dans ClickUp

Le ticketing CIT est **ServiceNow personnalisé**, confirmé par l'utilisateur. La documentation de cette personnalisation n'est pas disponible. Ne pas supposer les tables, champs obligatoires, règles de transition, droits ou endpoints de l'instance.

Texte à ajouter à PB-25 (`123tcvwctu6`) et au volet ticketing de S3-09 (`123tcvwctt4`), en préservant leurs descriptions existantes :

> Cible confirmée : ServiceNow personnalisé. Documentation de personnalisation indisponible à ce jour. Connecteur non identifié dans le code audité ; développement et validation restent à réaliser. Prérequis : identifier l'instance de test, les tables et champs autorisés, les états et transitions, l'authentification et les droits du compte d'intégration. Le périmètre bidirectionnel doit être vérifié sur cette instance avant clôture.

Nouvelle tentative de lecture de PB-25 : ClickUp répond toujours `RATE_LIMIT_EXCEEDED`, avec 684 minutes avant réessai (environ 11 h 24). Aucun changement distant effectué. Cette précision est enregistrée localement en attente de la disponibilité du connecteur.

1. Reprendre la lecture des critères détaillés des tâches quand ClickUp redevient accessible ; établir la matrice exigences → code → tests → preuve de recette.
2. Développer les intégrations manquantes selon les contrats CIT : tenant et mappage de rôles Entra ID, produit/API ticketing, protocole et endpoints JMX.
3. Exécuter les deux tests WinRM et la recette N4 UAT ; vérifier les parcours authentifiés au navigateur.
4. Mettre ClickUp à jour avec les preuves et ne clôturer que les tâches dont tous les critères sont vérifiés.
