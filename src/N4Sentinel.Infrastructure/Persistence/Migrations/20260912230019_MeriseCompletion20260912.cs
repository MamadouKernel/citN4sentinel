using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace N4Sentinel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MeriseCompletion20260912 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BusinessSuccessMetrics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReportingPeriod = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    MetricCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    MetricLabel = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NumericValueAvgOrSum = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    TargetFixedValue = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    BaselineBeforeSentinel = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    TrendVsPreviousPeriodPct = table.Column<decimal>(type: "decimal(8,2)", precision: 8, scale: 2, nullable: true),
                    AggregationKind = table.Column<int>(type: "int", nullable: false),
                    CalculatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SampleSizeCount = table.Column<long>(type: "bigint", nullable: true),
                    IsTargetMissed = table.Column<bool>(type: "bit", nullable: false),
                    NarrativeComment = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BusinessSuccessMetrics", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BusinessSuccessMetrics_Environments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "Environments",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "CapitalizedIncidents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExternalReference = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeclaredSeverity = table.Column<int>(type: "int", nullable: false),
                    InitialSymptom = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    RealIncidentStartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RealIncidentEndedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    TotalDurationMinutes = table.Column<long>(type: "bigint", nullable: true),
                    DetectedBySentinelAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    MainRootCauseCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    MainRootCauseLabel = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    FiveWhysRootCause = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: true),
                    CorrectiveActionsTaken = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: true),
                    LinkedDiagnosticSessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LinkedExecutionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SOPsUsedListString = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    GeneratedSOPId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    QuantifiedBusinessImpact = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CompensatoryMeasures = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    OperatorsInvolvedCount = table.Column<int>(type: "int", nullable: false),
                    ResolutionSkillLevel = table.Column<int>(type: "int", nullable: true),
                    ClosedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    ClosedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PostMortemDocumentRef = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    LessonsAndCorrectives = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CapitalizedIncidents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CapitalizedIncidents_DiagnosticSessions_LinkedDiagnosticSessionId",
                        column: x => x.LinkedDiagnosticSessionId,
                        principalTable: "DiagnosticSessions",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CapitalizedIncidents_Environments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "Environments",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CapitalizedIncidents_Executions_LinkedExecutionId",
                        column: x => x.LinkedExecutionId,
                        principalTable: "Executions",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CapitalizedIncidents_Sops_GeneratedSOPId",
                        column: x => x.GeneratedSOPId,
                        principalTable: "Sops",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ComponentAssociatedServices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ComponentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ServiceName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    ExpectedStatus = table.Column<int>(type: "int", nullable: false),
                    ExpectedPort = table.Column<int>(type: "int", nullable: true),
                    ExpectedServiceAccount = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    TimeoutSecondsOverride = table.Column<int>(type: "int", nullable: true),
                    IsMandatory = table.Column<bool>(type: "bit", nullable: false),
                    LastObservedPid = table.Column<int>(type: "int", nullable: true),
                    LastObservedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastObservedStatus = table.Column<int>(type: "int", nullable: true),
                    LastObservationNote = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Order = table.Column<int>(type: "int", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComponentAssociatedServices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ComponentAssociatedServices_Components_ComponentId",
                        column: x => x.ComponentId,
                        principalTable: "Components",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PreventiveRecommendedActions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceComponentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Code = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    RootCauseFamily = table.Column<int>(type: "int", nullable: false),
                    EstimatedP1RiskPct = table.Column<decimal>(type: "decimal(8,2)", precision: 8, scale: 2, nullable: false),
                    EstimatedDurationMinutes = table.Column<int>(type: "int", nullable: false),
                    PriorityScore = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    APRStatus = table.Column<int>(type: "int", nullable: false),
                    FirstWeakSignalAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    GeneratedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EstimatedDeadlineForAction = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ScheduledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ScheduledBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    LaunchWorkflowId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ResultingExecutionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ExecutedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ExecutedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DismissedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    DismissedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DismissalReason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    IsBasedOnTrend = table.Column<bool>(type: "bit", nullable: false),
                    EvidenceSummary = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    RawSignalSnapshot = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PreventiveRecommendedActions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PreventiveRecommendedActions_Components_SourceComponentId",
                        column: x => x.SourceComponentId,
                        principalTable: "Components",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_PreventiveRecommendedActions_Environments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "Environments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PreventiveRecommendedActions_Executions_ResultingExecutionId",
                        column: x => x.ResultingExecutionId,
                        principalTable: "Executions",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_PreventiveRecommendedActions_Workflows_LaunchWorkflowId",
                        column: x => x.LaunchWorkflowId,
                        principalTable: "Workflows",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "WorkflowPreChecks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkflowId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Label = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Category = table.Column<int>(type: "int", nullable: false),
                    TargetComponentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    ControlInstruction = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    WarningThreshold = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    BlockingThreshold = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    TimeoutSeconds = table.Column<int>(type: "int", nullable: false),
                    Order = table.Column<int>(type: "int", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    RequiresExplicitApprovalIfBlocking = table.Column<bool>(type: "bit", nullable: false),
                    RemediationProcedure = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    SuggestedSopId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowPreChecks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkflowPreChecks_Components_TargetComponentId",
                        column: x => x.TargetComponentId,
                        principalTable: "Components",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_WorkflowPreChecks_Sops_SuggestedSopId",
                        column: x => x.SuggestedSopId,
                        principalTable: "Sops",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_WorkflowPreChecks_Workflows_WorkflowId",
                        column: x => x.WorkflowId,
                        principalTable: "Workflows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BusinessSuccessMetrics_EnvironmentId",
                table: "BusinessSuccessMetrics",
                column: "EnvironmentId");

            migrationBuilder.CreateIndex(
                name: "IX_BusinessSuccessMetrics_IsTargetMissed",
                table: "BusinessSuccessMetrics",
                column: "IsTargetMissed");

            migrationBuilder.CreateIndex(
                name: "IX_BusinessSuccessMetrics_MetricCode_ReportingPeriod",
                table: "BusinessSuccessMetrics",
                columns: new[] { "MetricCode", "ReportingPeriod" });

            migrationBuilder.CreateIndex(
                name: "IX_BusinessSuccessMetrics_ReportingPeriod_MetricCode_EnvironmentId",
                table: "BusinessSuccessMetrics",
                columns: new[] { "ReportingPeriod", "MetricCode", "EnvironmentId" },
                unique: true,
                filter: "[EnvironmentId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CapitalizedIncidents_EnvironmentId_DeclaredSeverity_RealIncidentStartedAt",
                table: "CapitalizedIncidents",
                columns: new[] { "EnvironmentId", "DeclaredSeverity", "RealIncidentStartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CapitalizedIncidents_ExternalReference",
                table: "CapitalizedIncidents",
                column: "ExternalReference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CapitalizedIncidents_GeneratedSOPId",
                table: "CapitalizedIncidents",
                column: "GeneratedSOPId");

            migrationBuilder.CreateIndex(
                name: "IX_CapitalizedIncidents_LinkedDiagnosticSessionId",
                table: "CapitalizedIncidents",
                column: "LinkedDiagnosticSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_CapitalizedIncidents_LinkedExecutionId",
                table: "CapitalizedIncidents",
                column: "LinkedExecutionId");

            migrationBuilder.CreateIndex(
                name: "IX_ComponentAssociatedServices_ComponentId_Order",
                table: "ComponentAssociatedServices",
                columns: new[] { "ComponentId", "Order" });

            migrationBuilder.CreateIndex(
                name: "IX_ComponentAssociatedServices_ComponentId_ServiceName",
                table: "ComponentAssociatedServices",
                columns: new[] { "ComponentId", "ServiceName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PreventiveRecommendedActions_Code",
                table: "PreventiveRecommendedActions",
                column: "Code");

            migrationBuilder.CreateIndex(
                name: "IX_PreventiveRecommendedActions_EnvironmentId_APRStatus_GeneratedAt",
                table: "PreventiveRecommendedActions",
                columns: new[] { "EnvironmentId", "APRStatus", "GeneratedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PreventiveRecommendedActions_EnvironmentId_PriorityScore",
                table: "PreventiveRecommendedActions",
                columns: new[] { "EnvironmentId", "PriorityScore" });

            migrationBuilder.CreateIndex(
                name: "IX_PreventiveRecommendedActions_LaunchWorkflowId",
                table: "PreventiveRecommendedActions",
                column: "LaunchWorkflowId");

            migrationBuilder.CreateIndex(
                name: "IX_PreventiveRecommendedActions_ResultingExecutionId",
                table: "PreventiveRecommendedActions",
                column: "ResultingExecutionId");

            migrationBuilder.CreateIndex(
                name: "IX_PreventiveRecommendedActions_SourceComponentId",
                table: "PreventiveRecommendedActions",
                column: "SourceComponentId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowPreChecks_SuggestedSopId",
                table: "WorkflowPreChecks",
                column: "SuggestedSopId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowPreChecks_TargetComponentId",
                table: "WorkflowPreChecks",
                column: "TargetComponentId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowPreChecks_WorkflowId_Code",
                table: "WorkflowPreChecks",
                columns: new[] { "WorkflowId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowPreChecks_WorkflowId_Order",
                table: "WorkflowPreChecks",
                columns: new[] { "WorkflowId", "Order" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BusinessSuccessMetrics");

            migrationBuilder.DropTable(
                name: "CapitalizedIncidents");

            migrationBuilder.DropTable(
                name: "ComponentAssociatedServices");

            migrationBuilder.DropTable(
                name: "PreventiveRecommendedActions");

            migrationBuilder.DropTable(
                name: "WorkflowPreChecks");
        }
    }
}
