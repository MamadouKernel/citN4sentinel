using N4Sentinel.Infrastructure.Diagnostic;

namespace N4Sentinel.Tests;

/// <summary>
/// Tests du masquage des secrets — DIA-04, recette AC-11.
///
/// Ces tests ne vérifient pas une commodité d'affichage. Ils vérifient qu'un
/// mot de passe de production ne peut pas entrer dans la base de N4 Sentinel,
/// donc ni dans ses sauvegardes, ni dans un rapport transmis à un tiers.
///
/// Le test qui compte le plus est le dernier : il prend une ligne de journal
/// N4 réaliste et vérifie qu'après masquage, plus rien ne ressemble à un
/// secret. C'est le seul qui attrape un motif oublié.
/// </summary>
public sealed class MasquageTests
{
    [Theory]
    // Formes usuelles clé/valeur
    [InlineData("password=Bonjour@2026", "Bonjour@2026")]
    [InlineData("PASSWORD = SuperSecret123", "SuperSecret123")]
    [InlineData("pwd:motdepasse", "motdepasse")]
    [InlineData("motdepasse=Azerty123!", "Azerty123!")]
    // JSON
    [InlineData("{\"user\":\"navis\",\"password\":\"P@ssw0rd\"}", "P@ssw0rd")]
    [InlineData("{\"apiKey\":\"sk-abc123def456\"}", "sk-abc123def456")]
    // XML de configuration N4
    [InlineData("<password>ClearText42</password>", "ClearText42")]
    [InlineData("<credential>abc-def-ghi</credential>", "abc-def-ghi")]
    // Chaines de connexion
    [InlineData("jdbc:sqlserver://srv01:1433;user=n4;password=Prod2026", "Prod2026")]
    [InlineData("Server=SRV;Database=n4;User ID=sa;Password=Sql!2026;", "Sql!2026")]
    // En-tetes HTTP
    [InlineData("Authorization: Bearer eyJhbGciOiJIUzI1NiJ9", "eyJhbGciOiJIUzI1NiJ9")]
    [InlineData("authorization: Basic bmF2aXM6c2VjcmV0", "bmF2aXM6c2VjcmV0")]
    // Jetons
    [InlineData("client_secret=zX9-secret-value", "zX9-secret-value")]
    [InlineData("access_token: ya29.a0AfH6", "ya29.a0AfH6")]
    // Identifiants dans une URL
    [InlineData("https://navis:Secret123@ecn4.cit.ci/api", "Secret123")]
    // Attribut XML : la forme que prend la configuration Mule/ESB deversee
    // dans navis-apex.log quand com.navis.control passe en DEBUG.
    [InlineData("<db:connection user=\"eci\" password=\"EciProd2026\" />", "EciProd2026")]
    [InlineData("<spring:property name='password' value='x'/> password='EciProd2026'", "EciProd2026")]
    public void Un_Secret_Est_Masque(string ligne, string secret)
    {
        var (masque, compte) = SecretMasker.Masquer(ligne);

        Assert.DoesNotContain(secret, masque);
        Assert.Contains(SecretMasker.Remplacement, masque);
        Assert.True(compte >= 1, $"Aucun secret compté dans « {ligne} »");
    }

    [Fact]
    public void Le_Contexte_Reste_Lisible_Autour_Du_Masquage()
    {
        // Effacer la ligne entiere ferait perdre l'information utile : on doit
        // continuer a voir QUE c'etait un mot de passe, et pour quel compte.
        var (masque, _) = SecretMasker.Masquer(
            "jdbc:sqlserver://SRV-N4-01:1433;databaseName=navis;user=n4app;password=Prod2026");

        Assert.Contains("SRV-N4-01", masque);
        Assert.Contains("user=n4app", masque);
        Assert.Contains("password=", masque);
        Assert.DoesNotContain("Prod2026", masque);
    }

    [Fact]
    public void Une_Cle_Privee_Est_Entierement_Masquee()
    {
        var texte = "-----BEGIN RSA PRIVATE KEY-----\n"
                  + "MIIEowIBAAKCAQEA1234567890abcdefghijklmnop\n"
                  + "qrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ==\n"
                  + "-----END RSA PRIVATE KEY-----";

        var (masque, compte) = SecretMasker.Masquer(texte);

        Assert.DoesNotContain("MIIEowIBAAKCAQEA", masque);
        Assert.DoesNotContain("qrstuvwxyz", masque);
        Assert.Equal(1, compte);
    }

    [Fact]
    public void Une_Ligne_Sans_Secret_N_Est_Pas_Alteree()
    {
        // Le masquage doit etre chirurgical : abimer les lignes normales
        // rendrait le diagnostic plus difficile, pas plus sur.
        const string ligne =
            "2026-08-14 09:12:03,441 INFO  [main] c.n.a.WebTier - Web tier servlet 'action' initialized";

        var (masque, compte) = SecretMasker.Masquer(ligne);

        Assert.Equal(ligne, masque);
        Assert.Equal(0, compte);
    }

    [Fact]
    public void Le_Mot_Password_Seul_Dans_Une_Phrase_N_Est_Pas_Masque()
    {
        const string ligne = "2026-08-14 09:12:03 WARN Authentication failed: invalid password supplied";

        var (masque, compte) = SecretMasker.Masquer(ligne);

        Assert.Equal(ligne, masque);
        Assert.Equal(0, compte);
    }

    [Fact]
    public void Plusieurs_Secrets_Sur_La_Meme_Ligne_Sont_Tous_Masques()
    {
        var (masque, compte) = SecretMasker.Masquer(
            "password=Premier123 et api_key=Deuxieme456 et client_secret=Troisieme789");

        Assert.DoesNotContain("Premier123", masque);
        Assert.DoesNotContain("Deuxieme456", masque);
        Assert.DoesNotContain("Troisieme789", masque);
        Assert.True(compte >= 3, $"{compte} secret(s) comptés au lieu de 3 au moins.");
    }

    [Fact]
    public void Un_Second_Masquage_Ne_Regonfle_Pas_Le_Compte()
    {
        // La collecte peut repasser sur un contenu deja traite. Recompter les
        // masquages ferait dire a l'ecran "14 secrets masques" sur un fichier
        // qui n'en contenait que deux.
        var (premier, compte1) = SecretMasker.Masquer("password=Secret1;api_key=Secret2");
        var (second, compte2) = SecretMasker.Masquer(premier);

        Assert.Equal(premier, second);
        Assert.True(compte1 >= 2);
        Assert.Equal(0, compte2);
    }

    [Fact]
    public void Le_Detecteur_Confirme_Qu_Il_Ne_Reste_Aucun_Secret_Apparent()
    {
        // Le filet de securite : s'il repond vrai sur du contenu deja masque,
        // c'est qu'un motif manque.
        const string journal = """
            2026-08-14 09:12:01,003 INFO  [main] Loading configuration from navis.properties
            2026-08-14 09:12:01,118 DEBUG [main] jdbc:sqlserver://SRV-N4-DB:1433;user=n4app;password=Prod!2026
            2026-08-14 09:12:01,220 DEBUG [main] {"broker":"tcp://SRV-MQ:61616","password":"MqSecret99"}
            2026-08-14 09:12:02,004 INFO  [http] Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9
            2026-08-14 09:12:02,551 WARN  [main] <password>XmlClearText</password>
            2026-08-14 09:12:03,441 INFO  [main] Web tier servlet 'action' initialized
            """;

        Assert.True(SecretMasker.ContientUnSecretApparent(journal),
            "Le journal d'origine devrait contenir des secrets apparents.");

        var (masque, compte) = SecretMasker.Masquer(journal);

        Assert.False(SecretMasker.ContientUnSecretApparent(masque),
            $"Il reste un secret apparent après masquage :\n{masque}");

        // Quatre secrets : le mot de passe JDBC, celui du JSON ActiveMQ, le
        // jeton Bearer et la balise XML. Le compte ne double PAS sur la ligne
        // JDBC, que deux motifs reconnaissent — le second voit un masquage
        // déjà posé et passe son tour.
        Assert.Equal(4, compte);

        // La ligne utile, elle, doit avoir survecu intacte.
        Assert.Contains("Web tier servlet 'action' initialized", masque);
    }

    [Fact]
    public void Le_Cas_Documente_Du_Mot_De_Passe_ECI_Est_Couvert()
    {
        // Le guide 3.8.25 documente noir sur blanc que passer
        // com.navis.control.esb en DEBUG sur le Center Node fait ecrire le mot
        // de passe de la base ECI EN CLAIR dans navis-apex.log. C'est le seul
        // cas connu ou un journal N4 contient un secret de production : il doit
        // etre masque avant d'entrer dans la base de N4 Sentinel.
        const string journal = """
            2026-08-14 09:12:01,118 DEBUG [main] c.n.c.e.m.ControlDynamicMuleConfigurer - Building config
            <spring:bean class="org.mule.jdbc.JdbcConnector">
              <spring:property name="url" value="jdbc:sqlserver://SRV-ECI:1433;databaseName=eci"/>
              <spring:property name="username" value="eci_app"/>
            </spring:bean>
            <jdbc:connector name="eciConnector" user="eci_app" password="EciProduction!2026" />
            """;

        Assert.True(SecretMasker.ContientUnSecretApparent(journal));

        var (masque, compte) = SecretMasker.Masquer(journal);

        Assert.DoesNotContain("EciProduction!2026", masque);
        Assert.False(SecretMasker.ContientUnSecretApparent(masque));
        Assert.True(compte >= 1);

        // Le contexte utile survit : on voit encore de quel connecteur il s'agit.
        Assert.Contains("eciConnector", masque);
        Assert.Contains("user=\"eci_app\"", masque);
    }

    [Fact]
    public void Un_Texte_Vide_Ne_Provoque_Aucune_Erreur()
    {
        Assert.Equal((string.Empty, 0), SecretMasker.Masquer(null));
        Assert.Equal((string.Empty, 0), SecretMasker.Masquer(string.Empty));
        Assert.False(SecretMasker.ContientUnSecretApparent(null));
    }
}
