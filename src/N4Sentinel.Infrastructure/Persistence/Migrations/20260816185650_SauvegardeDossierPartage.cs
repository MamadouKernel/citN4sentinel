using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace N4Sentinel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SauvegardeDossierPartage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SharedFolder_LastBackupAt",
                table: "Components",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SharedFolder_LastBackupBy",
                table: "Components",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SharedFolder_LastBackupNote",
                table: "Components",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SharedFolder_LastBackupAt",
                table: "Components");

            migrationBuilder.DropColumn(
                name: "SharedFolder_LastBackupBy",
                table: "Components");

            migrationBuilder.DropColumn(
                name: "SharedFolder_LastBackupNote",
                table: "Components");
        }
    }
}
