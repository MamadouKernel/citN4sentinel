using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace N4Sentinel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EnrichissementSignaturesEtConstats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LoggerClass",
                table: "LogFindings",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ThreadName",
                table: "LogFindings",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TransactionId",
                table: "LogFindings",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CounterEvidence",
                table: "DiagnosticSignatures",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "DiagnosticSignatures",
                type: "int",
                nullable: false,
                defaultValue: 1);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LoggerClass",
                table: "LogFindings");

            migrationBuilder.DropColumn(
                name: "ThreadName",
                table: "LogFindings");

            migrationBuilder.DropColumn(
                name: "TransactionId",
                table: "LogFindings");

            migrationBuilder.DropColumn(
                name: "CounterEvidence",
                table: "DiagnosticSignatures");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "DiagnosticSignatures");
        }
    }
}
