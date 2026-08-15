using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace N4Sentinel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ResumeEtIdentificationLogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ComponentAutoDetected",
                table: "LogSources",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "DetectedVersion",
                table: "LogSources",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EarliestEntryAt",
                table: "LogSources",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ErrorCount",
                table: "LogSources",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "InfoCount",
                table: "LogSources",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LatestEntryAt",
                table: "LogSources",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WarningCount",
                table: "LogSources",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ComponentAutoDetected",
                table: "LogSources");

            migrationBuilder.DropColumn(
                name: "DetectedVersion",
                table: "LogSources");

            migrationBuilder.DropColumn(
                name: "EarliestEntryAt",
                table: "LogSources");

            migrationBuilder.DropColumn(
                name: "ErrorCount",
                table: "LogSources");

            migrationBuilder.DropColumn(
                name: "InfoCount",
                table: "LogSources");

            migrationBuilder.DropColumn(
                name: "LatestEntryAt",
                table: "LogSources");

            migrationBuilder.DropColumn(
                name: "WarningCount",
                table: "LogSources");
        }
    }
}
