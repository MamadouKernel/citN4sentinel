using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace N4Sentinel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SopActionSensible : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // NOTE : les colonnes Workflows.AutomationLevel, Executions.AutomationLevel
            // et Executions.IsFallbackSemiAutoForced existaient deja en base (ajoutees
            // hors migration EF lors d'un travail anterieur non lie a ce correctif) au
            // moment ou cette migration a ete generee. Scaffolding retire pour ne pas
            // echouer sur "Column ... specified more than once" ; seule l'evolution
            // realisee ici (Sops.RequiresElevatedRole + alignement des colonnes
            // Palier2Approved*) est appliquee.

            migrationBuilder.AddColumn<bool>(
                name: "RequiresElevatedRole",
                table: "Sops",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AlterColumn<string>(
                name: "Palier2ApprovedBy",
                table: "Environments",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(200)",
                oldMaxLength: 200,
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTime>(
                name: "Palier2ApprovedAt",
                table: "Environments",
                type: "datetime2",
                nullable: true,
                oldClrType: typeof(DateTimeOffset),
                oldType: "datetimeoffset",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RequiresElevatedRole",
                table: "Sops");

            migrationBuilder.AlterColumn<string>(
                name: "Palier2ApprovedBy",
                table: "Environments",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "Palier2ApprovedAt",
                table: "Environments",
                type: "datetimeoffset",
                nullable: true,
                oldClrType: typeof(DateTime),
                oldType: "datetime2",
                oldNullable: true);
        }
    }
}
