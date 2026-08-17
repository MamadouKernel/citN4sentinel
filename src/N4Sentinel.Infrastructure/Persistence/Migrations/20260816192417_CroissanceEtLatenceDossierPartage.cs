using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace N4Sentinel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CroissanceEtLatenceDossierPartage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "GrowthBytesPerHour",
                table: "SharedFolderSnapshots",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HealthWarnings",
                table: "SharedFolderSnapshots",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<double>(
                name: "WriteLatencyMs",
                table: "SharedFolderSnapshots",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SharedFolder_MaxGrowthBytesPerHour",
                table: "Components",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SharedFolder_MaxWriteLatencyMs",
                table: "Components",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GrowthBytesPerHour",
                table: "SharedFolderSnapshots");

            migrationBuilder.DropColumn(
                name: "HealthWarnings",
                table: "SharedFolderSnapshots");

            migrationBuilder.DropColumn(
                name: "WriteLatencyMs",
                table: "SharedFolderSnapshots");

            migrationBuilder.DropColumn(
                name: "SharedFolder_MaxGrowthBytesPerHour",
                table: "Components");

            migrationBuilder.DropColumn(
                name: "SharedFolder_MaxWriteLatencyMs",
                table: "Components");
        }
    }
}
