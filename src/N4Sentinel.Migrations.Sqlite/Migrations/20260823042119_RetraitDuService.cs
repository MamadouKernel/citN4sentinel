using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace N4Sentinel.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class RetraitDuService : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Servers_EnvironmentId_HostName",
                table: "Servers");

            migrationBuilder.DropIndex(
                name: "IX_Environments_Code",
                table: "Environments");

            migrationBuilder.DropIndex(
                name: "IX_Components_EnvironmentId_LogicalName",
                table: "Components");

            migrationBuilder.AddColumn<long>(
                name: "DeletedAt",
                table: "Servers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletedBy",
                table: "Servers",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DeletionBatchId",
                table: "Servers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletionReason",
                table: "Servers",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "DeletedAt",
                table: "Environments",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletedBy",
                table: "Environments",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DeletionBatchId",
                table: "Environments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletionReason",
                table: "Environments",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "DeletedAt",
                table: "Components",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletedBy",
                table: "Components",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DeletionBatchId",
                table: "Components",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletionReason",
                table: "Components",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Servers_DeletedAt",
                table: "Servers",
                column: "DeletedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Servers_EnvironmentId_HostName",
                table: "Servers",
                columns: new[] { "EnvironmentId", "HostName" },
                unique: true,
                filter: "\"DeletedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Environments_Code",
                table: "Environments",
                column: "Code",
                unique: true,
                filter: "\"DeletedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Environments_DeletedAt",
                table: "Environments",
                column: "DeletedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Components_DeletedAt",
                table: "Components",
                column: "DeletedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Components_EnvironmentId_LogicalName",
                table: "Components",
                columns: new[] { "EnvironmentId", "LogicalName" },
                unique: true,
                filter: "\"DeletedAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Servers_DeletedAt",
                table: "Servers");

            migrationBuilder.DropIndex(
                name: "IX_Servers_EnvironmentId_HostName",
                table: "Servers");

            migrationBuilder.DropIndex(
                name: "IX_Environments_Code",
                table: "Environments");

            migrationBuilder.DropIndex(
                name: "IX_Environments_DeletedAt",
                table: "Environments");

            migrationBuilder.DropIndex(
                name: "IX_Components_DeletedAt",
                table: "Components");

            migrationBuilder.DropIndex(
                name: "IX_Components_EnvironmentId_LogicalName",
                table: "Components");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "Servers");

            migrationBuilder.DropColumn(
                name: "DeletedBy",
                table: "Servers");

            migrationBuilder.DropColumn(
                name: "DeletionBatchId",
                table: "Servers");

            migrationBuilder.DropColumn(
                name: "DeletionReason",
                table: "Servers");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "Environments");

            migrationBuilder.DropColumn(
                name: "DeletedBy",
                table: "Environments");

            migrationBuilder.DropColumn(
                name: "DeletionBatchId",
                table: "Environments");

            migrationBuilder.DropColumn(
                name: "DeletionReason",
                table: "Environments");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "Components");

            migrationBuilder.DropColumn(
                name: "DeletedBy",
                table: "Components");

            migrationBuilder.DropColumn(
                name: "DeletionBatchId",
                table: "Components");

            migrationBuilder.DropColumn(
                name: "DeletionReason",
                table: "Components");

            migrationBuilder.CreateIndex(
                name: "IX_Servers_EnvironmentId_HostName",
                table: "Servers",
                columns: new[] { "EnvironmentId", "HostName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Environments_Code",
                table: "Environments",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Components_EnvironmentId_LogicalName",
                table: "Components",
                columns: new[] { "EnvironmentId", "LogicalName" },
                unique: true);
        }
    }
}
