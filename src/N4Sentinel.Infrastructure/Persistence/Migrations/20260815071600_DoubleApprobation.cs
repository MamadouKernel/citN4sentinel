using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace N4Sentinel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DoubleApprobation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RequiresDoubleApproval",
                table: "Workflows",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "RequiresDoubleApproval",
                table: "Executions",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SecondApprovedAt",
                table: "Executions",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SecondApprovedBy",
                table: "Executions",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RequiresDoubleApproval",
                table: "Workflows");

            migrationBuilder.DropColumn(
                name: "RequiresDoubleApproval",
                table: "Executions");

            migrationBuilder.DropColumn(
                name: "SecondApprovedAt",
                table: "Executions");

            migrationBuilder.DropColumn(
                name: "SecondApprovedBy",
                table: "Executions");
        }
    }
}
