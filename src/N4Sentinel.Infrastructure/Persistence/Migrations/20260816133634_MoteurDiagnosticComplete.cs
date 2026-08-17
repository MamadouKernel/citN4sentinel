using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace N4Sentinel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MoteurDiagnosticComplete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContentHash",
                table: "LogSources",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ValidationStatus",
                table: "DiagnosticSignatures",
                type: "int",
                nullable: false,
                defaultValue: 2); // LifecycleStatus.Valide - les signatures existantes restent actives

            migrationBuilder.AddColumn<string>(
                name: "CounterEvidence",
                table: "DiagnosticHypotheses",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EvidenceObservedAt",
                table: "DiagnosticHypotheses",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RuleVersion",
                table: "DiagnosticHypotheses",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ContentHash",
                table: "LogSources");

            migrationBuilder.DropColumn(
                name: "ValidationStatus",
                table: "DiagnosticSignatures");

            migrationBuilder.DropColumn(
                name: "CounterEvidence",
                table: "DiagnosticHypotheses");

            migrationBuilder.DropColumn(
                name: "EvidenceObservedAt",
                table: "DiagnosticHypotheses");

            migrationBuilder.DropColumn(
                name: "RuleVersion",
                table: "DiagnosticHypotheses");
        }
    }
}
