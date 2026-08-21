using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace N4Sentinel.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class ServicesAssocies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CompanionServiceNames",
                table: "Components",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CompanionServiceNames",
                table: "Components");
        }
    }
}
