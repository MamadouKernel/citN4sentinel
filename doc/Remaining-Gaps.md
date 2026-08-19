# Gaps Restants (Remaining Gaps) - N4Sentinel

Ce document recense les exigences du cahier des charges qui n'ont pas pu être totalement validées par le code seul, car elles dépendent de ressources externes, de matériel réseau, ou de décisions de gouvernance hors du périmètre de l'application elle-même. 

À l'issue de la phase de remédiation du 19/08/2026, l'ensemble des développements applicatifs attendus (soit 100% des exigences réalisables) ont été menés à bien. Les exigences listées ci-dessous constituent donc le reliquat nécessitant une intervention extérieure pour être clôturées :

## 1. Intégrations avec le Système d'Information (SI)
| Exigence | Prérequis manquant | Contournement actuel |
|---|---|---|
| **Intégration LDAP / Active Directory** | Un tenant Azure AD réel configuré + les identifiants d'application | Scaffolding d'authentification prévu (`AzureAdAuthProvider.cs`) mais désactivé par défaut. Utilisation d'authentification locale pour l'instant. |
| **Intégration Ticketing (ITSM)** | Accès à l'API du système de ticketing réel de CIT | L'application dispose d'un connecteur générique désactivé et propose un champ texte libre pour référencer le ticket manuellement en attendant. |

## 2. Infrastructure et Réseau
| Exigence | Prérequis manquant | Contournement actuel |
|---|---|---|
| **Passerelle réseau sécurisée** | Décision d'architecture et de mise en place d'un matériel réseau ou WAF par l'équipe infrastructure | La documentation de déploiement recommande formellement l'utilisation d'un Reverse Proxy (voir `doc/Guide-deploiement.md`). |

## 3. Exigences Non Fonctionnelles (NFR) lièes à la Charge
| Exigence | Prérequis manquant | Contournement actuel |
|---|---|---|
| **NFR-003 : Performance sous charge** | Un environnement de test de charge réel (Injecteur de logs, bots de simulation) | L'architecture est pensée pour l'instrumentation et la performance, mais aucune mesure valide ne peut être produite hors d'un vrai cluster de test de charge. |
| **NFR-004 : Scalabilité réelle** | Plusieurs dizaines d'environnements N4 réels et actifs simultanément à superviser | La conception applicative est multi-environnement et prête pour l'échelle, mais la preuve formelle est impossible sans ces environnements. |

---
**Conclusion** : Le code est prêt à accueillir ces intégrations. La prochaine étape consiste en une réunion avec la DSI pour provisionner les environnements manquants (AD, ITSM) et qualifier les performances sous une charge réelle de production.
