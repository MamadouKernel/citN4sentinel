namespace N4Sentinel.Domain;

/// <summary>
/// Profils d'acces definis au cahier des charges (CdC 2.3.2).
///
/// Les droits de lecture, de diagnostic, d'execution, d'approbation et
/// d'administration sont attribues SEPAREMENT : c'est la traduction du
/// moindre privilege et de la separation des responsabilites (SEC-002).
///
/// Regles structurantes a ne pas perdre de vue en implementant :
///   - le lancement et l'approbation d'une meme operation critique en
///     Production doivent etre le fait de deux utilisateurs distincts ;
///   - un Administrateur N4 peut demander un contournement, mais ne doit
///     pas approuver seul le sien ;
///   - les droits se differencient par environnement, notamment entre UAT
///     et Production.
/// </summary>
public static class N4Roles
{
    /// <summary>Consultation des tableaux de bord, etats, historiques et rapports.</summary>
    public const string LecteurSupportN1 = "Lecteur-SupportN1";

    /// <summary>Lance les diagnostics et les scenarios autorises, confirme les etapes.</summary>
    public const string OperateurN4 = "Operateur-N4";

    /// <summary>Operations sensibles completes, partielles ou unitaires ; demande de contournement.</summary>
    public const string AdministrateurN4 = "Administrateur-N4";

    /// <summary>Connecteurs techniques, controles reseau et systeme, references aux secrets.</summary>
    public const string AdministrateurInfrastructure = "Administrateur-Infrastructure";

    /// <summary>Approuve ou refuse les operations critiques, contournements et arrets forces.</summary>
    public const string Validateur = "Validateur";

    /// <summary>Environnements, composants, workflows, seuils, regles, roles, parametres.</summary>
    public const string AdministrateurSolution = "Administrateur-Solution";

    /// <summary>Acces en lecture aux historiques, preuves, habilitations et journaux d'audit.</summary>
    public const string Auditeur = "Auditeur";

    /// <summary>Consultation operationnelle, sans pilotage ni administration technique.</summary>
    public const string TosManager = "TOS-Manager";

    public static readonly string[] All =
    [
        LecteurSupportN1,
        OperateurN4,
        AdministrateurN4,
        AdministrateurInfrastructure,
        Validateur,
        AdministrateurSolution,
        Auditeur,
        TosManager
    ];

    /// <summary>Description affichee dans l'ecran d'administration des roles.</summary>
    public static string DescriptionOf(string role) => role switch
    {
        LecteurSupportN1 => "Consulter tableaux de bord, etats, alertes, historiques, diagnostics et rapports.",
        OperateurN4 => "Lancer des diagnostics, executer les scenarios autorises, confirmer les etapes prevues.",
        AdministrateurN4 => "Executer les operations sensibles, analyser les incidents avances, demander un contournement.",
        AdministrateurInfrastructure => "Configurer les connecteurs, controles reseau et systeme, references aux secrets.",
        Validateur => "Approuver ou refuser les operations critiques en Production, contournements et arrets forces.",
        AdministrateurSolution => "Gerer environnements, composants, workflows, seuils, regles, roles et parametres.",
        Auditeur => "Acceder en lecture aux historiques, preuves, habilitations et journaux d'audit.",
        TosManager => "Consulter les etats, alertes et rapports d'interet operationnel, sans pilotage.",
        _ => role
    };
}

/// <summary>
/// Noms des politiques d'autorisation. Une politique exprime une CAPACITE
/// metier ; les roles y sont rattaches. Le code verifie la capacite, jamais
/// le role directement - ce qui permet de faire evoluer la matrice
/// d'habilitation sans rouvrir le code applicatif.
/// </summary>
public static class N4Policies
{
    public const string PeutConsulter = "PeutConsulter";
    public const string PeutDiagnostiquer = "PeutDiagnostiquer";
    public const string PeutExecuter = "PeutExecuter";
    public const string PeutApprouver = "PeutApprouver";
    public const string PeutAdministrerReferentiel = "PeutAdministrerReferentiel";
    public const string PeutAdministrerConnecteurs = "PeutAdministrerConnecteurs";
    public const string PeutAuditer = "PeutAuditer";
}
