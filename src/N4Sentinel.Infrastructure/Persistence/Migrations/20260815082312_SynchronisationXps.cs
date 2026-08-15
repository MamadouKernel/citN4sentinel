using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace N4Sentinel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SynchronisationXps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Readiness_SyncDelayThresholdMinutes",
                table: "Components",
                type: "int",
                nullable: false,
                // Aligne les lignes existantes sur le defaut C# (15) : sans
                // cela, une ligne anterieure a cette migration se retrouverait
                // a 0 minute des qu'un administrateur y configurerait des
                // SyncPatterns, ce qui la declarerait en retard en permanence.
                defaultValue: 15);

            // defaultValue: "[]", pas "" — cette colonne passe par le meme
            // convertisseur JSON que les autres listes de motifs ; une chaine
            // vide y est invalide et ferait echouer la deserialisation au
            // premier chargement d'un composant existant.
            migrationBuilder.AddColumn<string>(
                name: "Readiness_SyncPatterns",
                table: "Components",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Readiness_SyncDelayThresholdMinutes",
                table: "Components");

            migrationBuilder.DropColumn(
                name: "Readiness_SyncPatterns",
                table: "Components");
        }
    }
}
