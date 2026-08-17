using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace N4Sentinel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TaxonomieEchecEtDetectionJournal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CorrelationId",
                table: "LogSources",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DetectedLogType",
                table: "LogSources",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DetectedTimeZone",
                table: "LogSources",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FailureReason",
                table: "LogSources",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CorrelationId",
                table: "ComponentSignals",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CorrelationId",
                table: "LogSources");

            migrationBuilder.DropColumn(
                name: "DetectedLogType",
                table: "LogSources");

            migrationBuilder.DropColumn(
                name: "DetectedTimeZone",
                table: "LogSources");

            migrationBuilder.DropColumn(
                name: "FailureReason",
                table: "LogSources");

            migrationBuilder.DropColumn(
                name: "CorrelationId",
                table: "ComponentSignals");
        }
    }
}
