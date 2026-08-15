using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace N4Sentinel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class IntroductionSop : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Sops",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    HasBeenExecuted = table.Column<bool>(type: "bit", nullable: false),
                    Objective = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Scope = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Prerequisites = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Risks = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Controls = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ExpectedOutcome = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    RollbackPlan = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    EscalationPath = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    AppliesToVersion = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    SourceDocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SourceExecutionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sops", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Sops_Environments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "Environments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SopAssociations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SopId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    ComponentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Role = table.Column<int>(type: "int", nullable: true),
                    SignatureId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SignatureCode = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    DiagnosticSessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SopAssociations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SopAssociations_Sops_SopId",
                        column: x => x.SopId,
                        principalTable: "Sops",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SopExecutions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SopId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SopVersion = table.Column<int>(type: "int", nullable: false),
                    SopCode = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    SopTitle = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EnvironmentCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    StartedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    TicketReference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    SourceAlertId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SourceDiagnosticSessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    AbandonReason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SopExecutions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SopExecutions_Environments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "Environments",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SopExecutions_Sops_SopId",
                        column: x => x.SopId,
                        principalTable: "Sops",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "SopSteps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SopId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Order = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Instruction = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    ExpectedResult = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ComponentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsSkippable = table.Column<bool>(type: "bit", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SopSteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SopSteps_Components_ComponentId",
                        column: x => x.ComponentId,
                        principalTable: "Components",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SopSteps_Sops_SopId",
                        column: x => x.SopId,
                        principalTable: "Sops",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SopExecutionSteps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SopExecutionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Order = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Instruction = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    ExpectedResult = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ComponentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ComponentName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    State = table.Column<int>(type: "int", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    EndedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    IsSkippable = table.Column<bool>(type: "bit", nullable: false),
                    ConfirmedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ConfirmedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Evidence = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    DeviationNote = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    SkippedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    SkipReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    History = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SopExecutionSteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SopExecutionSteps_SopExecutions_SopExecutionId",
                        column: x => x.SopExecutionId,
                        principalTable: "SopExecutions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SopAssociations_ComponentId_Kind",
                table: "SopAssociations",
                columns: new[] { "ComponentId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_SopAssociations_SignatureId",
                table: "SopAssociations",
                column: "SignatureId");

            migrationBuilder.CreateIndex(
                name: "IX_SopAssociations_SopId",
                table: "SopAssociations",
                column: "SopId");

            migrationBuilder.CreateIndex(
                name: "IX_SopExecutions_CorrelationId",
                table: "SopExecutions",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_SopExecutions_EnvironmentId_Status",
                table: "SopExecutions",
                columns: new[] { "EnvironmentId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_SopExecutions_SopId",
                table: "SopExecutions",
                column: "SopId");

            migrationBuilder.CreateIndex(
                name: "IX_SopExecutions_StartedAt",
                table: "SopExecutions",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_SopExecutionSteps_SopExecutionId_Order",
                table: "SopExecutionSteps",
                columns: new[] { "SopExecutionId", "Order" });

            migrationBuilder.CreateIndex(
                name: "IX_Sops_EnvironmentId_Code_Version",
                table: "Sops",
                columns: new[] { "EnvironmentId", "Code", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SopSteps_ComponentId",
                table: "SopSteps",
                column: "ComponentId");

            migrationBuilder.CreateIndex(
                name: "IX_SopSteps_SopId_Order",
                table: "SopSteps",
                columns: new[] { "SopId", "Order" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SopAssociations");

            migrationBuilder.DropTable(
                name: "SopExecutionSteps");

            migrationBuilder.DropTable(
                name: "SopSteps");

            migrationBuilder.DropTable(
                name: "SopExecutions");

            migrationBuilder.DropTable(
                name: "Sops");
        }
    }
}
