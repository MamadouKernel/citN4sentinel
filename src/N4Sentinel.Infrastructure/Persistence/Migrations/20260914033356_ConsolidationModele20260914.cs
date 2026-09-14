using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace N4Sentinel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ConsolidationModele20260914 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "QualificationNote",
                table: "Sops",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "QualifiedAt",
                table: "Sops",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "QualifiedBy",
                table: "Sops",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RevocationReason",
                table: "Sops",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RevokedAt",
                table: "Sops",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RevokedBy",
                table: "Sops",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FeedbackGivenAt",
                table: "SopExecutions",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FeedbackGivenBy",
                table: "SopExecutions",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FeedbackNote",
                table: "SopExecutions",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "FeedbackStars0To5",
                table: "SopExecutions",
                type: "tinyint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AntivirusChecked",
                table: "LogSources",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "AntivirusNote",
                table: "LogSources",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TriggeredBy",
                table: "Executions",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "TriggeredByScheduleId",
                table: "Executions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Palier2ApprovedBy",
                table: "Environments",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ResendRequestedAt",
                table: "EdiFiles",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResendRequestedBy",
                table: "EdiFiles",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AllowTokenPastedLogin",
                table: "AzureAdSettings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "RequireSignatureValidation",
                table: "AzureAdSettings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "RoleMappings",
                table: "AzureAdSettings",
                type: "nvarchar(max)",
                nullable: false,
                defaultValueSql: "'[]'");

            migrationBuilder.CreateTable(
                name: "ExecutionSchedules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkflowId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    TimeOfDay = table.Column<TimeSpan>(type: "time", nullable: false),
                    WeeklyDayOfWeekMask = table.Column<int>(type: "int", nullable: false),
                    MonthlyDays = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CronExpression = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    RanAs = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ReasonTemplate = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TicketTemplate = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AutoApprove = table.Column<bool>(type: "bit", nullable: false),
                    ValidFrom = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ValidUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastTriggeredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    NextPlannedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SkipIfActiveAlready = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "varbinary(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExecutionSchedules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExecutionSchedules_Environments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "Environments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ExecutionSchedules_Workflows_WorkflowId",
                        column: x => x.WorkflowId,
                        principalTable: "Workflows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.NoAction);
                });

            migrationBuilder.CreateTable(
                name: "JmxEndpoints",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    EndpointType = table.Column<byte>(type: "tinyint", nullable: false),
                    Host = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Port = table.Column<int>(type: "int", nullable: false),
                    JolokiaBaseUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CredentialId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    LastReachableAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastFailureMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    LastPooledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastSuccessAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastErrorAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastErrorMessage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JmxEndpoints", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MaintenanceHolidays",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "varbinary(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaintenanceHolidays", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MaintenanceHolidays_Environments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "Environments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Tickets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    Priority = table.Column<byte>(type: "tinyint", nullable: false),
                    Severity = table.Column<byte>(type: "tinyint", nullable: false),
                    Source = table.Column<byte>(type: "tinyint", nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DiagnosticSignatureId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OperationCorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    AprId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AssignedTo = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ExternalTicketId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    OpenedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ClosedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ClosureReason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    OpenedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tickets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Tickets_Environments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "Environments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "TicketComments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WrittenBy = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Author = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Body = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Text = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    WrittenAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IsInternal = table.Column<bool>(type: "bit", nullable: false),
                    Source = table.Column<byte>(type: "tinyint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketComments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TicketComments_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionSchedules_EnvironmentId",
                table: "ExecutionSchedules",
                column: "EnvironmentId");

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionSchedules_WorkflowId",
                table: "ExecutionSchedules",
                column: "WorkflowId");

            migrationBuilder.CreateIndex(
                name: "IX_JmxEndpoints_EndpointType",
                table: "JmxEndpoints",
                column: "EndpointType");

            migrationBuilder.CreateIndex(
                name: "IX_JmxEndpoints_Host_Port",
                table: "JmxEndpoints",
                columns: new[] { "Host", "Port" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceHolidays_EnvironmentId",
                table: "MaintenanceHolidays",
                column: "EnvironmentId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketComments_TicketId_WrittenAt",
                table: "TicketComments",
                columns: new[] { "TicketId", "WrittenAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_Code",
                table: "Tickets",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_EnvironmentId",
                table: "Tickets",
                column: "EnvironmentId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_OpenedAt",
                table: "Tickets",
                column: "OpenedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_Status_Priority",
                table: "Tickets",
                columns: new[] { "Status", "Priority" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExecutionSchedules");

            migrationBuilder.DropTable(
                name: "JmxEndpoints");

            migrationBuilder.DropTable(
                name: "MaintenanceHolidays");

            migrationBuilder.DropTable(
                name: "TicketComments");

            migrationBuilder.DropTable(
                name: "Tickets");

            migrationBuilder.DropColumn(
                name: "QualificationNote",
                table: "Sops");

            migrationBuilder.DropColumn(
                name: "QualifiedAt",
                table: "Sops");

            migrationBuilder.DropColumn(
                name: "QualifiedBy",
                table: "Sops");

            migrationBuilder.DropColumn(
                name: "RevocationReason",
                table: "Sops");

            migrationBuilder.DropColumn(
                name: "RevokedAt",
                table: "Sops");

            migrationBuilder.DropColumn(
                name: "RevokedBy",
                table: "Sops");

            migrationBuilder.DropColumn(
                name: "FeedbackGivenAt",
                table: "SopExecutions");

            migrationBuilder.DropColumn(
                name: "FeedbackGivenBy",
                table: "SopExecutions");

            migrationBuilder.DropColumn(
                name: "FeedbackNote",
                table: "SopExecutions");

            migrationBuilder.DropColumn(
                name: "FeedbackStars0To5",
                table: "SopExecutions");

            migrationBuilder.DropColumn(
                name: "AntivirusChecked",
                table: "LogSources");

            migrationBuilder.DropColumn(
                name: "AntivirusNote",
                table: "LogSources");

            migrationBuilder.DropColumn(
                name: "TriggeredBy",
                table: "Executions");

            migrationBuilder.DropColumn(
                name: "TriggeredByScheduleId",
                table: "Executions");

            migrationBuilder.DropColumn(
                name: "ResendRequestedAt",
                table: "EdiFiles");

            migrationBuilder.DropColumn(
                name: "ResendRequestedBy",
                table: "EdiFiles");

            migrationBuilder.DropColumn(
                name: "AllowTokenPastedLogin",
                table: "AzureAdSettings");

            migrationBuilder.DropColumn(
                name: "RequireSignatureValidation",
                table: "AzureAdSettings");

            migrationBuilder.DropColumn(
                name: "RoleMappings",
                table: "AzureAdSettings");

            migrationBuilder.AlterColumn<string>(
                name: "Palier2ApprovedBy",
                table: "Environments",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(200)",
                oldMaxLength: 200,
                oldNullable: true);
        }
    }
}
