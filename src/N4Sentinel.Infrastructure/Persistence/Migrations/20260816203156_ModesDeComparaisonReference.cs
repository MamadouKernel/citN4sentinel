using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace N4Sentinel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ModesDeComparaisonReference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ReferenceComponentId",
                table: "DiagnosticSessions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReferenceExecutionId",
                table: "DiagnosticSessions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReferenceKind",
                table: "DiagnosticSessions",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReferenceComponentId",
                table: "DiagnosticSessions");

            migrationBuilder.DropColumn(
                name: "ReferenceExecutionId",
                table: "DiagnosticSessions");

            migrationBuilder.DropColumn(
                name: "ReferenceKind",
                table: "DiagnosticSessions");
        }
    }
}
