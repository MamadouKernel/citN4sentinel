# Déploiement N4 Sentinel — SQLite et service Windows

**Sans IIS, sans SQL Server.** Un exécutable, un fichier de base, un service
Windows.

Ce mode vise la VM sur laquelle on ne veut rien installer d'autre. Ce n'est pas
un mode dégradé : transactions, index uniques et concurrence optimiste sont
tenus par le moteur, exactement comme sur SQL Server. Les différences réelles
sont énumérées au § 8.

---

## 1. Ce qu'il faut sur la VM

| | |
|---|---|
| Windows Server | 2019 ou ultérieur |
| Runtime | ASP.NET Core 10, ou publication autonome (§ 2) |
| Compte de service | compte de domaine, **pas** LocalSystem |
| Ports | un port TCP libre, 8080 par défaut |
| Espace disque | 2 Go pour l'application, plus la croissance de la base |

**Rien d'autre.** Pas de SQL Server, pas de rôle IIS.

---

## 2. Produire le paquet

Depuis le poste de développement :

```powershell
.\deploiement\Publier-N4Sentinel.ps1
```

Le paquet ne contient **aucun secret** : les gabarits de configuration sont
livrés avec des valeurs vides, à renseigner sur la machine cible.

Si la VM n'a pas le runtime ASP.NET Core, publiez en autonome :

```powershell
dotnet publish src\N4Sentinel.Web -c Release -r win-x64 --self-contained -o publication
```

---

## 3. Installer

```powershell
.\Installer-N4Sentinel.ps1 -Source .\application -Destination C:\N4Sentinel -Port 8080
```

Le script crée le service `N4Sentinel`, le règle en démarrage automatique,
définit sa politique de redémarrage après incident et ouvre le pare-feu sur le
profil Domaine.

Le nom du service est un paramètre : pour faire cohabiter deux instances sur
une même VM, passez `-NomService N4Sentinel-UAT`, et le même paramètre au
script de vérification.

---

## 4. Configurer SQLite

Dans `C:\N4Sentinel\appsettings.Production.json` :

```json
{
  "ConnectionStrings": {
    "N4Sentinel": "Data Source=C:\\ProgramData\\N4Sentinel\\n4sentinel.db"
  },
  "N4Sentinel": {
    "Database": { "Provider": "Sqlite" },
    "DataProtection": { "KeyPath": "C:\\ProgramData\\N4Sentinel\\keys" }
  }
}
```

**Placez la base hors du dossier d'application.** Une mise à jour remplace le
contenu de `C:\N4Sentinel` ; une base qui y résiderait serait écrasée.

Créez le dossier et donnez-en le contrôle au compte de service :

```powershell
New-Item -ItemType Directory -Force C:\ProgramData\N4Sentinel
icacls C:\ProgramData\N4Sentinel /grant "CIT\svc_n4sentinel:(OI)(CI)M"
```

La base n'est pas à créer : elle est produite au premier démarrage, et les
migrations s'appliquent seules.

---

## 5. Désigner le compte de service

À faire à la main, dans `services.msc` → propriétés de `N4Sentinel` → onglet
*Connexion*.

Le script ne le fait pas volontairement : un mot de passe passé en paramètre
se retrouve dans l'historique PowerShell et dans les journaux.

Ce compte a besoin de trois choses, et de rien de plus :

1. **Écriture** sur `C:\ProgramData\N4Sentinel` — base et trousseau de clés ;
2. **WinRM** vers les serveurs N4 à superviser ;
3. **Ouverture de session en tant que service** sur la VM.

Il n'a besoin d'aucun droit sur un SQL Server, puisqu'il n'y en a pas.

---

## 6. Démarrer, puis PROUVER que ça marche

```powershell
Start-Service N4Sentinel
```

```powershell
.\Verifier-N4Sentinel.ps1 -Port 8080
```

**N'en restez pas au statut `Running`.** C'est précisément ce que N4 Sentinel
reproche à la supervision naïve : un service qui se déclare démarré ne prouve
pas qu'il rend le service. Le processus peut tourner alors que Kestrel
n'écoute pas, que le fichier de base est inaccessible, ou qu'une migration a
échoué.

Le script établit trois constats, du plus faible au plus fort :

| Constat | Ce que ça prouve |
|---|---|
| Service `Running` | Le processus tourne. Rien de plus. |
| Port ouvert | Kestrel écoute |
| `/health` → `Healthy` | La base est joignable, les migrations ont abouti |

Si le démarrage échoue sur l'**erreur 1053**, l'exécutable ne se déclare pas au
gestionnaire de services — vérifiez que la version déployée est postérieure au
20/08/2026.

Ensuite seulement : `http://<VM>:8080`. Le premier écran crée l'administrateur
initial. Aucun compte n'existe avant : il n'y a pas de mot de passe par défaut
à changer.

---

## 7. Sauvegarder

**Deux choses, indissociables :**

1. `C:\ProgramData\N4Sentinel\n4sentinel.db` — la base ;
2. `C:\ProgramData\N4Sentinel\keys` — le trousseau.

Le trousseau chiffre les mots de passe des comptes techniques, **par DPAPI à
l'échelle de la machine**. Conséquence à connaître avant d'en avoir besoin :
une base restaurée sur une AUTRE machine sans son trousseau s'ouvrira
normalement, mais aucun compte technique ne sera déchiffrable. Il faudra tous
les ressaisir.

Passez par l'écran `/admin/sauvegarde` plutôt que par une copie de fichier.
Il utilise `VACUUM INTO`, qui demande au moteur d'écrire une base complète et
cohérente sans interrompre le service — une simple copie capturerait un
instant qui n'a jamais existé, et le défaut ne se verrait qu'au jour de la
restauration. La sauvegarde est ensuite relue par `PRAGMA integrity_check`.

---

## 8. Ce qui diffère de SQL Server

**Ce qui ne change pas** : transactions, index uniques tenus par la base — dont
le verrou d'un seul opérateur par environnement —, concurrence optimiste,
migrations, clés étrangères.

**Ce qui change :**

| | SQL Server | SQLite |
|---|---|---|
| Sauvegarde | `BACKUP DATABASE` | `VACUUM INTO`, même garantie |
| Vérification | `RESTORE VERIFYONLY` | `PRAGMA integrity_check` |
| Jeton de concurrence | incrémenté par le moteur | estampillé par l'application |
| Accès concurrent | plusieurs machines | **une seule machine** |

Cette dernière ligne est la vraie limite : SQLite est un fichier local. Deux
instances de N4 Sentinel sur deux VM ne peuvent pas partager la même base.
Si la répartition de charge ou la reprise sur une seconde VM entre un jour dans
le périmètre, il faudra revenir à SQL Server — le changement se fait par la
configuration, mais les données seront à migrer.

Un écart plus fin, écrit dans le code : sur SQL Server, le moteur incrémente le
jeton de concurrence même lors d'un `ExecuteUpdate`, qui contourne le suivi de
modifications. Sur SQLite, non. Les deux seuls appels concernés sont ceux qui
ont été écrits pour ne pas entrer en conflit, donc c'est sans conséquence
aujourd'hui — mais cela cesserait de l'être si `ExecuteUpdate` était employé
ailleurs.

---

## 9. Mettre à jour

```powershell
Stop-Service N4Sentinel
.\Installer-N4Sentinel.ps1 -Source .\application -Destination C:\N4Sentinel -ForcerRemplacement
Start-Service N4Sentinel
.\Verifier-N4Sentinel.ps1 -Port 8080
```

La base et le trousseau ne sont pas touchés, puisqu'ils vivent hors du dossier
d'application (§ 4). Les migrations en attente s'appliquent au démarrage.

**Sauvegardez avant.** Une migration ne se défait pas.

---

## 10. En cas de problème

| Symptôme | Où regarder |
|---|---|
| Erreur 1053 au démarrage | Version antérieure au 20/08/2026 : l'exécutable ne se déclarait pas au SCM |
| Service `Running`, port muet | `C:\N4Sentinel\logs\n4sentinel-*.log` |
| Port ouvert, `/health` muet | Amorçage bloqué sur les migrations — même journal |
| `/health` répond `Unhealthy` | Connectez-vous, puis `/health/detail` donne le motif |
| Base verrouillée | Une seule instance par fichier. Vérifiez qu'un second service ne pointe pas la même base |

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
fichier, sauvegarde par `VACUUM INTO` — mais l'installation complète sur une
machine cible reste à faire. Le premier `Start-Service` est le vrai test.
