using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace N4Sentinel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FR071_SuggestionOrigineJournal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "OriginAmbiguous",
                table: "LogSources",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "SuggestedComponentId",
                table: "LogSources",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SuggestedComponentName",
                table: "LogSources",
                type: "nvarchar(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SuggestionEvidence",
                table: "LogSources",
                type: "nvarchar(600)",
                maxLength: 600,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OriginAmbiguous",
                table: "LogSources");

            migrationBuilder.DropColumn(
                name: "SuggestedComponentId",
                table: "LogSources");

            migrationBuilder.DropColumn(
                name: "SuggestedComponentName",
                table: "LogSources");

            migrationBuilder.DropColumn(
                name: "SuggestionEvidence",
                table: "LogSources");
        }
    }
}
