using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace N4Sentinel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ResultatSopEtCorrectionDocumentaire : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Outcome",
                table: "SopExecutions",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "OutcomeDeclaredAt",
                table: "SopExecutions",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OutcomeDeclaredBy",
                table: "SopExecutions",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OutcomeNote",
                table: "SopExecutions",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProposedCorrection",
                table: "KnowledgeFeedback",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReviewNote",
                table: "KnowledgeFeedback",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReviewStatus",
                table: "KnowledgeFeedback",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReviewedAt",
                table: "KnowledgeFeedback",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReviewedBy",
                table: "KnowledgeFeedback",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Outcome",
                table: "SopExecutions");

            migrationBuilder.DropColumn(
                name: "OutcomeDeclaredAt",
                table: "SopExecutions");

            migrationBuilder.DropColumn(
                name: "OutcomeDeclaredBy",
                table: "SopExecutions");

            migrationBuilder.DropColumn(
                name: "OutcomeNote",
                table: "SopExecutions");

            migrationBuilder.DropColumn(
                name: "ProposedCorrection",
                table: "KnowledgeFeedback");

            migrationBuilder.DropColumn(
                name: "ReviewNote",
                table: "KnowledgeFeedback");

            migrationBuilder.DropColumn(
                name: "ReviewStatus",
                table: "KnowledgeFeedback");

            migrationBuilder.DropColumn(
                name: "ReviewedAt",
                table: "KnowledgeFeedback");

            migrationBuilder.DropColumn(
                name: "ReviewedBy",
                table: "KnowledgeFeedback");
        }
    }
}
