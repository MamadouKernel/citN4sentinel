using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace N4Sentinel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ComptesNominatifs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Credentials_EnvironmentId_Reference",
                table: "Credentials");

            migrationBuilder.AddColumn<string>(
                name: "OperatingIdentityLabel",
                table: "Executions",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OperatingIdentityLogin",
                table: "Executions",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "EnvironmentId",
                table: "Credentials",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "InvalidatedAt",
                table: "Credentials",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InvalidationReason",
                table: "Credentials",
                type: "nvarchar(400)",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OwnerDisplayName",
                table: "Credentials",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OwnerLogin",
                table: "Credentials",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OwnerUserId",
                table: "Credentials",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RequiresReentry",
                table: "Credentials",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_Credentials_EnvironmentId_Reference",
                table: "Credentials",
                columns: new[] { "EnvironmentId", "Reference" },
                unique: true,
                filter: "[EnvironmentId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Credentials_OwnerLogin",
                table: "Credentials",
                column: "OwnerLogin");

            migrationBuilder.CreateIndex(
                name: "IX_Credentials_OwnerUserId",
                table: "Credentials",
                column: "OwnerUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Credentials_EnvironmentId_Reference",
                table: "Credentials");

            migrationBuilder.DropIndex(
                name: "IX_Credentials_OwnerLogin",
                table: "Credentials");

            migrationBuilder.DropIndex(
                name: "IX_Credentials_OwnerUserId",
                table: "Credentials");

            migrationBuilder.DropColumn(
                name: "OperatingIdentityLabel",
                table: "Executions");

            migrationBuilder.DropColumn(
                name: "OperatingIdentityLogin",
                table: "Executions");

            migrationBuilder.DropColumn(
                name: "InvalidatedAt",
                table: "Credentials");

            migrationBuilder.DropColumn(
                name: "InvalidationReason",
                table: "Credentials");

            migrationBuilder.DropColumn(
                name: "OwnerDisplayName",
                table: "Credentials");

            migrationBuilder.DropColumn(
                name: "OwnerLogin",
                table: "Credentials");

            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                table: "Credentials");

            migrationBuilder.DropColumn(
                name: "RequiresReentry",
                table: "Credentials");

            migrationBuilder.AlterColumn<Guid>(
                name: "EnvironmentId",
                table: "Credentials",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Credentials_EnvironmentId_Reference",
                table: "Credentials",
                columns: new[] { "EnvironmentId", "Reference" },
                unique: true);
        }
    }
}
