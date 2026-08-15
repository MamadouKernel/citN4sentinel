using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace N4Sentinel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CorrelationEtReference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "ClockSkewSecondsAtCollection",
                table: "LogSources",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsReferenceBaseline",
                table: "DiagnosticSessions",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "ReferenceSessionId",
                table: "DiagnosticSessions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceAlertId",
                table: "DiagnosticSessions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_DiagnosticSessions_ReferenceSessionId",
                table: "DiagnosticSessions",
                column: "ReferenceSessionId");

            migrationBuilder.AddForeignKey(
                name: "FK_DiagnosticSessions_DiagnosticSessions_ReferenceSessionId",
                table: "DiagnosticSessions",
                column: "ReferenceSessionId",
                principalTable: "DiagnosticSessions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_DiagnosticSessions_DiagnosticSessions_ReferenceSessionId",
                table: "DiagnosticSessions");

            migrationBuilder.DropIndex(
                name: "IX_DiagnosticSessions_ReferenceSessionId",
                table: "DiagnosticSessions");

            migrationBuilder.DropColumn(
                name: "ClockSkewSecondsAtCollection",
                table: "LogSources");

            migrationBuilder.DropColumn(
                name: "IsReferenceBaseline",
                table: "DiagnosticSessions");

            migrationBuilder.DropColumn(
                name: "ReferenceSessionId",
                table: "DiagnosticSessions");

            migrationBuilder.DropColumn(
                name: "SourceAlertId",
                table: "DiagnosticSessions");
        }
    }
}
