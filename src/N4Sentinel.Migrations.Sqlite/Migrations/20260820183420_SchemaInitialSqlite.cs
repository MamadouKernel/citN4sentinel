using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace N4Sentinel.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class SchemaInitialSqlite : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApprovalMatrixRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EnvironmentKind = table.Column<int>(type: "INTEGER", nullable: true),
                    WorkflowKind = table.Column<int>(type: "INTEGER", nullable: true),
                    MinCriticality = table.Column<int>(type: "INTEGER", nullable: false),
                    RequiresApproval = table.Column<bool>(type: "INTEGER", nullable: false),
                    RequiresDoubleApproval = table.Column<bool>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalMatrixRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetRoles",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    NormalizedName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUsers",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 150, nullable: true),
                    Department = table.Column<string>(type: "TEXT", maxLength: 150, nullable: true),
                    IsDisabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastLoginAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    PasswordChangedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    PasswordExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    PasswordHistory = table.Column<string>(type: "TEXT", nullable: false),
                    UserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    NormalizedUserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Email = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    NormalizedEmail = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    EmailConfirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", nullable: true),
                    SecurityStamp = table.Column<string>(type: "TEXT", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "TEXT", nullable: true),
                    PhoneNumber = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    PhoneNumberConfirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                    TwoFactorEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    LockoutEnd = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LockoutEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    AccessFailedCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuditEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OccurredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Actor = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ActorIpAddress = table.Column<string>(type: "TEXT", maxLength: 45, nullable: true),
                    Action = table.Column<int>(type: "INTEGER", nullable: false),
                    Outcome = table.Column<int>(type: "INTEGER", nullable: false),
                    EntityType = table.Column<string>(type: "TEXT", maxLength: 150, nullable: false),
                    EntityId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    EntityLabel = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    EnvironmentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    BeforeJson = table.Column<string>(type: "TEXT", nullable: true),
                    AfterJson = table.Column<string>(type: "TEXT", nullable: true),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Detail = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AzureAdSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ClientId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Authority = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    PostLogoutRedirectUri = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AzureAdSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ComponentSignals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ComponentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ComponentName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    SignalType = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    Target = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Value = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Threshold = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    CapturedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Quality = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComponentSignals", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CorrelationRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Code = table.Column<string>(type: "TEXT", maxLength: 150, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Domain = table.Column<int>(type: "INTEGER", nullable: false),
                    HypothesisStatement = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Confidence = table.Column<int>(type: "INTEGER", nullable: false),
                    Recommendation = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    TimeWindowSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CorrelationRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DiagnosticSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    HypothesisEstablishedThreshold = table.Column<int>(type: "INTEGER", nullable: false),
                    ConclusiveSignatureConfidenceWeight = table.Column<int>(type: "INTEGER", nullable: false),
                    SeriousLeadConfidenceThreshold = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiagnosticSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DiagnosticSignatures",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Code = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Pattern = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Domain = table.Column<int>(type: "INTEGER", nullable: false),
                    Severity = table.Column<int>(type: "INTEGER", nullable: false),
                    Origin = table.Column<int>(type: "INTEGER", nullable: false),
                    Meaning = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Remediation = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    DocumentReference = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    CounterEvidence = table.Column<string>(type: "TEXT", nullable: true),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    ConfidenceWeight = table.Column<int>(type: "INTEGER", nullable: false),
                    AppliesToRole = table.Column<int>(type: "INTEGER", nullable: true),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ValidationStatus = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiagnosticSignatures", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Environments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Code = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 150, nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Criticality = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    TimeZoneId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    AutomationLevel = table.Column<int>(type: "INTEGER", nullable: false),
                    Palier2ApprovedBy = table.Column<string>(type: "TEXT", nullable: true),
                    Palier2ApprovedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    TechnicalOwner = table.Column<string>(type: "TEXT", maxLength: 150, nullable: true),
                    FunctionalOwner = table.Column<string>(type: "TEXT", maxLength: 150, nullable: true),
                    DefaultCredentialReference = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ExpectedTimeSource = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ClockToleranceSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Environments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "KnowledgeDocuments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Reference = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    DocumentVersion = table.Column<string>(type: "TEXT", maxLength: 60, nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    ValidatedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    ValidatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    SourceFileName = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    PageCount = table.Column<int>(type: "INTEGER", nullable: false),
                    SectionCount = table.Column<int>(type: "INTEGER", nullable: false),
                    AppliesToVersion = table.Column<string>(type: "TEXT", maxLength: 60, nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    AntivirusChecked = table.Column<bool>(type: "INTEGER", nullable: false),
                    AntivirusNote = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeDocuments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RetentionPolicies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    LogsRetentionDays = table.Column<int>(type: "INTEGER", nullable: false),
                    ReportsRetentionDays = table.Column<int>(type: "INTEGER", nullable: false),
                    AuditRetentionDays = table.Column<int>(type: "INTEGER", nullable: false),
                    SignalsRetentionDays = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RetentionPolicies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetRoleClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RoleId = table.Column<string>(type: "TEXT", nullable: false),
                    ClaimType = table.Column<string>(type: "TEXT", nullable: true),
                    ClaimValue = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoleClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetRoleClaims_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    ClaimType = table.Column<string>(type: "TEXT", nullable: true),
                    ClaimValue = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetUserClaims_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserLogins",
                columns: table => new
                {
                    LoginProvider = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ProviderKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ProviderDisplayName = table.Column<string>(type: "TEXT", nullable: true),
                    UserId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserLogins", x => new { x.LoginProvider, x.ProviderKey });
                    table.ForeignKey(
                        name: "FK_AspNetUserLogins_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserPasskeys",
                columns: table => new
                {
                    CredentialId = table.Column<byte[]>(type: "BLOB", maxLength: 1024, nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    Data = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserPasskeys", x => x.CredentialId);
                    table.ForeignKey(
                        name: "FK_AspNetUserPasskeys_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserRoles",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    RoleId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserRoles", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserTokens",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    LoginProvider = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserTokens", x => new { x.UserId, x.LoginProvider, x.Name });
                    table.ForeignKey(
                        name: "FK_AspNetUserTokens_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PasswordHistoryRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PasswordHistoryRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PasswordHistoryRecords_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CorrelationConditions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RuleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SignalSourceId = table.Column<string>(type: "TEXT", maxLength: 150, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    IsNegation = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CorrelationConditions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CorrelationConditions_CorrelationRules_RuleId",
                        column: x => x.RuleId,
                        principalTable: "CorrelationRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Credentials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Reference = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Label = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Mode = table.Column<int>(type: "INTEGER", nullable: false),
                    UserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    ProtectedPassword = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    PasswordSetAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastVerifiedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastVerificationResult = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Credentials", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Credentials_Environments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "Environments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DiagnosticSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EnvironmentCode = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    TicketReference = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    RequestedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    WindowStart = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    WindowEnd = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Verdict = table.Column<int>(type: "INTEGER", nullable: false),
                    VerdictExplanation = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    AnalysedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    SourceAlertId = table.Column<Guid>(type: "TEXT", nullable: true),
                    IsReferenceBaseline = table.Column<bool>(type: "INTEGER", nullable: false),
                    ReferenceSessionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ReferenceKind = table.Column<int>(type: "INTEGER", nullable: false),
                    ReferenceExecutionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ReferenceComponentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Phase = table.Column<int>(type: "INTEGER", nullable: false),
                    EscalatedTo = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    EscalatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    EscalatedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiagnosticSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DiagnosticSessions_DiagnosticSessions_ReferenceSessionId",
                        column: x => x.ReferenceSessionId,
                        principalTable: "DiagnosticSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DiagnosticSessions_Environments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "Environments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EnvironmentGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    UserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Level = table.Column<int>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EnvironmentGrants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EnvironmentGrants_Environments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "Environments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EnvironmentLocks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExecutionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    HeldBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    AcquiredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EnvironmentLocks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EnvironmentLocks_Environments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "Environments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Servers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    HostName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    IpAddress = table.Column<string>(type: "TEXT", maxLength: 45, nullable: true),
                    DnsName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    OperatingSystem = table.Column<string>(type: "TEXT", maxLength: 150, nullable: true),
                    WinRmPort = table.Column<int>(type: "INTEGER", nullable: false),
                    UseSsl = table.Column<bool>(type: "INTEGER", nullable: false),
                    Criticality = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    TechnicalOwner = table.Column<string>(type: "TEXT", maxLength: 150, nullable: true),
                    CredentialReference = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Servers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Servers_Environments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "Environments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Sops",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Code = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    HasBeenExecuted = table.Column<bool>(type: "INTEGER", nullable: false),
                    RequiresElevatedRole = table.Column<bool>(type: "INTEGER", nullable: false),
                    Objective = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Scope = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Prerequisites = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Risks = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Controls = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    ExpectedOutcome = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    RollbackPlan = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    EscalationPath = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    AppliesToVersion = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    SourceDocumentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SourceExecutionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SourceDiagnosticSessionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: true)
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
                name: "Workflows",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Code = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    HasBeenExecuted = table.Column<bool>(type: "INTEGER", nullable: false),
                    RequiresApproval = table.Column<bool>(type: "INTEGER", nullable: false),
                    RequiresDoubleApproval = table.Column<bool>(type: "INTEGER", nullable: false),
                    AutomationLevel = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Workflows", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Workflows_Environments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "Environments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DocumentSections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DocumentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                    PageNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    Heading = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    Content = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: false),
                    SearchText = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentSections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DocumentSections_KnowledgeDocuments_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "KnowledgeDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DiagnosticHypotheses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Domain = table.Column<int>(type: "INTEGER", nullable: false),
                    Statement = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Confidence = table.Column<int>(type: "INTEGER", nullable: false),
                    Evidence = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    CounterEvidence = table.Column<string>(type: "TEXT", nullable: true),
                    EvidenceObservedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    RuleVersion = table.Column<string>(type: "TEXT", nullable: true),
                    Recommendation = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Rank = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiagnosticHypotheses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DiagnosticHypotheses_DiagnosticSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "DiagnosticSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LogSources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ComponentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ComponentName = table.Column<string>(type: "TEXT", maxLength: 150, nullable: true),
                    ComponentRole = table.Column<int>(type: "INTEGER", nullable: true),
                    HostName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    ComponentAutoDetected = table.Column<bool>(type: "INTEGER", nullable: false),
                    DetectedVersion = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    DetectedLogType = table.Column<string>(type: "TEXT", nullable: true),
                    DetectedTimeZone = table.Column<string>(type: "TEXT", nullable: true),
                    SuggestedComponentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SuggestedComponentName = table.Column<string>(type: "TEXT", maxLength: 150, nullable: true),
                    SuggestionEvidence = table.Column<string>(type: "TEXT", maxLength: 600, nullable: true),
                    OriginAmbiguous = table.Column<bool>(type: "INTEGER", nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", nullable: true),
                    Origin = table.Column<int>(type: "INTEGER", nullable: false),
                    ResolvedPath = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    LineCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", nullable: true),
                    EarliestEntryAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LatestEntryAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    InfoCount = table.Column<int>(type: "INTEGER", nullable: false),
                    WarningCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ErrorCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ClockSkewSecondsAtCollection = table.Column<double>(type: "REAL", nullable: true),
                    MaskedSecretCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Truncated = table.Column<bool>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    FailureReason = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LogSources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LogSources_DiagnosticSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "DiagnosticSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PhaseTransitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Phase = table.Column<int>(type: "INTEGER", nullable: false),
                    EnteredBy = table.Column<string>(type: "TEXT", nullable: false),
                    EnteredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Note = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhaseTransitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PhaseTransitions_DiagnosticSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "DiagnosticSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Components",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ServerId = table.Column<Guid>(type: "TEXT", nullable: true),
                    LogicalName = table.Column<string>(type: "TEXT", maxLength: 150, nullable: false),
                    Role = table.Column<int>(type: "INTEGER", nullable: false),
                    WindowsServiceName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    ProcessName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    Endpoint = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Port = table.Column<int>(type: "INTEGER", nullable: true),
                    Criticality = table.Column<int>(type: "INTEGER", nullable: false),
                    ControlMode = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    MaintenanceMode = table.Column<bool>(type: "INTEGER", nullable: false),
                    MaintenanceNote = table.Column<string>(type: "TEXT", nullable: true),
                    MaintenanceUntil = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    MaintenanceReason = table.Column<string>(type: "TEXT", nullable: true),
                    MaintenanceBy = table.Column<string>(type: "TEXT", nullable: true),
                    StartOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    TechnicalOwner = table.Column<string>(type: "TEXT", maxLength: 150, nullable: true),
                    Readiness_LogPath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Readiness_ReadyPatterns = table.Column<string>(type: "TEXT", nullable: false),
                    Readiness_ErrorPatterns = table.Column<string>(type: "TEXT", nullable: false),
                    Readiness_IgnorePatterns = table.Column<string>(type: "TEXT", nullable: false),
                    Readiness_ActiveRolePatterns = table.Column<string>(type: "TEXT", nullable: false),
                    Readiness_SyncPatterns = table.Column<string>(type: "TEXT", nullable: false),
                    Readiness_SyncDelayThresholdMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    Readiness_ServiceRunningTimeoutSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    Readiness_LogReadyTimeoutSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    Readiness_StopTimeoutSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    Readiness_PollIntervalSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    Readiness_ProgressEverySeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    Readiness_PostReadySettleSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    SharedFolder_RootPath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    SharedFolder_Category = table.Column<int>(type: "INTEGER", nullable: false),
                    SharedFolder_PendingSubfolder = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    SharedFolder_ConsumedSubfolder = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    SharedFolder_BlockedSubfolder = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    SharedFolder_ErrorSubfolder = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    SharedFolder_MaxPendingAgeHours = table.Column<int>(type: "INTEGER", nullable: true),
                    SharedFolder_MaxWriteLatencyMs = table.Column<int>(type: "INTEGER", nullable: true),
                    SharedFolder_MaxGrowthBytesPerHour = table.Column<long>(type: "INTEGER", nullable: true),
                    SharedFolder_EdiFileNamingPattern = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    SharedFolder_MaxHoursSinceLastIntegration = table.Column<int>(type: "INTEGER", nullable: true),
                    SharedFolder_LastBackupAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    SharedFolder_LastBackupBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    SharedFolder_LastBackupNote = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Components", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Components_Environments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "Environments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Components_Servers_ServerId",
                        column: x => x.ServerId,
                        principalTable: "Servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SopAssociations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SopId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    ComponentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Role = table.Column<int>(type: "INTEGER", nullable: true),
                    SignatureId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SignatureCode = table.Column<string>(type: "TEXT", maxLength: 60, nullable: true),
                    DiagnosticSessionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: true)
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SopId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SopVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    SopCode = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    SopTitle = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EnvironmentCode = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    TicketReference = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    SourceAlertId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SourceDiagnosticSessionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    AbandonReason = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Outcome = table.Column<int>(type: "INTEGER", nullable: true),
                    OutcomeNote = table.Column<string>(type: "TEXT", nullable: true),
                    OutcomeDeclaredBy = table.Column<string>(type: "TEXT", nullable: true),
                    OutcomeDeclaredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: true)
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
                name: "Executions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkflowId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkflowVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    WorkflowName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EnvironmentCode = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    IsSimulation = table.Column<bool>(type: "INTEGER", nullable: false),
                    AutomationLevel = table.Column<int>(type: "INTEGER", nullable: false),
                    IsFallbackSemiAutoForced = table.Column<bool>(type: "INTEGER", nullable: false),
                    RequestedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    TicketReference = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ExpectedImpact = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    StartWindow = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    EndWindow = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    EstimatedTotalDuration = table.Column<TimeSpan>(type: "TEXT", nullable: true),
                    ApprovedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    RequiresDoubleApproval = table.Column<bool>(type: "INTEGER", nullable: false),
                    SecondApprovedBy = table.Column<string>(type: "TEXT", nullable: true),
                    SecondApprovedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ContinuityChoiceRequired = table.Column<bool>(type: "INTEGER", nullable: false),
                    ContinuityChoice = table.Column<int>(type: "INTEGER", nullable: true),
                    ContinuityChoiceBy = table.Column<string>(type: "TEXT", nullable: true),
                    ContinuityChoiceAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    EndedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastHeartbeatAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    PauseRequestedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    CancelRequestedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    PostCancellationReport = table.Column<string>(type: "TEXT", nullable: true),
                    RequiresManualInterventionAfterCancel = table.Column<bool>(type: "INTEGER", nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    PreflightJson = table.Column<string>(type: "TEXT", nullable: true),
                    PreflightAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    PreflightBlocked = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Executions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Executions_Environments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "Environments",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Executions_Workflows_WorkflowId",
                        column: x => x.WorkflowId,
                        principalTable: "Workflows",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "KnowledgeFeedback",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DocumentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SectionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Question = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Comment = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    ProposedCorrection = table.Column<string>(type: "TEXT", nullable: true),
                    ReportedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ReviewStatus = table.Column<int>(type: "INTEGER", nullable: false),
                    ReviewNote = table.Column<string>(type: "TEXT", nullable: true),
                    ReviewedBy = table.Column<string>(type: "TEXT", nullable: true),
                    ReviewedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Resolved = table.Column<bool>(type: "INTEGER", nullable: false),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ResolvedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeFeedback", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KnowledgeFeedback_DocumentSections_SectionId",
                        column: x => x.SectionId,
                        principalTable: "DocumentSections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LogFindings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SignatureId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SignatureCode = table.Column<string>(type: "TEXT", maxLength: 60, nullable: true),
                    Domain = table.Column<int>(type: "INTEGER", nullable: false),
                    Severity = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    SampleLine = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Context = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    OccurrenceCount = table.Column<int>(type: "INTEGER", nullable: false),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    FirstLineNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    Meaning = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Remediation = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    DocumentReference = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    ThreadName = table.Column<string>(type: "TEXT", nullable: true),
                    LoggerClass = table.Column<string>(type: "TEXT", nullable: true),
                    TransactionId = table.Column<string>(type: "TEXT", nullable: true),
                    Level = table.Column<string>(type: "TEXT", nullable: true),
                    ErrorCode = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LogFindings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LogFindings_DiagnosticSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "DiagnosticSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LogFindings_LogSources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "LogSources",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Alerts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Signature = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ComponentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ComponentName = table.Column<string>(type: "TEXT", maxLength: 150, nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Severity = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Detail = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    Recommendation = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    FirstOccurredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastOccurredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    OccurrenceCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    AcknowledgedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    AcknowledgedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    AcknowledgementNote = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Alerts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Alerts_Components_ComponentId",
                        column: x => x.ComponentId,
                        principalTable: "Components",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Alerts_Environments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "Environments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ComponentDependencies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ComponentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DependsOnComponentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComponentDependencies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ComponentDependencies_Components_ComponentId",
                        column: x => x.ComponentId,
                        principalTable: "Components",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ComponentDependencies_Components_DependsOnComponentId",
                        column: x => x.DependsOnComponentId,
                        principalTable: "Components",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EdiFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ComponentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 260, nullable: false),
                    MessageType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Partner = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    IntegratedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ConsecutiveRejections = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: true)
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

            migrationBuilder.CreateTable(
                name: "SharedFolderSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ComponentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CapturedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Reachable = table.Column<bool>(type: "INTEGER", nullable: true),
                    UnreachableReason = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    TotalFileCount = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalSizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    OldestFileAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    NewestFileAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    PendingCount = table.Column<int>(type: "INTEGER", nullable: true),
                    ConsumedCount = table.Column<int>(type: "INTEGER", nullable: true),
                    BlockedCount = table.Column<int>(type: "INTEGER", nullable: true),
                    ErrorCount = table.Column<int>(type: "INTEGER", nullable: true),
                    OldestPendingAgeHours = table.Column<double>(type: "REAL", nullable: true),
                    CanWrite = table.Column<bool>(type: "INTEGER", nullable: true),
                    WriteLatencyMs = table.Column<double>(type: "REAL", nullable: true),
                    GrowthBytesPerHour = table.Column<double>(type: "REAL", nullable: true),
                    MandatoryFilesPresent = table.Column<bool>(type: "INTEGER", nullable: true),
                    MissingMandatoryFiles = table.Column<string>(type: "TEXT", nullable: false),
                    HealthWarnings = table.Column<string>(type: "TEXT", nullable: false),
                    CorruptionIndicators = table.Column<string>(type: "TEXT", nullable: false),
                    SopExecutionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CorruptionConfirmed = table.Column<bool>(type: "INTEGER", nullable: true),
                    CorruptionConclusion = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: true)
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

            migrationBuilder.CreateTable(
                name: "SopSteps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SopId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Instruction = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    ExpectedResult = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    ComponentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    IsSkippable = table.Column<bool>(type: "INTEGER", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: true)
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
                name: "WorkflowSteps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkflowId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Action = table.Column<int>(type: "INTEGER", nullable: false),
                    ComponentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Instruction = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    ExpectedSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    WarningThresholdSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    TimeoutSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxRetries = table.Column<int>(type: "INTEGER", nullable: false),
                    RetryDelaySeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    AutomaticRetry = table.Column<bool>(type: "INTEGER", nullable: false),
                    FailurePolicy = table.Column<int>(type: "INTEGER", nullable: false),
                    IsSkippable = table.Column<bool>(type: "INTEGER", nullable: false),
                    RequiresConfirmation = table.Column<bool>(type: "INTEGER", nullable: false),
                    RequiresEvidenceFile = table.Column<bool>(type: "INTEGER", nullable: false),
                    CanRunInParallel = table.Column<bool>(type: "INTEGER", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowSteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkflowSteps_Components_ComponentId",
                        column: x => x.ComponentId,
                        principalTable: "Components",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_WorkflowSteps_Workflows_WorkflowId",
                        column: x => x.WorkflowId,
                        principalTable: "Workflows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SopExecutionSteps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SopExecutionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Instruction = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    ExpectedResult = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    ComponentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ComponentName = table.Column<string>(type: "TEXT", maxLength: 150, nullable: true),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    EndedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    IsSkippable = table.Column<bool>(type: "INTEGER", nullable: false),
                    ConfirmedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    ConfirmedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Evidence = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    DeviationNote = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    SkippedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    SkipReason = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    History = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: true)
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

            migrationBuilder.CreateTable(
                name: "ExecutionSteps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExecutionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Action = table.Column<int>(type: "INTEGER", nullable: false),
                    ComponentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ComponentName = table.Column<string>(type: "TEXT", maxLength: 150, nullable: true),
                    HostName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    EndedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ProgressMessage = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Evidence = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    ExecutedCommand = table.Column<string>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    ErrorType = table.Column<int>(type: "INTEGER", nullable: true),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxRetries = table.Column<int>(type: "INTEGER", nullable: false),
                    AutomaticRetry = table.Column<bool>(type: "INTEGER", nullable: false),
                    RetryDelaySeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    RetryNotBeforeAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    SkippedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    SkipReason = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    SkipCoApprovedBy = table.Column<string>(type: "TEXT", nullable: true),
                    SkipCoApprovedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ForcedStopBy = table.Column<string>(type: "TEXT", nullable: true),
                    ForcedStopReason = table.Column<string>(type: "TEXT", nullable: true),
                    ConfirmedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    ConfirmedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    OperatorNote = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    RequiresEvidenceFile = table.Column<bool>(type: "INTEGER", nullable: false),
                    EvidenceFileName = table.Column<string>(type: "TEXT", nullable: true),
                    EvidenceFileContentType = table.Column<string>(type: "TEXT", nullable: true),
                    EvidenceFileContent = table.Column<byte[]>(type: "BLOB", nullable: true),
                    DiagnosticSessionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TimeoutSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    ExpectedSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    WarningThresholdSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    IsSkippable = table.Column<bool>(type: "INTEGER", nullable: false),
                    RequiresConfirmation = table.Column<bool>(type: "INTEGER", nullable: false),
                    CanRunInParallel = table.Column<bool>(type: "INTEGER", nullable: false),
                    FailurePolicy = table.Column<int>(type: "INTEGER", nullable: false),
                    Instruction = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExecutionSteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExecutionSteps_Executions_ExecutionId",
                        column: x => x.ExecutionId,
                        principalTable: "Executions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ExternalActionDeclarations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DiagnosticSessionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    WorkflowExecutionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ComponentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ComponentName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DeclaredBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalActionDeclarations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExternalActionDeclarations_DiagnosticSessions_DiagnosticSessionId",
                        column: x => x.DiagnosticSessionId,
                        principalTable: "DiagnosticSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ExternalActionDeclarations_Executions_WorkflowExecutionId",
                        column: x => x.WorkflowExecutionId,
                        principalTable: "Executions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_ComponentId_Signature_Status",
                table: "Alerts",
                columns: new[] { "ComponentId", "Signature", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_EnvironmentId_Status_LastOccurredAt",
                table: "Alerts",
                columns: new[] { "EnvironmentId", "Status", "LastOccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AspNetRoleClaims_RoleId",
                table: "AspNetRoleClaims",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "RoleNameIndex",
                table: "AspNetRoles",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserClaims_UserId",
                table: "AspNetUserClaims",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserLogins_UserId",
                table: "AspNetUserLogins",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserPasskeys_UserId",
                table: "AspNetUserPasskeys",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserRoles_RoleId",
                table: "AspNetUserRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "AspNetUsers",
                column: "NormalizedEmail");

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                table: "AspNetUsers",
                column: "NormalizedUserName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_CorrelationId",
                table: "AuditEntries",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_EntityType_EntityId",
                table: "AuditEntries",
                columns: new[] { "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_EnvironmentId_OccurredAt",
                table: "AuditEntries",
                columns: new[] { "EnvironmentId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_OccurredAt",
                table: "AuditEntries",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_ComponentDependencies_ComponentId_DependsOnComponentId",
                table: "ComponentDependencies",
                columns: new[] { "ComponentId", "DependsOnComponentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ComponentDependencies_DependsOnComponentId",
                table: "ComponentDependencies",
                column: "DependsOnComponentId");

            migrationBuilder.CreateIndex(
                name: "IX_Components_EnvironmentId_LogicalName",
                table: "Components",
                columns: new[] { "EnvironmentId", "LogicalName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Components_ServerId",
                table: "Components",
                column: "ServerId");

            migrationBuilder.CreateIndex(
                name: "IX_ComponentSignals_ComponentId_CapturedAt",
                table: "ComponentSignals",
                columns: new[] { "ComponentId", "CapturedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CorrelationConditions_RuleId",
                table: "CorrelationConditions",
                column: "RuleId");

            migrationBuilder.CreateIndex(
                name: "IX_CorrelationRules_Code",
                table: "CorrelationRules",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Credentials_EnvironmentId_Reference",
                table: "Credentials",
                columns: new[] { "EnvironmentId", "Reference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DiagnosticHypotheses_SessionId_Rank",
                table: "DiagnosticHypotheses",
                columns: new[] { "SessionId", "Rank" });

            migrationBuilder.CreateIndex(
                name: "IX_DiagnosticSessions_EnvironmentId_CreatedAt",
                table: "DiagnosticSessions",
                columns: new[] { "EnvironmentId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DiagnosticSessions_ReferenceSessionId",
                table: "DiagnosticSessions",
                column: "ReferenceSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_DiagnosticSignatures_Code",
                table: "DiagnosticSignatures",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DocumentSections_DocumentId_Ordinal",
                table: "DocumentSections",
                columns: new[] { "DocumentId", "Ordinal" });

            migrationBuilder.CreateIndex(
                name: "IX_EdiFiles_ComponentId_FileName",
                table: "EdiFiles",
                columns: new[] { "ComponentId", "FileName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EdiFiles_ComponentId_Status",
                table: "EdiFiles",
                columns: new[] { "ComponentId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_EnvironmentGrants_EnvironmentId",
                table: "EnvironmentGrants",
                column: "EnvironmentId");

            migrationBuilder.CreateIndex(
                name: "IX_EnvironmentGrants_UserId_EnvironmentId",
                table: "EnvironmentGrants",
                columns: new[] { "UserId", "EnvironmentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EnvironmentLocks_EnvironmentId",
                table: "EnvironmentLocks",
                column: "EnvironmentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Environments_Code",
                table: "Environments",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Executions_CorrelationId",
                table: "Executions",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_Executions_EnvironmentId_Status",
                table: "Executions",
                columns: new[] { "EnvironmentId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Executions_StartedAt",
                table: "Executions",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Executions_WorkflowId",
                table: "Executions",
                column: "WorkflowId");

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionSteps_ExecutionId_Order",
                table: "ExecutionSteps",
                columns: new[] { "ExecutionId", "Order" });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalActionDeclarations_DiagnosticSessionId",
                table: "ExternalActionDeclarations",
                column: "DiagnosticSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalActionDeclarations_WorkflowExecutionId",
                table: "ExternalActionDeclarations",
                column: "WorkflowExecutionId");

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeDocuments_Reference",
                table: "KnowledgeDocuments",
                column: "Reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeFeedback_SectionId",
                table: "KnowledgeFeedback",
                column: "SectionId");

            migrationBuilder.CreateIndex(
                name: "IX_LogFindings_SessionId",
                table: "LogFindings",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_LogFindings_SignatureCode",
                table: "LogFindings",
                column: "SignatureCode");

            migrationBuilder.CreateIndex(
                name: "IX_LogFindings_SourceId",
                table: "LogFindings",
                column: "SourceId");

            migrationBuilder.CreateIndex(
                name: "IX_LogSources_SessionId",
                table: "LogSources",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_PasswordHistoryRecords_UserId",
                table: "PasswordHistoryRecords",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_PhaseTransitions_SessionId",
                table: "PhaseTransitions",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_Servers_EnvironmentId_HostName",
                table: "Servers",
                columns: new[] { "EnvironmentId", "HostName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SharedFolderSnapshots_ComponentId_CapturedAt",
                table: "SharedFolderSnapshots",
                columns: new[] { "ComponentId", "CapturedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SharedFolderSnapshots_CorruptionConfirmed",
                table: "SharedFolderSnapshots",
                column: "CorruptionConfirmed");

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

            migrationBuilder.CreateIndex(
                name: "IX_Workflows_EnvironmentId_Code_Version",
                table: "Workflows",
                columns: new[] { "EnvironmentId", "Code", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowSteps_ComponentId",
                table: "WorkflowSteps",
                column: "ComponentId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowSteps_WorkflowId_Order",
                table: "WorkflowSteps",
                columns: new[] { "WorkflowId", "Order" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Alerts");

            migrationBuilder.DropTable(
                name: "ApprovalMatrixRules");

            migrationBuilder.DropTable(
                name: "AspNetRoleClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserLogins");

            migrationBuilder.DropTable(
                name: "AspNetUserPasskeys");

            migrationBuilder.DropTable(
                name: "AspNetUserRoles");

            migrationBuilder.DropTable(
                name: "AspNetUserTokens");

            migrationBuilder.DropTable(
                name: "AuditEntries");

            migrationBuilder.DropTable(
                name: "AzureAdSettings");

            migrationBuilder.DropTable(
                name: "ComponentDependencies");

            migrationBuilder.DropTable(
                name: "ComponentSignals");

            migrationBuilder.DropTable(
                name: "CorrelationConditions");

            migrationBuilder.DropTable(
                name: "Credentials");

            migrationBuilder.DropTable(
                name: "DiagnosticHypotheses");

            migrationBuilder.DropTable(
                name: "DiagnosticSettings");

            migrationBuilder.DropTable(
                name: "DiagnosticSignatures");

            migrationBuilder.DropTable(
                name: "EdiFiles");

            migrationBuilder.DropTable(
                name: "EnvironmentGrants");

            migrationBuilder.DropTable(
                name: "EnvironmentLocks");

            migrationBuilder.DropTable(
                name: "ExecutionSteps");

            migrationBuilder.DropTable(
                name: "ExternalActionDeclarations");

            migrationBuilder.DropTable(
                name: "KnowledgeFeedback");

            migrationBuilder.DropTable(
                name: "LogFindings");

            migrationBuilder.DropTable(
                name: "PasswordHistoryRecords");

            migrationBuilder.DropTable(
                name: "PhaseTransitions");

            migrationBuilder.DropTable(
                name: "RetentionPolicies");

            migrationBuilder.DropTable(
                name: "SharedFolderSnapshots");

            migrationBuilder.DropTable(
                name: "SopAssociations");

            migrationBuilder.DropTable(
                name: "SopExecutionSteps");

            migrationBuilder.DropTable(
                name: "SopSteps");

            migrationBuilder.DropTable(
                name: "WorkflowSteps");

            migrationBuilder.DropTable(
                name: "AspNetRoles");

            migrationBuilder.DropTable(
                name: "CorrelationRules");

            migrationBuilder.DropTable(
                name: "Executions");

            migrationBuilder.DropTable(
                name: "DocumentSections");

            migrationBuilder.DropTable(
                name: "LogSources");

            migrationBuilder.DropTable(
                name: "AspNetUsers");

            migrationBuilder.DropTable(
                name: "SopExecutions");

            migrationBuilder.DropTable(
                name: "Components");

            migrationBuilder.DropTable(
                name: "Workflows");

            migrationBuilder.DropTable(
                name: "KnowledgeDocuments");

            migrationBuilder.DropTable(
                name: "DiagnosticSessions");

            migrationBuilder.DropTable(
                name: "Sops");

            migrationBuilder.DropTable(
                name: "Servers");

            migrationBuilder.DropTable(
                name: "Environments");
        }
    }
}
