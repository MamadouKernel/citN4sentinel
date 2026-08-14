# N4 Sentinel — Guide de déploiement

**CIT-CIV-DSI-RFP-0010** · Déploiement sur un site

Ce guide couvre l'installation de N4 Sentinel sur un serveur, sa mise en
service, sa sauvegarde et son retour arrière. Il s'adresse à un administrateur
système ; il ne suppose aucune connaissance de N4 Sentinel.

---

## 1. Ce que vous installez, et ce que vous n'installez pas

N4 Sentinel est **une application unique par site**. Elle ne s'installe pas par
environnement : un seul serveur pilote la Production, l'UAT et tout autre
environnement, chacun étant enregistré depuis l'interface après l'installation.

**Elle ne s'installe sur aucun serveur N4.** Elle les interroge à distance par
WinRM. Aucun agent, aucun composant, aucune modification n'est déposée sur les
serveurs Navis.

**Son indisponibilité n'empêche aucune opération.** Les procédures manuelles
d'exploitation restent applicables telles quelles. N4 Sentinel accélère et
trace ; il n'est pas un point de passage obligé.

---

## 2. Prérequis

### Serveur applicatif

| | |
|---|---|
| Système | Windows Server 2019 ou ultérieur |
| Exécution | Runtime ASP.NET Core 10 — *Hosting Bundle* |
| Mémoire | 4 Go, 8 Go recommandés |
| Disque | 20 Go, dont l'espace pour les journaux applicatifs |
| Réseau | Accès sortant WinRM (5985/5986) vers les serveurs N4, accès à SQL Server (1433) |

### Base de données

SQL Server 2019 ou ultérieur. Une base dédiée, **collation `French_CI_AS`**,
avec *Read Committed Snapshot* activé.

Les scripts du dossier `base-de-donnees\` du paquet la préparent.

### Compte de service

Un compte de domaine, qui doit avoir :

- le droit de **se connecter comme service** sur le serveur applicatif ;
- l'accès en écriture à la base N4 Sentinel ;
- l'appartenance au groupe **Remote Management Users** — ou administrateur
  local — sur chaque serveur N4 à superviser.

> **Ce compte n'a pas besoin d'être administrateur du domaine.** Si on vous le
> demande, la demande est excessive : refusez-la et remontez ce paragraphe.

---

## 3. Installation

### 3.1 Constituer le paquet

Sur un poste disposant du SDK .NET 10 et de Node.js :

```powershell
.\deploiement\Publier-N4Sentinel.ps1 -Destination D:\Paquets -Version 1.0.0
```

Le paquet obtenu **ne contient aucun secret** : `appsettings.Production.json`
est un gabarit dont le mot de passe est à remplacer.

### 3.2 Préparer la base

Sur l'instance SQL Server, avec un compte administrateur :

```sql
CREATE DATABASE [n4sentinel] COLLATE French_CI_AS;
GO
ALTER DATABASE [n4sentinel] SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;
GO
```

Puis créez le compte applicatif et donnez-lui `db_owner` **sur cette base
seulement**. Il applique les migrations au démarrage, ce qui exige de pouvoir
créer des tables — mais uniquement là.

### 3.3 Renseigner la configuration

Dans `application\appsettings.Production.json` du paquet, **avant** d'installer :

- `ConnectionStrings:N4Sentinel` — serveur, base, compte, mot de passe ;
- `N4Sentinel:DataProtection:KeyPath` — dossier du trousseau de clés.

> Si le compte de service accède à SQL Server par authentification Windows,
> préférez `Trusted_Connection=True` : il n'y a alors **aucun mot de passe** à
> écrire dans ce fichier, et donc aucun à protéger.

### 3.4 Installer

Sur le serveur cible, **console élevée** :

```powershell
.\Installer-N4Sentinel.ps1 -Source .\application -Destination C:\N4Sentinel -Port 8443
```

Le script refuse d'installer une configuration incomplète. Un service
enregistré qui ne démarre pas est pire qu'une installation interrompue : il
donne l'illusion que le travail est fait.

### 3.5 Désigner le compte de service

`services.msc` → propriétés de **N4Sentinel** → onglet **Connexion** → compte
du domaine.

Le script ne le fait pas volontairement : un mot de passe passé en paramètre se
retrouve dans l'historique PowerShell.

### 3.6 Premier démarrage

```powershell
Start-Service N4Sentinel
```

Puis `http://<serveur>:8443`.

Le premier écran crée l'administrateur initial. **Aucun compte n'existe avant** :
il n'y a pas de mot de passe par défaut qu'on oublierait de changer.

---

## 4. Mise en service d'un environnement

Une fois connecté, le parcours guidé enchaîne huit étapes. L'ordre compte :
chacune s'appuie sur la précédente.

1. **Créer l'environnement** — code, type, fuseau horaire.
2. **Renseigner la source de temps attendue** — permet de répondre OK/KO à la
   conformité NTP des nœuds. Sans elle, l'application répondra « à confirmer ».
3. **Déclarer les serveurs** — nom d'hôte, port WinRM, compte technique.
4. **Tester la connexion** — serveur par serveur, en lecture seule.
5. **Déclarer les composants** — nom logique, rôle N4, service Windows, ordre.
6. **Relever les marqueurs de démarrage** — l'application lit le journal réel
   et propose les lignes candidates.
7. **Déclarer les dépendances** — c'est ce graphe qui fera refuser une séquence
   impossible.
8. **Générer et relire les séquences N4** — livrées en brouillon, à valider.

> **L'étape 6 est celle qu'on est tenté de sauter.** Sans marqueur, un
> composant démarré reste « à confirmer » : le service Windows tourne, mais
> l'application refuse d'affirmer que le composant est opérationnel. C'est
> voulu — et c'est aussi ce qui rend l'outil beaucoup moins utile.

---

## 5. Sauvegarde

### Ce qu'il faut sauvegarder

| Élément | Pourquoi |
|---|---|
| Base `n4sentinel` | Référentiel, historique, diagnostics, documentation |
| Dossier du trousseau de clés | **Déchiffre les mots de passe des comptes techniques** |

**La base seule ne suffit pas.** Restaurée sans le trousseau, elle redonne des
comptes techniques dont les secrets sont illisibles — et le constat se fait au
pire moment, quand on tente de redémarrer un environnement après un sinistre.

L'écran **Administration → Sauvegarde** produit les deux ensemble, avec une
notice de restauration, et vérifie qu'une sauvegarde est réellement lisible par
SQL Server.

### La limite à connaître avant d'en avoir besoin

Le trousseau est chiffré par **DPAPI à l'échelle de la machine**. Copié sur un
autre serveur, il est inutilisable.

Une reprise sur une machine neuve impose donc de **ressaisir les mots de passe
des comptes techniques**, quelle que soit la qualité de la sauvegarde. Ce n'est
pas un défaut : c'est la contrepartie du choix de ne stocker aucune clé en
clair.

**Prévoyez cette ressaisie dans votre procédure de reprise** plutôt que de la
découvrir le jour où vous l'exécutez. Conservez la liste des comptes techniques
à ressaisir — leurs références, pas leurs mots de passe — dans votre coffre.

### Fréquence proposée

- **Base** : sauvegarde complète quotidienne, journal toutes les heures.
- **Trousseau** : à chaque modification d'un compte technique, et au minimum
  une fois par semaine. Il change rarement, mais sa perte est irréversible.

---

## 6. Mise à jour

```powershell
Stop-Service N4Sentinel
# Sauvegarder la base ET le trousseau avant toute chose
.\Installer-N4Sentinel.ps1 -Source .\application -Destination C:\N4Sentinel -ForcerRemplacement
Start-Service N4Sentinel
```

Le script **préserve le trousseau existant** : il le met de côté avant la copie
et le remet ensuite. L'écraser rendrait tous les comptes techniques illisibles,
sans message d'erreur.

Les migrations de schéma s'appliquent au démarrage. Consultez le journal
applicatif pour vérifier qu'elles ont abouti.

---

## 7. Retour arrière

Si une version se révèle défectueuse :

1. **Arrêter le service.**
   ```powershell
   Stop-Service N4Sentinel
   ```

2. **Restaurer la base** dans son état antérieur à la mise à jour.
   ```sql
   RESTORE DATABASE [n4sentinel] FROM DISK = 'D:\Sauvegardes\...\n4sentinel.bak'
     WITH REPLACE, RECOVERY;
   ```

3. **Réinstaller la version précédente** depuis son paquet, avec
   `-ForcerRemplacement`.

4. **Redémarrer et vérifier** que les comptes techniques ne portent pas l'état
   « secret illisible » sur l'écran des comptes.

> **Ne restaurez jamais la base sans réinstaller la version correspondante.**
> Une base restaurée à un schéma antérieur, lue par une version plus récente,
> ne se plaindra pas immédiatement — elle échouera plus tard, sur une colonne
> absente, dans un écran que personne ne consulte tous les jours.

**Le retour arrière n'a aucun effet sur l'écosystème N4.** N4 Sentinel ne
modifie rien de persistant sur les serveurs Navis : il démarre et arrête des
services. Revenir à une version antérieure ne défait aucune opération déjà
exécutée — l'état des composants reste ce qu'il est, et doit être constaté.

---

## 8. Mode dégradé

Si N4 Sentinel est indisponible :

- **Les procédures manuelles restent valides.** Les scripts PowerShell du
  corpus d'exploitation (`Navis-N4-Scripts`) fonctionnent indépendamment.
- **Aucun composant N4 n'est affecté.** L'application ne laisse rien tourner
  sur les serveurs Navis.
- **Une opération en cours au moment de la panne** ne reprend pas seule : au
  redémarrage, elle passe en « réconciliation requise » et attend qu'un
  opérateur constate l'état réel. C'est voulu — reprendre sur une base fausse
  est plus dangereux que s'arrêter.

Vérifiez ce mode dégradé **avant** d'en avoir besoin : arrêtez le service et
confirmez que votre équipe sait exploiter N4 sans lui.

---

## 9. Dépannage

| Symptôme | Cause la plus fréquente |
|---|---|
| Le service démarre puis s'arrête | Chaîne de connexion invalide. Consultez `journaux\` : le message est explicite. |
| « Accès refusé » sur un serveur N4 | Le compte de service n'est pas dans **Remote Management Users** du serveur cible. |
| Un composant reste « à confirmer » | Aucun marqueur de démarrage configuré. Écran **Marqueurs**. |
| NTP « à confirmer » partout | Source de temps attendue non renseignée sur la fiche de l'environnement. |
| Mises à jour Windows non relevées | L'objet COM Windows Update ne franchit pas WinRM sans délégation. Relevez depuis le serveur, ou configurez CredSSP. |
| Interface sans mise en forme | Feuille de style non compilée dans le paquet. Reconstituez le paquet avec Node.js installé. |
| « Secret illisible » sur un compte | Le trousseau a été perdu ou remplacé. Ressaisissez le mot de passe. |

Le journal applicatif se trouve dans `C:\N4Sentinel\journaux\`, un fichier par
jour.

---

## 10. Sécurité

- **Exposez l'application derrière HTTPS.** Le script ouvre un port HTTP en
  clair sur le profil Domaine ; en production, placez un reverse proxy avec
  certificat, ou configurez Kestrel avec un certificat serveur.
- **Le trousseau ne doit être lisible que par le compte de service.** Vérifiez
  les droits du dossier après installation.
- **Aucun mot de passe n'apparaît dans les journaux ni dans les exports** :
  l'application les masque avant enregistrement, pas avant affichage.
- **Le second facteur (TOTP)** s'active par utilisateur depuis son profil.
  Rendez-le obligatoire pour les comptes habilités à exécuter en Production.

---

*N4 Sentinel — Côte d'Ivoire Terminal · Direction des Systèmes d'Information*
