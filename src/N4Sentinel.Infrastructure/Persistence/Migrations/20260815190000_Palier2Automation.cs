using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace N4Sentinel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Palier2Automation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AutomationLevel",
                table: "Workflows",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "AutomationLevel",
                table: "Executions",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<bool>(
                name: "IsFallbackSemiAutoForced",
                table: "Executions",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "AutomationLevel",
                table: "Environments",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "Palier2ApprovedAt",
                table: "Environments",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Palier2ApprovedBy",
                table: "Environments",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutomationLevel",
                table: "Workflows");

            migrationBuilder.DropColumn(
                name: "AutomationLevel",
                table: "Executions");

            migrationBuilder.DropColumn(
                name: "IsFallbackSemiAutoForced",
                table: "Executions");

            migrationBuilder.DropColumn(
                name: "AutomationLevel",
                table: "Environments");

            migrationBuilder.DropColumn(
                name: "Palier2ApprovedAt",
                table: "Environments");

            migrationBuilder.DropColumn(
                name: "Palier2ApprovedBy",
                table: "Environments");
        }
    }
}
