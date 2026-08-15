using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace N4Sentinel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SuiviEdi : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SharedFolder_EdiFileNamingPattern",
                table: "Components",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SharedFolder_MaxHoursSinceLastIntegration",
                table: "Components",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "EdiFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ComponentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: false),
                    MessageType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Partner = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IntegratedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ConsecutiveRejections = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EdiFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EdiFiles_Components_ComponentId",
                        column: x => x.ComponentId,
                        principalTable: "Components",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_EdiFiles_ComponentId_FileName",
                table: "EdiFiles",
                columns: new[] { "ComponentId", "FileName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EdiFiles_ComponentId_Status",
                table: "EdiFiles",
                columns: new[] { "ComponentId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EdiFiles");

            migrationBuilder.DropColumn(
                name: "SharedFolder_EdiFileNamingPattern",
                table: "Components");

            migrationBuilder.DropColumn(
                name: "SharedFolder_MaxHoursSinceLastIntegration",
                table: "Components");
        }
    }
}
