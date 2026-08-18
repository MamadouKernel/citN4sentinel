# ADR-SEC-007 : Utilisation de DPAPI Windows pour le chiffrement des secrets

## 1. Contexte
N4 Sentinel a besoin de stocker de manière sécurisée les informations d'identification des comptes de service (mots de passe, tokens) utilisés pour se connecter aux composants N4 (base de données, WinRM, API) via la classe `TechnicalCredential`.

## 2. Solution retenue (FR-056)
L'application utilise l'API de protection des données (Data Protection) d'ASP.NET Core avec la clé principale protégée par **Windows DPAPI** (Data Protection API) :
```csharp
builder.Services.AddDataProtection()
    .SetApplicationName("N4Sentinel")
    .ProtectKeysWithDpapi();
```

## 3. Analyse de la dépendance DPAPI

### Avantages
- **Sécurité native** : Les clés de chiffrement sont liées au compte utilisateur Windows ou à la machine locale. Un attaquant obtenant la base de données ne peut pas déchiffrer les secrets (par exemple la colonne `ProtectedPassword` de la table `Credentials`) sans un accès au serveur hébergeant l'application.
- **Aucune gestion de clé** : Pas besoin de gérer, stocker ou faire tourner manuellement une clé maîtresse. Tout est géré par l'OS.

### Contraintes et Limites
- **Couplage Windows** : DPAPI est spécifique à Windows. Si N4 Sentinel devait être migré sous Linux (Docker/Kubernetes), DPAPI ne serait pas disponible.
- **Scénario de ferme de serveurs** : Si N4 Sentinel tourne sur plusieurs serveurs derrière un Load Balancer (ce qui n'est pas le cas pour la V1), chaque nœud utiliserait son propre jeu de clés DPAPI local, ce qui empêcherait le déchiffrement des secrets chiffrés par un autre nœud. Il faudrait alors stocker les clés sur un partage réseau ou dans le registre (via un certificat X.509 ou Azure Key Vault).

## 4. Recommandation pour la suite
La solution actuelle satisfait les exigences de sécurité (SEC-004) pour un hébergement sur un serveur IIS unique.
Si un passage vers Kubernetes ou Linux est envisagé dans le futur, il faudra remplacer `.ProtectKeysWithDpapi()` par un fournisseur neutre, comme Azure Key Vault.
