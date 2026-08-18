# ADR-MFA-001 — Mécanisme d'authentification multifacteur

**Statut** : 🔴 DÉCISION DSI REQUISE  
**Date** : 18/08/2026  
**Référence CdC** : SEC-001

---

## 1. Contexte

N4 Sentinel est un outil de pilotage de l'infrastructure portuaire N4 Navis.
Certaines actions (lancement d'une opération, approbation d'un arrêt en Production)
peuvent avoir un impact immédiat sur les opérations du terminal.

Le cahier des charges exige un mécanisme de second facteur d'authentification
pour les comptes habilités à ces actions.

---

## 2. Exigence du cahier des charges

**SEC-001 (texte exact)** :
> « Authentification par double facteur (2FA) obligatoire pour les profils
> OperateurN4, AdministrateurN4 et Validateur en environnement de Production.
> Le second facteur doit être un code envoyé par e-mail ou une application
> d'authentification (TOTP). »

Le cahier des charges **autorise les deux mécanismes** (e-mail ou TOTP).

---

## 3. Implémentation actuelle

**Ce qui existe** :
- ASP.NET Core Identity TOTP est implémenté et fonctionnel
- Flux complet : enrôlement via QR code (`/Account/Manage/EnableAuthenticator`),
  vérification au login (`/Account/LoginWith2fa`), codes de récupération
- Le second facteur est configurable via `N4Sentinel:Securite:SecondFacteurExigePourAction`
  (défaut : `false` — désactivé délibérément pour permettre l'enrôlement initial)
- `SmtpEmailSender` est implémenté et fonctionnel pour les notifications

**Ce qui manque pour l'option e-mail** :
- La génération et l'envoi d'un code à usage unique par e-mail au login
  (le flux Identity par défaut utilise TOTP, pas un OTP e-mail)
- Une table de codes temporaires (expiration, usage unique, rotation)

---

## 4. Options disponibles

### Option A — TOTP (implémentation actuelle)
**Avantages** :
- Déjà implémenté, fonctionnel et testé
- Indépendant d'un serveur SMTP (pas de dépendance à la messagerie pour se connecter)
- Résistant au phishing (code ne transite pas par e-mail)
- Conforme à l'exigence CdC (le texte autorise TOTP)

**Inconvénients** :
- Nécessite une application d'authentification (Google Authenticator, Microsoft Authenticator, etc.)
- Enrôlement à gérer pour chaque utilisateur habilité

**Effort de mise en production** : aucun code supplémentaire — activer `SecondFacteurExigePourAction = true` après enrôlement de tous les comptes.

---

### Option B — OTP par e-mail (non implémenté)
**Avantages** :
- Aucune application supplémentaire pour l'utilisateur
- Familier pour la plupart des utilisateurs

**Inconvénients** :
- Dépend de la disponibilité du serveur SMTP au moment de la connexion
- Si le serveur SMTP est en panne, plus personne ne peut se connecter
- Code transmis par e-mail : susceptible d'interception si la messagerie est compromise
- Délai variable selon la messagerie (filtres anti-spam, latence)
- À implémenter : ~2 jours de développement + migration pour la table OTP

**Effort** : développement + migration EF + tests + configuration SMTP garantie.

---

### Option C — Hybride (TOTP par défaut, e-mail en fallback)
Plus complexe, non recommandé pour une V1.

---

## 5. Risques

| Risque | Option A (TOTP) | Option B (e-mail) |
|---|---|---|
| Connexion impossible si SMTP en panne | ✅ Aucun | 🔴 Critique |
| Code interceptable | ✅ Non (jamais transmis) | ⚠️ Si messagerie compromise |
| Enrôlement des utilisateurs | ⚠️ À organiser | ✅ Aucun |
| Délai de mise en production | ✅ Immédiat | ⚠️ ~2 jours de dev |

---

## 6. Recommandation technique

**Retenir l'Option A (TOTP)**.

Justification :
1. Conforme au texte du cahier des charges
2. Déjà implémenté et testé
3. Meilleure résistance aux pannes et au phishing
4. Effort nul pour la mise en production

---

## 7. Décision requise de la DSI CIT

```
La DSI CIT doit choisir l'une des options suivantes :

[ ] Option A — TOTP (application d'authentification)
    → Activer SecondFacteurExigePourAction = true après enrôlement
    → Aucun développement supplémentaire requis

[ ] Option B — OTP par e-mail
    → Développement supplémentaire requis (~2 jours)
    → Configuration SMTP garantie requise en Production

[ ] Option C — Reporter la décision et déployer en UAT sans second facteur obligatoire
    → SecondFacteurExigePourAction = false pour la recette UAT
    → Décision avant mise en Production

Signataire DSI :  ________________________________

Date :  _______________
```

---

## 8. Impact sur la roadmap

Tant que cette décision n'est pas prise et signée, **SEC-001 est classé** :

> ⛔ **Bloqué par décision externe (DSI)**

Le code TOTP est livré et fonctionnel. L'activation ne nécessite qu'un changement
de configuration, pas de développement.
