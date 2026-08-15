using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace N4Sentinel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DossiersPartages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SharedFolder_BlockedSubfolder",
                table: "Components",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SharedFolder_Category",
                table: "Components",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SharedFolder_ConsumedSubfolder",
                table: "Components",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SharedFolder_ErrorSubfolder",
                table: "Components",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SharedFolder_MaxPendingAgeHours",
                table: "Components",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SharedFolder_PendingSubfolder",
                table: "Components",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SharedFolder_RootPath",
                table: "Components",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SharedFolderSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ComponentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CapturedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Reachable = table.Column<bool>(type: "bit", nullable: true),
                    UnreachableReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    TotalFileCount = table.Column<int>(type: "int", nullable: false),
                    TotalSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    OldestFileAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    NewestFileAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PendingCount = table.Column<int>(type: "int", nullable: true),
                    ConsumedCount = table.Column<int>(type: "int", nullable: true),
                    BlockedCount = table.Column<int>(type: "int", nullable: true),
                    ErrorCount = table.Column<int>(type: "int", nullable: true),
                    OldestPendingAgeHours = table.Column<double>(type: "float", nullable: true),
                    CanWrite = table.Column<bool>(type: "bit", nullable: true),
                    MandatoryFilesPresent = table.Column<bool>(type: "bit", nullable: true),
                    MissingMandatoryFiles = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CorruptionIndicators = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SharedFolderSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SharedFolderSnapshots_Components_ComponentId",
                        column: x => x.ComponentId,
                        principalTable: "Components",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_SharedFolderSnapshots_ComponentId_CapturedAt",
                table: "SharedFolderSnapshots",
                columns: new[] { "ComponentId", "CapturedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SharedFolderSnapshots");

            migrationBuilder.DropColumn(
                name: "SharedFolder_BlockedSubfolder",
                table: "Components");

            migrationBuilder.DropColumn(
                name: "SharedFolder_Category",
                table: "Components");

            migrationBuilder.DropColumn(
                name: "SharedFolder_ConsumedSubfolder",
                table: "Components");

            migrationBuilder.DropColumn(
                name: "SharedFolder_ErrorSubfolder",
                table: "Components");

            migrationBuilder.DropColumn(
                name: "SharedFolder_MaxPendingAgeHours",
                table: "Components");

            migrationBuilder.DropColumn(
                name: "SharedFolder_PendingSubfolder",
                table: "Components");

            migrationBuilder.DropColumn(
                name: "SharedFolder_RootPath",
                table: "Components");
        }
    }
}
