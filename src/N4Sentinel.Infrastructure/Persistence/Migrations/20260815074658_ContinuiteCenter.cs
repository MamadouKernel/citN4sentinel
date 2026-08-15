using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace N4Sentinel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ContinuiteCenter : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ContinuityChoice",
                table: "Executions",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ContinuityChoiceAt",
                table: "Executions",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContinuityChoiceBy",
                table: "Executions",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ContinuityChoiceRequired",
                table: "Executions",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ContinuityChoice",
                table: "Executions");

            migrationBuilder.DropColumn(
                name: "ContinuityChoiceAt",
                table: "Executions");

            migrationBuilder.DropColumn(
                name: "ContinuityChoiceBy",
                table: "Executions");

            migrationBuilder.DropColumn(
                name: "ContinuityChoiceRequired",
                table: "Executions");
        }
    }
}
