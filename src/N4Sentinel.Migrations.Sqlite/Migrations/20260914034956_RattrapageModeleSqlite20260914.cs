using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace N4Sentinel.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class RattrapageModeleSqlite20260914 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "QualificationNote",
                table: "Sops",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "QualifiedAt",
                table: "Sops",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "QualifiedBy",
                table: "Sops",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RevocationReason",
                table: "Sops",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "RevokedAt",
                table: "Sops",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RevokedBy",
                table: "Sops",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "FeedbackGivenAt",
                table: "SopExecutions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FeedbackGivenBy",
                table: "SopExecutions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FeedbackNote",
                table: "SopExecutions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "FeedbackStars0To5",
                table: "SopExecutions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AntivirusChecked",
                table: "LogSources",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "AntivirusNote",
                table: "LogSources",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TriggeredBy",
                table: "Executions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "TriggeredByScheduleId",
                table: "Executions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Palier2AuthorizedConnectorReferences",
                table: "Environments",
                type: "TEXT",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ResendRequestedAt",
                table: "EdiFiles",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResendRequestedBy",
                table: "EdiFiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ActiveSignaturesCountAtAnalysis",
                table: "DiagnosticSessions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SignatureCatalogueVersionAtAnalysis",
                table: "DiagnosticSessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Top10ChecklistCheckedCodesCsv",
                table: "DiagnosticSessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Top10ChecklistNotes",
                table: "DiagnosticSessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AllowTokenPastedLogin",
                table: "AzureAdSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "RequireSignatureValidation",
                table: "AzureAdSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "RoleMappings",
                table: "AzureAdSettings",
                type: "TEXT",
                nullable: false,
                defaultValueSql: "'[]'");

            migrationBuilder.CreateTable(
                name: "BusinessSuccessMetrics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ReportingPeriod = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    MetricCode = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    MetricLabel = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    NumericValueAvgOrSum = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: true),
                    TargetFixedValue = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: true),
                    BaselineBeforeSentinel = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: true),
                    TrendVsPreviousPeriodPct = table.Column<decimal>(type: "TEXT", precision: 8, scale: 2, nullable: true),
                    AggregationKind = table.Column<int>(type: "INTEGER", nullable: false),
                    CalculatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    SampleSizeCount = table.Column<long>(type: "INTEGER", nullable: true),
                    IsTargetMissed = table.Column<bool>(type: "INTEGER", nullable: false),
                    NarrativeComment = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: true)
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExternalReference = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeclaredSeverity = table.Column<int>(type: "INTEGER", nullable: false),
                    InitialSymptom = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    RealIncidentStartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RealIncidentEndedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    TotalDurationMinutes = table.Column<long>(type: "INTEGER", nullable: true),
                    DetectedBySentinelAt = table.Column<long>(type: "INTEGER", nullable: true),
                    MainRootCauseCode = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    MainRootCauseLabel = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    FiveWhysRootCause = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true),
                    CorrectiveActionsTaken = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true),
                    LinkedDiagnosticSessionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    LinkedExecutionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SOPsUsedListString = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    GeneratedSOPId = table.Column<Guid>(type: "TEXT", nullable: true),
                    QuantifiedBusinessImpact = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CompensatoryMeasures = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    OperatorsInvolvedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ResolutionSkillLevel = table.Column<int>(type: "INTEGER", nullable: true),
                    ClosedBy = table.Column<string>(type: "TEXT", maxLength: 150, nullable: true),
                    ClosedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    PostMortemDocumentRef = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    LessonsAndCorrectives = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: true)
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ComponentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ServiceName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    ExpectedStatus = table.Column<int>(type: "INTEGER", nullable: false),
                    ExpectedPort = table.Column<int>(type: "INTEGER", nullable: true),
                    ExpectedServiceAccount = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    TimeoutSecondsOverride = table.Column<int>(type: "INTEGER", nullable: true),
                    IsMandatory = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastObservedPid = table.Column<int>(type: "INTEGER", nullable: true),
                    LastObservedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastObservedStatus = table.Column<int>(type: "INTEGER", nullable: true),
                    LastObservationNote = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: true)
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
                name: "ExecutionSchedules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkflowId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Code = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    TimeOfDay = table.Column<TimeSpan>(type: "TEXT", nullable: false),
                    WeeklyDayOfWeekMask = table.Column<ushort>(type: "INTEGER", nullable: false),
                    MonthlyDays = table.Column<string>(type: "TEXT", nullable: false),
                    CronExpression = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    RanAs = table.Column<string>(type: "TEXT", nullable: false),
                    ReasonTemplate = table.Column<string>(type: "TEXT", nullable: true),
                    TicketTemplate = table.Column<string>(type: "TEXT", nullable: true),
                    AutoApprove = table.Column<bool>(type: "INTEGER", nullable: false),
                    ValidFrom = table.Column<long>(type: "INTEGER", nullable: true),
                    ValidUntil = table.Column<long>(type: "INTEGER", nullable: true),
                    LastTriggeredAt = table.Column<long>(type: "INTEGER", nullable: true),
                    NextPlannedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    SkipIfActiveAlready = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: true)
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
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "JmxEndpoints",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    EndpointType = table.Column<byte>(type: "INTEGER", nullable: false),
                    Host = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Port = table.Column<int>(type: "INTEGER", nullable: false),
                    JolokiaBaseUrl = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CredentialId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastReachableAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastFailureMessage = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    LastPooledAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastSuccessAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastErrorAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JmxEndpoints", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MaintenanceHolidays",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: true)
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
                name: "PreventiveRecommendedActions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceComponentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Code = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    RootCauseFamily = table.Column<int>(type: "INTEGER", nullable: false),
                    EstimatedP1RiskPct = table.Column<decimal>(type: "TEXT", precision: 8, scale: 2, nullable: false),
                    EstimatedDurationMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    PriorityScore = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false),
                    APRStatus = table.Column<int>(type: "INTEGER", nullable: false),
                    FirstWeakSignalAt = table.Column<long>(type: "INTEGER", nullable: true),
                    GeneratedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    EstimatedDeadlineForAction = table.Column<long>(type: "INTEGER", nullable: true),
                    ScheduledAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ScheduledBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    LaunchWorkflowId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ResultingExecutionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ExecutedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    ExecutedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    DismissedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    DismissedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    DismissalReason = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    IsBasedOnTrend = table.Column<bool>(type: "INTEGER", nullable: false),
                    EvidenceSummary = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    RawSignalSnapshot = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: true)
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
                name: "Tickets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Code = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    Status = table.Column<byte>(type: "INTEGER", nullable: false),
                    Priority = table.Column<byte>(type: "INTEGER", nullable: false),
                    Severity = table.Column<byte>(type: "INTEGER", nullable: false),
                    Source = table.Column<byte>(type: "INTEGER", nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DiagnosticSignatureId = table.Column<Guid>(type: "TEXT", nullable: true),
                    OperationCorrelationId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    AprId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AssignedTo = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    ExternalTicketId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    OpenedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ClosedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ClosureReason = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    OpenedBy = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: true)
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
                name: "WorkflowPreChecks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkflowId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Code = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    Label = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Category = table.Column<int>(type: "INTEGER", nullable: false),
                    TargetComponentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    ControlInstruction = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    WarningThreshold = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    BlockingThreshold = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    TimeoutSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    RequiresExplicitApprovalIfBlocking = table.Column<bool>(type: "INTEGER", nullable: false),
                    RemediationProcedure = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    SuggestedSopId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: true)
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

            migrationBuilder.CreateTable(
                name: "TicketComments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TicketId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WrittenBy = table.Column<string>(type: "TEXT", nullable: false),
                    Author = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Body = table.Column<string>(type: "TEXT", nullable: false),
                    Text = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    WrittenAt = table.Column<long>(type: "INTEGER", nullable: false),
                    IsInternal = table.Column<bool>(type: "INTEGER", nullable: false),
                    Source = table.Column<byte>(type: "INTEGER", nullable: false)
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
                unique: true);

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
                name: "ExecutionSchedules");

            migrationBuilder.DropTable(
                name: "JmxEndpoints");

            migrationBuilder.DropTable(
                name: "MaintenanceHolidays");

            migrationBuilder.DropTable(
                name: "PreventiveRecommendedActions");

            migrationBuilder.DropTable(
                name: "TicketComments");

            migrationBuilder.DropTable(
                name: "WorkflowPreChecks");

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
                name: "Palier2AuthorizedConnectorReferences",
                table: "Environments");

            migrationBuilder.DropColumn(
                name: "ResendRequestedAt",
                table: "EdiFiles");

            migrationBuilder.DropColumn(
                name: "ResendRequestedBy",
                table: "EdiFiles");

            migrationBuilder.DropColumn(
                name: "ActiveSignaturesCountAtAnalysis",
                table: "DiagnosticSessions");

            migrationBuilder.DropColumn(
                name: "SignatureCatalogueVersionAtAnalysis",
                table: "DiagnosticSessions");

            migrationBuilder.DropColumn(
                name: "Top10ChecklistCheckedCodesCsv",
                table: "DiagnosticSessions");

            migrationBuilder.DropColumn(
                name: "Top10ChecklistNotes",
                table: "DiagnosticSessions");

            migrationBuilder.DropColumn(
                name: "AllowTokenPastedLogin",
                table: "AzureAdSettings");

            migrationBuilder.DropColumn(
                name: "RequireSignatureValidation",
                table: "AzureAdSettings");

            migrationBuilder.DropColumn(
                name: "RoleMappings",
                table: "AzureAdSettings");
        }
    }
}
