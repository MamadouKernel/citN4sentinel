using System.Management.Automation;
using System.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Security;

/// <summary>
/// Magasin des comptes techniques.
///
/// Chiffrement au repos par ASP.NET Core Data Protection, adosse a DPAPI sur
/// Windows. Seul le chiffre est en base : quiconque lit la table ne voit rien
/// d'exploitable sans la cle, et la cle ne quitte pas le serveur applicatif.
///
/// CONSEQUENCE A CONNAITRE : la perte du trousseau de cles rend les mots de
/// passe stockes indechiffrables. Ils devront etre ressaisis. C'est le prix du
/// chiffrement au repos, et c'est pour cela que le trousseau est persiste dans
/// un dossier sauvegarde plutot que laisse a son emplacement par defaut.
///
/// Ce magasin est concu pour etre remplace : le jour ou CIT dispose d'un
/// coffre d'entreprise, seule cette classe change. Ni les ecrans, ni le
/// connecteur, ni le referentiel n'en dependent directement.
/// </summary>
public sealed class CredentialStore
{
    /// <summary>
    /// La chaine de finalite lie le chiffre a cet usage precis. Un chiffre
    /// produit ici ne peut pas etre dechiffre par un autre composant de
    /// l'application, meme avec le meme trousseau.
    /// </summary>
    private const string Purpose = "N4Sentinel.TechnicalCredential.v1";

    private readonly IDbContextFactory<N4SentinelDbContext> _dbFactory;
    private readonly IDataProtector _protector;
    private readonly ILogger<CredentialStore> _logger;

    public CredentialStore(
        IDbContextFactory<N4SentinelDbContext> dbFactory,
        IDataProtectionProvider protectionProvider,
        ILogger<CredentialStore> logger)
    {
        _dbFactory = dbFactory;
        _protector = protectionProvider.CreateProtector(Purpose);
        _logger = logger;
    }

    // -----------------------------------------------------------------------
    // Lecture - jamais le secret
    // -----------------------------------------------------------------------
    /// <summary>
    /// Comptes PARTAGES d'un environnement, ceux qu'une fiche serveur peut
    /// designer.
    ///
    /// Les comptes nominatifs en sont volontairement exclus : le compte
    /// personnel d'un operateur n'est pas un choix de configuration, et le
    /// proposer dans une liste deroulante reviendrait a le montrer a tous les
    /// autres.
    /// </summary>
    public async Task<List<TechnicalCredential>> GetForEnvironmentAsync(
        Guid environmentId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Credentials
            .AsNoTracking()
            .Where(c => c.EnvironmentId == environmentId && c.OwnerUserId == null)
            .OrderBy(c => c.Label)
            .ToListAsync(ct);
    }

    public async Task<TechnicalCredential?> GetByReferenceAsync(
        Guid environmentId, string reference, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Credentials
            .AsNoTracking()
            .FirstOrDefaultAsync(
                c => c.EnvironmentId == environmentId
                     && c.Reference == reference
                     && c.OwnerUserId == null, ct);
    }

    // -----------------------------------------------------------------------
    // Comptes nominatifs
    // -----------------------------------------------------------------------
    /// <summary>
    /// Compte d'exploitation d'un operateur. Un seul par personne, valable sur
    /// tous les environnements : chez CIT le meme compte d'administration sert
    /// partout, et en demander un par environnement multiplierait les saisies
    /// sans rien apporter.
    /// </summary>
    public async Task<TechnicalCredential?> GetForOwnerAsync(
        string userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId)) return null;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Credentials
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.OwnerUserId == userId, ct);
    }

    /// <summary>
    /// Compte d'exploitation retrouve par l'identifiant de connexion applicatif.
    ///
    /// C'est la voie qu'emprunte le moteur d'orchestration : une execution
    /// enregistre le nom de son demandeur, jamais son identifiant technique.
    /// </summary>
    public async Task<TechnicalCredential?> GetForLoginAsync(
        string login, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(login)) return null;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Credentials
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.OwnerLogin == login, ct);
    }

    /// <summary>
    /// Enregistre ou met a jour le compte d'exploitation d'un operateur.
    ///
    /// Un mot de passe fourni leve l'obligation de ressaisie : c'est le geste
    /// meme par lequel l'operateur reprend la main apres une expiration.
    /// </summary>
    public async Task<string?> SaveOwnAsync(
        string userId, string login, string displayName, string userName, string password,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return "Aucun utilisateur identifie.";

        if (string.IsNullOrWhiteSpace(userName))
            return "Le compte est obligatoire, au format DOMAINE\\utilisateur.";

        if (!userName.Contains('\\') && !userName.Contains('@'))
            return "Le compte doit porter son domaine : DOMAINE\\utilisateur, ou utilisateur@domaine.";

        if (string.IsNullOrWhiteSpace(password))
            return "Le mot de passe est obligatoire.";

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var existing = await db.Credentials.FirstOrDefaultAsync(c => c.OwnerUserId == userId, ct);

        if (existing is null)
        {
            var credential = new TechnicalCredential
            {
                OwnerUserId = userId,
                OwnerLogin = login,
                OwnerDisplayName = displayName,
                // La reference porte l'identifiant : deux operateurs peuvent
                // partager le meme compte AD sans collision de reference.
                Reference = $"operateur:{userId}",
                Label = $"Compte d'exploitation de {displayName}",
                Mode = CredentialMode.CompteExplicite,
                UserName = userName.Trim(),
                Status = LifecycleStatus.Actif,
                Description = "Compte nominatif saisi par son proprietaire."
            };
            ApplyPassword(credential, password);
            credential.RequiresReentry = false;
            db.Credentials.Add(credential);
        }
        else
        {
            existing.OwnerLogin = login;
            existing.OwnerDisplayName = displayName;
            existing.UserName = userName.Trim();
            existing.Mode = CredentialMode.CompteExplicite;
            existing.Status = LifecycleStatus.Actif;
            ApplyPassword(existing, password);
            existing.RequiresReentry = false;
            existing.InvalidatedAt = null;
            existing.InvalidationReason = null;
        }

        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Compte d'exploitation enregistre pour {Utilisateur}. Aucun secret n'est journalise.",
            displayName);

        return null;
    }

    /// <summary>
    /// Marque un compte comme devant etre ressaisi, et l'ecarte immediatement.
    ///
    /// Appele des qu'une authentification est refusee. C'est ce qui empeche
    /// l'application de verrouiller le compte de domaine de l'operateur a force
    /// de reessayer un mot de passe perime.
    /// </summary>
    public async Task InvalidateAsync(
        Guid credentialId, string reason, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var credential = await db.Credentials.FirstOrDefaultAsync(c => c.Id == credentialId, ct);
        if (credential is null || credential.RequiresReentry) return;

        credential.RequiresReentry = true;
        credential.InvalidatedAt = DateTimeOffset.UtcNow;
        credential.InvalidationReason = reason.Length > 400 ? reason[..400] : reason;
        await db.SaveChangesAsync(ct);

        _logger.LogWarning(
            "Compte {Reference} ecarte apres un refus d'authentification : {Motif}. "
            + "Il ne sera plus employe tant qu'il n'aura pas ete ressaisi.",
            credential.Reference, reason);
    }

    /// <summary>
    /// Supprime le compte nominatif d'un utilisateur - a sa desactivation, ou
    /// a sa demande. Un secret qui survit a son proprietaire est un secret qui
    /// finira par servir a quelqu'un d'autre.
    /// </summary>
    public async Task<bool> EraseForOwnerAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var credential = await db.Credentials.FirstOrDefaultAsync(c => c.OwnerUserId == userId, ct);
        if (credential is null) return false;

        db.Credentials.Remove(credential);
        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Compte d'exploitation de {Proprietaire} supprime.", credential.OwnerDisplayName);
        return true;
    }

    // -----------------------------------------------------------------------
    // Ecriture
    // -----------------------------------------------------------------------
    /// <summary>
    /// Cree ou met a jour un compte technique.
    ///
    /// Le mot de passe est facultatif a la mise a jour : ne rien passer laisse
    /// le secret existant intact. C'est ce qui permet a un ecran de modifier le
    /// libelle d'un compte sans jamais avoir eu besoin de relire son mot de
    /// passe - ni de demander a l'operateur de le ressaisir.
    /// </summary>
    public async Task<string?> SaveAsync(
        TechnicalCredential credential, string? newPassword, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(credential.Reference))
            return "La reference est obligatoire : c'est elle que citent les serveurs.";

        if (credential.Mode == CredentialMode.CompteExplicite
            && string.IsNullOrWhiteSpace(credential.UserName))
            return "Un compte explicite exige un nom d'utilisateur, au format DOMAINE\\utilisateur.";

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var existing = await db.Credentials
            .FirstOrDefaultAsync(c => c.Id == credential.Id, ct);

        // Un compte nominatif ne se modifie que par son proprietaire, via
        // SaveOwnAsync. L'ecran des comptes partages ne doit pas pouvoir le
        // reecrire - pas meme pour un administrateur de la solution.
        if (existing is not null && existing.IsNominative)
            return "Ce compte appartient a un operateur : lui seul peut le modifier.";

        var duplicate = await db.Credentials.AnyAsync(
            c => c.EnvironmentId == credential.EnvironmentId
                 && c.Reference == credential.Reference
                 && c.OwnerUserId == null
                 && c.Id != credential.Id, ct);

        if (duplicate)
            return $"La reference '{credential.Reference}' est deja utilisee dans cet environnement.";

        if (existing is null)
        {
            if (credential.Mode == CredentialMode.CompteExplicite && string.IsNullOrWhiteSpace(newPassword))
                return "Un compte explicite exige un mot de passe a la creation.";

            ApplyPassword(credential, newPassword);
            db.Credentials.Add(credential);
        }
        else
        {
            existing.Reference = credential.Reference;
            existing.Label = credential.Label;
            existing.Mode = credential.Mode;
            existing.UserName = credential.UserName;
            existing.Description = credential.Description;
            existing.Status = credential.Status;

            // Passer en identite du processus efface le secret devenu inutile :
            // un secret conserve "au cas ou" est un secret qui fuira un jour.
            if (credential.Mode == CredentialMode.IdentiteDuProcessus)
            {
                existing.ProtectedPassword = null;
                existing.PasswordSetAt = null;
                existing.UserName = null;
            }
            else if (!string.IsNullOrWhiteSpace(newPassword))
            {
                ApplyPassword(existing, newPassword);
            }
        }

        await db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>Efface le secret sans supprimer le compte.</summary>
    public async Task ClearPasswordAsync(Guid credentialId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var credential = await db.Credentials.FirstOrDefaultAsync(c => c.Id == credentialId, ct);
        if (credential is null) return;

        credential.ProtectedPassword = null;
        credential.PasswordSetAt = null;
        credential.LastVerifiedAt = null;
        credential.LastVerificationResult = null;
        await db.SaveChangesAsync(ct);

        _logger.LogInformation("Secret efface pour le compte technique {Reference}.", credential.Reference);
    }

    public async Task RecordVerificationAsync(
        Guid credentialId, bool succeeded, string message, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var credential = await db.Credentials.FirstOrDefaultAsync(c => c.Id == credentialId, ct);
        if (credential is null) return;

        if (succeeded) credential.LastVerifiedAt = DateTimeOffset.UtcNow;

        // Le message est tronque : un retour d'erreur distant peut etre verbeux,
        // et n'a pas vocation a remplir la base.
        credential.LastVerificationResult = message.Length > 400 ? message[..400] : message;
        await db.SaveChangesAsync(ct);
    }

    // -----------------------------------------------------------------------
    // Dechiffrement - le seul point de sortie du secret
    // -----------------------------------------------------------------------
    /// <summary>
    /// Construit le PSCredential utilise pour ouvrir une session distante.
    ///
    /// Retourne null en identite du processus : c'est le cas nominal, pas une
    /// erreur - le connecteur se connectera alors sous l'identite de
    /// l'application.
    ///
    /// Le mot de passe n'existe en clair que le temps de remplir un
    /// SecureString, et n'est jamais retourne sous forme de chaine.
    /// </summary>
    public PSCredential? BuildPSCredential(TechnicalCredential credential)
    {
        if (credential.Mode == CredentialMode.IdentiteDuProcessus)
            return null;

        if (string.IsNullOrWhiteSpace(credential.UserName)
            || string.IsNullOrWhiteSpace(credential.ProtectedPassword))
        {
            _logger.LogWarning(
                "Compte technique {Reference} declare explicite mais incomplet : connexion sous l'identite du processus.",
                credential.Reference);
            return null;
        }

        try
        {
            var clair = _protector.Unprotect(credential.ProtectedPassword);

            var securise = new SecureString();
            foreach (var c in clair) securise.AppendChar(c);
            securise.MakeReadOnly();

            return new PSCredential(credential.UserName, securise);
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            // Cas typique : le trousseau de cles a change ou a ete perdu.
            // Dire clairement quoi faire vaut mieux qu'une erreur d'authentification
            // incomprehensible au moment ou l'on en a le plus besoin.
            _logger.LogError(ex,
                "Dechiffrement impossible pour le compte technique {Reference}. Le trousseau de cles de " +
                "protection a probablement change : le mot de passe doit etre ressaisi.",
                credential.Reference);
            return null;
        }
    }

    private void ApplyPassword(TechnicalCredential credential, string? password)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            credential.ProtectedPassword = null;
            credential.PasswordSetAt = null;
            return;
        }

        credential.ProtectedPassword = _protector.Protect(password);
        credential.PasswordSetAt = DateTimeOffset.UtcNow;
        credential.LastVerifiedAt = null;
        credential.LastVerificationResult = null;
    }
}
