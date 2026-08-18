using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace N4Sentinel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PhaseV_OrchestrationComplete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EndWindow",
                table: "Executions",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<TimeSpan>(
                name: "EstimatedTotalDuration",
                table: "Executions",
                type: "time",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "StartWindow",
                table: "Executions",
                type: "datetimeoffset",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EndWindow",
                table: "Executions");

            migrationBuilder.DropColumn(
                name: "EstimatedTotalDuration",
                table: "Executions");

            migrationBuilder.DropColumn(
                name: "StartWindow",
                table: "Executions");
        }
    }
}
