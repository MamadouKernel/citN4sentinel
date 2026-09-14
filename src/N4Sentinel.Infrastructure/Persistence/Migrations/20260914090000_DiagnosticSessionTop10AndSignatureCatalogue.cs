using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace N4Sentinel.Infrastructure.Persistence.Migrations;

/// <summary>
/// Ajoute à DiagnosticSessions les 4 colonnes déjà présentes dans le modèle
/// mais absentes du schéma (d'où l'erreur « Invalid column name » sur l'écran
/// Diagnostics) :
///   - RG-0307 : revue de la checklist Top 10 causes P1 Navis
///       (Top10ChecklistCheckedCodesCsv + Top10ChecklistNotes) ;
///   - P4.6 : instantané du catalogue de signatures au moment de l'analyse
///       (SignatureCatalogueVersionAtAnalysis + ActiveSignaturesCountAtAnalysis).
/// Toutes nullables, non configurées explicitement dans le modèle → nvarchar(max)
/// et int par convention.
/// </summary>
[DbContext(typeof(N4SentinelDbContext))]
[Migration("20260914090000_DiagnosticSessionTop10AndSignatureCatalogue")]
public sealed class DiagnosticSessionTop10AndSignatureCatalogue : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Top10ChecklistCheckedCodesCsv",
            table: "DiagnosticSessions",
            type: "nvarchar(max)",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Top10ChecklistNotes",
            table: "DiagnosticSessions",
            type: "nvarchar(max)",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "SignatureCatalogueVersionAtAnalysis",
            table: "DiagnosticSessions",
            type: "nvarchar(max)",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "ActiveSignaturesCountAtAnalysis",
            table: "DiagnosticSessions",
            type: "int",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "Top10ChecklistCheckedCodesCsv",
            table: "DiagnosticSessions");

        migrationBuilder.DropColumn(
            name: "Top10ChecklistNotes",
            table: "DiagnosticSessions");

        migrationBuilder.DropColumn(
            name: "SignatureCatalogueVersionAtAnalysis",
            table: "DiagnosticSessions");

        migrationBuilder.DropColumn(
            name: "ActiveSignaturesCountAtAnalysis",
            table: "DiagnosticSessions");
    }
}
