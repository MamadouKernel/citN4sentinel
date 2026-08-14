using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace N4Sentinel.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Source de temps attendue et tolérance d'écart, par environnement.
    ///
    /// NOTE. La version générée de cette migration recréait aussi les tables de
    /// diagnostic, déjà créées par la migration 20260814045928_Diagnostic : le
    /// fichier d'instantané du modèle avait été réinitialisé entre les deux, et
    /// EF a cru ces tables absentes. Elles ont été retirées d'ici — les
    /// conserver aurait fait échouer toute base déjà à jour sur « objet
    /// DiagnosticSessions déjà existant ».
    /// </summary>
    public partial class VitaliteNoeuds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExpectedTimeSource",
                table: "Environments",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            // Cinq secondes : seuil au-dela duquel N4 commence a produire des
            // statuts DISCONNECTED trompeurs. La valeur par defaut generee
            // etait 0, ce qui aurait declare tout serveur non conforme.
            migrationBuilder.AddColumn<int>(
                name: "ClockToleranceSeconds",
                table: "Environments",
                type: "int",
                nullable: false,
                defaultValue: 5);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "ExpectedTimeSource", table: "Environments");
            migrationBuilder.DropColumn(name: "ClockToleranceSeconds", table: "Environments");
        }
    }
}
