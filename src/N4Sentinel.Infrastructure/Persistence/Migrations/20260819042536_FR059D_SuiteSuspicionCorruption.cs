using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace N4Sentinel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FR059D_SuiteSuspicionCorruption : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CorruptionConclusion",
                table: "SharedFolderSnapshots",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CorruptionConfirmed",
                table: "SharedFolderSnapshots",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SopExecutionId",
                table: "SharedFolderSnapshots",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SharedFolderSnapshots_CorruptionConfirmed",
                table: "SharedFolderSnapshots",
                column: "CorruptionConfirmed");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SharedFolderSnapshots_CorruptionConfirmed",
                table: "SharedFolderSnapshots");

            migrationBuilder.DropColumn(
                name: "CorruptionConclusion",
                table: "SharedFolderSnapshots");

            migrationBuilder.DropColumn(
                name: "CorruptionConfirmed",
                table: "SharedFolderSnapshots");

            migrationBuilder.DropColumn(
                name: "SopExecutionId",
                table: "SharedFolderSnapshots");
        }
    }
}
