# Déploiement N4 Sentinel — SQLite et service Windows

**Sans IIS, sans SQL Server.** Un exécutable, un fichier de base, un service
Windows.

Ce mode vise la VM sur laquelle on ne veut rien installer d'autre. Ce n'est pas
un mode dégradé : transactions, index uniques et concurrence optimiste sont
tenus par le moteur, comme sur SQL Server. Les différences réelles sont au
chapitre F.

Le document suit l'ordre des machines : ce qui se fait sur le poste de
développement, puis sur la VM, puis en exploitation courante.

| Chapitre | Où | Quoi |
|---|---|---|
| **A** | Poste de développement | Produire le paquet |
| **B** | Poste de développement | Le transférer |
| **C** | VM — avant l'application | Préparer la machine |
| **D** | VM — installation | Installer et configurer |
| **E** | VM — mise en service | Démarrer et **prouver** |
| **F** | — | Ce qui diffère de SQL Server |
| **G** | VM — exploitation | Sauvegarder, mettre à jour, dépanner |

---

# A. Sur le poste de développement — produire le paquet

## A.1 Ce qu'il faut sur ce poste

| | |
|---|---|
| SDK .NET | 10.0 |
| Node.js | pour compiler la feuille de style Tailwind |
| Accès au dépôt | `citN4sentinel` |

Ces outils restent **sur le poste de développement**. Rien de tout cela n'est
installé sur la VM.

## A.2 Vérifier que ce qu'on va livrer est sain

Avant de produire un paquet, deux contrôles. Ils prennent quelques minutes et
évitent de découvrir un problème sur la machine cible.

```powershell
dotnet build N4Sentinel.slnx -c Release
```

```powershell
dotnet test tests\N4Sentinel.Tests\N4Sentinel.Tests.csproj
```

La suite compte 567 tests. Certains sont ignorés si SQL Server n'est pas
disponible localement — c'est normal et sans incidence sur un déploiement
SQLite.

## A.3 Produire le paquet

```powershell
.\deploiement\Publier-N4Sentinel.ps1 -Destination C:\Paquets
```

Le paramètre `-Destination` est **obligatoire**. Une version peut être imposée
avec `-Version 2026.08.20` ; par défaut c'est la date du jour.

Le script compile la feuille de style, publie l'application, puis assemble :

```
C:\Paquets\N4Sentinel-2026.08.21\
├── application\              l'application publiée
├── base-de-donnees\          scripts SQL (inutiles en mode SQLite)
├── documentation\            guide de déploiement
├── empreintes.txt            SHA-256 de chaque fichier livré
├── Installer-N4Sentinel.ps1
└── Verifier-N4Sentinel.ps1
```

`empreintes.txt` sert au contrôle après transfert (chapitre B) : une copie
interrompue ou altérée s'y voit, alors qu'un simple décompte de fichiers ne
la montrerait pas.

**Le paquet ne contient aucun secret.** Les gabarits de configuration sont
livrés avec des valeurs vides, à renseigner sur la machine cible.

## A.4 Si la VM n'a pas le runtime ASP.NET Core

Deux options. Choisissez **ici**, sur le poste de développement, et non une
fois devant la machine cible.

**Option 1 — installer le runtime sur la VM** (chapitre C.2). Paquet léger,
mais une dépendance de plus à faire approuver et à maintenir.

**Option 2 — publier en autonome.** Le runtime voyage dans le paquet :

```powershell
.\deploiement\Publier-N4Sentinel.ps1 -Destination C:\Paquets -Autonome
```

La VM n'a alors **rien** à installer. Sur un réseau d'exploitation isolé,
c'est presque toujours le bon compromis : faire approuver l'installation d'un
runtime sur un serveur de production prend plus de temps que de copier le
paquet.

Le commutateur `-Autonome` passe par le même script que le mode normal — donc
avec la feuille de style compilée, les gabarits de configuration, les deux
scripts et le fichier d'empreintes. Une publication `dotnet publish` lancée à
la main sauterait tout cela.

**Poids mesuré le 21/08/2026 : 217 Mo**, dont 12 Mo de ressources web.

**Contrepartie à connaître.** Les correctifs de sécurité du runtime ne
viennent plus par Windows Update : ils sont figés dans le paquet. Une mise à
jour du runtime impose de republier et de redéployer. En échange, vous savez
exactement quelle version tourne — ce qu'un runtime partagé ne garantit pas.

---

# B. Sur le poste de développement — transférer

Copiez le dossier `N4Sentinel-<version>` complet vers la VM, par le moyen
qu'autorise votre exploitation : partage réseau, RDP avec redirection de
lecteur, support amovible.

**Copiez le dossier entier**, pas seulement `application` : les deux scripts et
la documentation en font partie.

Contrôlez l'arrivée avant d'aller plus loin — une copie interrompue produit une
application qui démarre puis échoue de façon incompréhensible, et le lien avec
le transfert ne saute pas aux yeux.

Le paquet embarque l'empreinte SHA-256 de chaque fichier. Sur la VM :

```powershell
cd C:\Depot\N4Sentinel-2026.08.21\application
```

```powershell
Get-Content ..\empreintes.txt | ForEach-Object { $h,$f = $_ -split '\s+',2; if (Test-Path $f) { if ((Get-FileHash $f -Algorithm SHA256).Hash -ne $h) { "ALTERE : $f" } } else { "MANQUANT : $f" } }
```

Aucune sortie signifie que tout est arrivé intact.

---

# C. Sur la VM — préparer la machine

## C.1 Ce qu'il faut

| | |
|---|---|
| Windows Server | 2019 ou ultérieur |
| Espace disque | 2 Go, plus la croissance de la base |
| Un port TCP libre | **8443** par défaut |
| Droits | administrateur local, pour installer le service |

**Rien d'autre.** Pas de SQL Server, pas de rôle IIS.

> Le port par défaut est 8443, qui évoque conventionnellement HTTPS. Or le
> service écoute en **HTTP simple** (voir « Ce que ce document ne couvre pas »).
> Si cette convention risque d'induire en erreur chez vous, imposez un autre
> port avec `-Port`.

## C.2 Le runtime, si vous avez choisi l'option 1

Installez « ASP.NET Core Runtime 10.0 — Hosting Bundle » ou le runtime seul.
Puis vérifiez :

```powershell
dotnet --list-runtimes
```

`Microsoft.AspNetCore.App 10.0.x` doit apparaître. Si vous avez publié en
autonome (A.4, option 2), passez cette étape.

## C.3 Le compte de service

Faites-le créer par l'annuaire **avant** l'installation. Un compte de domaine,
pas LocalSystem.

Il a besoin de trois choses, et de rien de plus :

1. **Écriture** sur le dossier de données (créé en C.4) ;
2. **WinRM** vers les serveurs N4 à superviser ;
3. **Ouverture de session en tant que service** sur cette VM.

Il n'a besoin d'**aucun droit sur un SQL Server**, puisqu'il n'y en a pas.

Le droit « ouverture de session en tant que service » s'accorde par stratégie
locale (`secpol.msc` → Stratégies locales → Attribution des droits utilisateur)
ou par GPO. Sans lui, le service refusera de démarrer avec une erreur
d'ouverture de session, qui ne dit pas clairement ce qui manque.

## C.4 Le dossier de données

**Hors du dossier d'application.** C'est le point où l'on se trompe : une mise
à jour remplace le contenu de `C:\N4Sentinel`, et une base qui y résiderait
serait écrasée.

```powershell
New-Item -ItemType Directory -Force C:\ProgramData\N4Sentinel
```

Puis donnez-en le contrôle au compte de service. **`DOMAINE\compte` ci-dessous
est un exemple à remplacer** — recopié tel quel, Windows répond « No mapping
between account names and security IDs was done », ce qui veut simplement dire
que le compte n'existe pas.

Le domaine attendu est le nom **NetBIOS** (`CIT`), pas le FQDN (`cit.local`) :

```powershell
icacls C:\ProgramData\N4Sentinel /grant "DOMAINE\compte:(OI)(CI)M"
```

Pour retrouver le nom exact d'un compte existant :

```powershell
Get-ADUser -Filter "SamAccountName -like '*n4*'" | Select-Object SamAccountName, Name
```

> **Si le compte de service n'existe pas encore, passez cette étape.**
> L'installeur laisse le service sous `LocalSystem`, qui a déjà tous les droits
> sur `C:\ProgramData` : l'application s'installe, démarre et fonctionne.
>
> Ce que `LocalSystem` ne permettra PAS : se connecter aux serveurs N4 par
> WinRM. La supervision et l'orchestration des composants Navis échoueront sur
> des refus d'accès — tout le reste fonctionnera. C'est un compromis
> acceptable pour éprouver le déploiement, à condition de le savoir et de
> revenir désigner le compte de domaine avant la mise en service réelle.

Ce dossier accueillera deux choses, la base et le trousseau de clés. Les deux
sont à sauvegarder ensemble (chapitre G.1).

---

# D. Sur la VM — installer et configurer

## D.1 Installer

Depuis le dossier du paquet, en console **administrateur** :

```powershell
.\Installer-N4Sentinel.ps1 -Source .\application -Destination C:\N4Sentinel -Port 8443
```

Le script copie l'application, crée le service `N4Sentinel`, le règle en
démarrage automatique, définit sa politique de redémarrage après incident
(60 s, 60 s, puis 120 s) et ouvre le pare-feu sur le profil Domaine.

Pour faire cohabiter deux instances sur une même VM — une UAT et une
Production —, passez `-NomService N4Sentinel-UAT` et un port distinct. Le même
nom devra être passé au script de vérification.

Une réinstallation par-dessus une existante exige `-ForcerRemplacement` :
c'est délibéré, pour qu'un écrasement soit un geste conscient.

## D.2 Configurer SQLite

Dans `C:\N4Sentinel\appsettings.Production.json` :

```json
{
  "ConnectionStrings": {
    "N4Sentinel": "Data Source=C:\\ProgramData\\N4Sentinel\\n4sentinel.db"
  },
  "N4Sentinel": {
    "Database": { "Provider": "Sqlite" },
    "DataProtection": { "KeyPath": "C:\\ProgramData\\N4Sentinel\\keys" }
  },
  "AllowedHosts": "n4sentinel.cit.local"
}
```

Trois points :

- `Provider` vaut `Sqlite`. Sans cette ligne, l'application cherche un SQL
  Server et échoue au démarrage.
- La chaîne de connexion est un **chemin de fichier**, pas un serveur.
- La base n'est pas à créer : elle est produite au premier démarrage, et les
  migrations s'appliquent seules.

L'environnement d'exécution doit valoir `Production` pour que ce fichier soit
lu. Le service le fixe par variable d'environnement ; vérifiez-le si la
configuration ne semble pas prise en compte.

## D.3 Désigner le compte de service

À faire **à la main**, dans `services.msc` → propriétés de `N4Sentinel` →
onglet *Connexion*.

Le script d'installation ne le fait pas volontairement : un mot de passe passé
en paramètre se retrouve dans l'historique PowerShell et dans les journaux.

---

# E. Sur la VM — démarrer et prouver

## E.1 Démarrer

```powershell
Start-Service N4Sentinel
```

## E.2 Prouver que ça marche

```powershell
.\Verifier-N4Sentinel.ps1 -Port 8443
```

**N'en restez pas au statut `Running`.** C'est précisément ce que N4 Sentinel
reproche à la supervision naïve : un service qui se déclare démarré ne prouve
pas qu'il rend le service. Le processus peut tourner alors que Kestrel
n'écoute pas, que le fichier de base est inaccessible, ou qu'une migration a
échoué.

Le script établit trois constats, du plus faible au plus fort :

| Constat | Ce que ça prouve | Code de sortie si échec |
|---|---|---|
| Service `Running` | Le processus tourne. **Rien de plus.** | 2 |
| Port ouvert | Kestrel écoute | 3 |
| `/health` → `Healthy` | La base répond **et le schéma est à jour** | 4 ou 5 |

> Le troisième constat vérifie l'absence de migration en attente, et non la
> simple connexion. La nuance compte sur SQLite : ouvrir un fichier y réussit
> toujours — un fichier vide, tronqué, ou créé à l'instant par une faute de
> frappe dans le chemin répondraient tous « connexion réussie ». Une sonde
> fondée sur la connexion aurait déclaré sain un déploiement sans la moindre
> table.

## E.3 Créer le premier administrateur

Seulement une fois les trois constats obtenus :

```
http://<VM>:8443
```

Le premier écran crée l'administrateur initial. **Aucun compte n'existe
avant** : il n'y a pas de mot de passe par défaut à changer, et donc pas
d'oubli possible de le faire.

Ce compte reçoit les rôles Administrateur de la solution et Auditeur, et
détient toutes les capacités de l'application.

## E.4 Déclarer l'écosystème

L'application est vide : c'est voulu. Aucun nom d'hôte, aucun nom de service
n'est écrit en dur — tout se saisit ici.

1. `/admin/environnements` — déclarez l'environnement, puis validez-le ;
2. déclarez les serveurs et les composants, ou importez-les ;
3. `/admin/workflows` — **« Générer les séquences N4 »** déduit les séquences
   de référence du référentiel, en brouillon ;
4. lancez la séquence **en simulation** : elle n'émet aucune commande ;
5. validez le workflow — la simulation réussie en est la condition ;
6. `/operations` — la séquence est lançable en réel.

Quatre aides existent pour éviter la saisie à la main : le scanner de
composants non déclarés, l'import `Navis-Config.json`, l'import CSV, et la
duplication d'un environnement existant.

## E.5 Sous quelle identité l'application atteint les serveurs N4

C'est ici que se joue la différence entre une application qui affiche des
serveurs et une application qui les supervise.

Par défaut, N4 Sentinel ouvre ses sessions WinRM **sous l'identité de son
propre processus**. C'est le mode à viser : aucun mot de passe n'est stocké,
donc aucun ne peut fuir, et le renouvellement du secret ne casse rien.

| Le service tourne sous… | Il s'authentifie comme… | Sur les serveurs N4 |
|---|---|---|
| Compte de service du domaine | ce compte | fonctionne si le compte y a les droits |
| `LocalSystem` | le **compte machine** du serveur applicatif | refusé — ce compte n'y a aucun droit |

Le second cas est celui de l'installation de repli décrite en **D.3** : le
service démarre, l'application répond, `/health` est vert — et chaque test de
connexion renvoie *« Accès refusé »*. Rien n'est cassé ; simplement, personne
n'a donné de droits au compte machine, et personne ne devrait le faire.

## E.6 Deux identités, pour deux natures de travail

L'application distingue ce qu'un humain déclenche de ce qu'elle fait seule.
Cette ligne de partage commande tout le reste.

| Nature du travail | Identité employée | Où elle se déclare |
|---|---|---|
| Un opérateur lance une séquence, une action, un test | **son** compte nominatif | « Mon compte d'exploitation » |
| La supervision balaie les composants, la nuit, sans demande | compte partagé, ou identité du processus | `/admin/environnements/{id}/comptes` |

### Le compte nominatif de chaque opérateur

À sa première connexion, un opérateur habilité à agir sur les services est
sollicité pour **son compte d'administration**, avec l'explication de la
raison. Il peut reporter : la consultation et le diagnostic restent ouverts.
Le blocage tombe là où il a un sens — au lancement d'une opération réelle.

- L'application **éprouve le compte avant de le conserver** : elle ouvre une
  session de test sur un serveur validé. Un compte refusé n'est pas enregistré.
- À chaque connexion, si le dernier contrôle date de plus de douze heures, le
  compte est revérifié. C'est ainsi qu'une **expiration ou un changement de
  mot de passe dans le domaine** se détecte : personne ne nous prévient.
- Le mot de passe est saisi par son propriétaire, chiffré au repos, et **aucun
  écran ne le restitue — pas même à un administrateur de la solution**, qui ne
  peut ni le voir ni le modifier (SEC-003).
- La désactivation d'un compte applicatif **supprime** son compte
  d'exploitation.

> **Une règle contre-intuitive, et délibérée.** Au premier refus
> d'authentification, le compte est écarté et **n'est plus jamais réessayé**
> tant qu'il n'a pas été ressaisi. Réessayer un mot de passe périmé sur chaque
> serveur, à chaque reprise de séquence, finirait par **verrouiller le compte
> de domaine de l'opérateur** — l'outil censé l'aider le mettrait dehors en
> pleine escale. Un *accès* refusé, lui, n'écarte rien : le mot de passe est
> bon, ce sont les droits qui manquent.

### Ce que cela donne côté serveurs N4

Une session WinRM est une ouverture de session réseau : l'événement 4624 du
serveur N4 porte le compte **et l'adresse source**. Une action passée par
l'application arrive depuis l'IP du serveur N4 Sentinel — `adm-mkonate` depuis
`CI018NATAPP06` se distingue donc de la même personne assise à la console,
sans qu'aucun artifice soit nécessaire.

L'identité employée est **figée au lancement** de l'opération : une reprise
trois heures plus tard, par quelqu'un d'autre, reste attribuée à celui qui a
lancé. La piste applicative et le journal du serveur ne peuvent pas diverger.

### Le compte partagé, réduit à son vrai rôle

Il ne disparaît pas : il porte le travail non surveillé. Lui prêter le compte
du dernier connecté ferait porter à cet opérateur des relevés faits par la
machine à trois heures du matin, ce qui **détruirait** l'attribution au lieu
de la servir.

Sa déclaration reste `/admin/environnements/{id}/comptes` — **« Déclarer un
compte »**, mode *Compte explicite*, puis sélection sur la fiche du serveur
dans **Compte technique**. Un compte de service dédié du domaine (`D.3`) reste
préférable : il n'y a alors aucun mot de passe à protéger.

Le trousseau de clés qui chiffre tous ces secrets est celui que **G.1** impose
de sauvegarder avec la base. Le perdre oblige désormais **chaque opérateur** à
ressaisir son compte, pas seulement l'administrateur.

---

# F. Ce qui diffère de SQL Server

**Ce qui ne change pas** : transactions, index uniques tenus par la base — dont
le verrou d'un seul opérateur par environnement —, concurrence optimiste,
migrations, clés étrangères.

| | SQL Server | SQLite |
|---|---|---|
| Sauvegarde | `BACKUP DATABASE` | `VACUUM INTO`, même garantie |
| Vérification | `RESTORE VERIFYONLY` | `PRAGMA integrity_check` |
| Jeton de concurrence | incrémenté par le moteur | estampillé par l'application |
| Accès concurrent | plusieurs machines | **une seule machine** |

La dernière ligne est la vraie limite : SQLite est un fichier local. Deux
instances sur deux VM ne peuvent pas partager la même base. Si la reprise sur
une seconde VM entre un jour au périmètre, il faudra revenir à SQL Server — la
bascule se fait par configuration, mais les données seront à migrer.

Un écart plus fin, écrit dans le code : sur SQL Server, le moteur incrémente le
jeton de concurrence même lors d'un `ExecuteUpdate`, qui contourne le suivi de
modifications. Sur SQLite, non. Les deux seuls appels concernés sont ceux
écrits pour ne pas entrer en conflit, donc sans conséquence aujourd'hui — mais
cela cesserait de l'être si `ExecuteUpdate` était employé ailleurs.

---

# G. Sur la VM — exploitation courante

## G.1 Sauvegarder

**Deux choses, indissociables :**

1. `C:\ProgramData\N4Sentinel\n4sentinel.db` — la base ;
2. `C:\ProgramData\N4Sentinel\keys` — le trousseau.

Le trousseau chiffre les mots de passe des comptes techniques, par DPAPI **à
l'échelle de la machine**. Conséquence à connaître avant d'en avoir besoin :
une base restaurée sur une AUTRE machine sans son trousseau s'ouvrira
normalement, mais aucun compte technique ne sera déchiffrable. Il faudra tous
les ressaisir.

Passez par `/admin/sauvegarde` plutôt que par une copie de fichier. L'écran
utilise `VACUUM INTO`, qui demande au moteur d'écrire une base complète et
cohérente sans interrompre le service. Une simple copie capturerait un instant
qui n'a jamais existé — écritures en cours, journal non replié — et le défaut
ne se verrait qu'au jour de la restauration. La sauvegarde est ensuite relue
par `PRAGMA integrity_check`.

## G.2 Mettre à jour

**Sauvegardez avant. Une migration ne se défait pas.**

```powershell
Stop-Service N4Sentinel
```

```powershell
.\Installer-N4Sentinel.ps1 -Source .\application -Destination C:\N4Sentinel -ForcerRemplacement
```

```powershell
Start-Service N4Sentinel; .\Verifier-N4Sentinel.ps1 -Port 8443
```

La base et le trousseau ne sont pas touchés, puisqu'ils vivent hors du dossier
d'application (C.4). Les migrations en attente s'appliquent au démarrage.

## G.3 Dépanner

| Symptôme | Où regarder |
|---|---|
| Erreur 1053 au démarrage | Version antérieure au 20/08/2026 : l'exécutable ne se déclarait pas au SCM |
| Échec d'ouverture de session | Le compte de service n'a pas le droit « ouverture de session en tant que service » (C.3) |
| Service `Running`, port muet | `C:\N4Sentinel\logs\n4sentinel-*.log` |
| Port ouvert, `/health` muet | Amorçage bloqué sur les migrations — même journal |
| `/health` répond `Unhealthy` | Connectez-vous, puis `/health/detail` donne le motif |
| « Base verrouillée » | Une seule instance par fichier : vérifiez qu'un second service ne pointe pas la même base |
| Configuration ignorée | L'environnement d'exécution ne vaut pas `Production` (D.2) |

---

## Ce que ce document ne couvre pas

**Le chiffrement du transport.** Le service écoute en HTTP simple. Sur un
réseau d'exploitation isolé, c'est un choix défendable — mais c'en est un : les
mots de passe de connexion et les codes du second facteur circulent en clair
sur le LAN. Sans IIS, il n'y a plus de terminaison TLS fournie ; il faut
déclarer un certificat directement dans Kestrel.

**Le déploiement n'a pas été éprouvé sur une VM réelle.** Chaque élément a été
vérifié séparément — publication, présence des migrations SQLite dans le
paquet, absence de la dépendance vulnérable, création et relecture d'une base
fichier, sauvegarde par `VACUUM INTO` avec contrôle d'intégrité — mais
l'installation complète sur une machine cible reste à faire. Le premier
`Start-Service` est le vrai test.
