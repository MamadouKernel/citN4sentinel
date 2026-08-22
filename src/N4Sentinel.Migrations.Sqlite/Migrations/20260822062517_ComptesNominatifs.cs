using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace N4Sentinel.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class ComptesNominatifs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OperatingIdentityLogin",
                table: "Executions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "EnvironmentId",
                table: "Credentials",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AddColumn<long>(
                name: "InvalidatedAt",
                table: "Credentials",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InvalidationReason",
                table: "Credentials",
                type: "TEXT",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OwnerDisplayName",
                table: "Credentials",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OwnerLogin",
                table: "Credentials",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OwnerUserId",
                table: "Credentials",
                type: "TEXT",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RequiresReentry",
                table: "Credentials",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

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
                name: "IX_Credentials_OwnerLogin",
                table: "Credentials");

            migrationBuilder.DropIndex(
                name: "IX_Credentials_OwnerUserId",
                table: "Credentials");

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
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);
        }
    }
}
