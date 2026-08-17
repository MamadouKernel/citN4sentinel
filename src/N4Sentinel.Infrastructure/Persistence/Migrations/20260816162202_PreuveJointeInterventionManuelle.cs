using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace N4Sentinel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PreuveJointeInterventionManuelle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RequiresEvidenceFile",
                table: "WorkflowSteps",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<byte[]>(
                name: "EvidenceFileContent",
                table: "ExecutionSteps",
                type: "varbinary(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EvidenceFileContentType",
                table: "ExecutionSteps",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EvidenceFileName",
                table: "ExecutionSteps",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RequiresEvidenceFile",
                table: "ExecutionSteps",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RequiresEvidenceFile",
                table: "WorkflowSteps");

            migrationBuilder.DropColumn(
                name: "EvidenceFileContent",
                table: "ExecutionSteps");

            migrationBuilder.DropColumn(
                name: "EvidenceFileContentType",
                table: "ExecutionSteps");

            migrationBuilder.DropColumn(
                name: "EvidenceFileName",
                table: "ExecutionSteps");

            migrationBuilder.DropColumn(
                name: "RequiresEvidenceFile",
                table: "ExecutionSteps");
        }
    }
}
