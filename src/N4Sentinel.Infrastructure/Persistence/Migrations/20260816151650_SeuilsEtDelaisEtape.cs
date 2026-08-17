using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace N4Sentinel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SeuilsEtDelaisEtape : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RetryDelaySeconds",
                table: "WorkflowSteps",
                type: "int",
                nullable: false,
                defaultValue: 30); // WorkflowStep.RetryDelaySeconds

            migrationBuilder.AddColumn<int>(
                name: "WarningThresholdSeconds",
                table: "WorkflowSteps",
                type: "int",
                nullable: false,
                defaultValue: 120); // WorkflowStep.WarningThresholdSeconds

            migrationBuilder.AddColumn<int>(
                name: "RetryDelaySeconds",
                table: "ExecutionSteps",
                type: "int",
                nullable: false,
                defaultValue: 30); // ExecutionStep.RetryDelaySeconds

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RetryNotBeforeAt",
                table: "ExecutionSteps",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WarningThresholdSeconds",
                table: "ExecutionSteps",
                type: "int",
                nullable: false,
                defaultValue: 120); // ExecutionStep.WarningThresholdSeconds
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RetryDelaySeconds",
                table: "WorkflowSteps");

            migrationBuilder.DropColumn(
                name: "WarningThresholdSeconds",
                table: "WorkflowSteps");

            migrationBuilder.DropColumn(
                name: "RetryDelaySeconds",
                table: "ExecutionSteps");

            migrationBuilder.DropColumn(
                name: "RetryNotBeforeAt",
                table: "ExecutionSteps");

            migrationBuilder.DropColumn(
                name: "WarningThresholdSeconds",
                table: "ExecutionSteps");
        }
    }
}
