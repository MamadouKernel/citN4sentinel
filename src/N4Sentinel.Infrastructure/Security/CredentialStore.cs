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
    public async Task<List<TechnicalCredential>> GetForEnvironmentAsync(
        Guid environmentId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Credentials
            .AsNoTracking()
            .Where(c => c.EnvironmentId == environmentId)
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
            .FirstOrDefaultAsync(c => c.EnvironmentId == environmentId && c.Reference == reference, ct);
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

        var duplicate = await db.Credentials.AnyAsync(
            c => c.EnvironmentId == credential.EnvironmentId
                 && c.Reference == credential.Reference
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
