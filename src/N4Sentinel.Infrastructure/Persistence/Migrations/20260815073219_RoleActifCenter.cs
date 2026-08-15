using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace N4Sentinel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RoleActifCenter : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // defaultValue: "[]", pas "" — cette colonne passe par le meme
            // convertisseur JSON que Readiness_ReadyPatterns et consorts ; une
            // chaine vide y est invalide et ferait echouer la desererialisation
            // au premier chargement d'un composant existant.
            migrationBuilder.AddColumn<string>(
                name: "Readiness_ActiveRolePatterns",
                table: "Components",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Readiness_ActiveRolePatterns",
                table: "Components");
        }
    }
}
