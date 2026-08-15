using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace N4Sentinel.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// FR-092 : le journal d'audit ne doit pas être modifiable par les
    /// opérateurs. Jusqu'ici cette garantie n'était qu'applicative (aucune
    /// méthode d'update exposée) alors que le commentaire d'AuditEntry.cs
    /// affirmait une contrainte "côté base" qui n'existait pas. Ce trigger la
    /// rend réelle : toute tentative d'UPDATE ou de DELETE sur AuditEntries,
    /// y compris via un accès direct à la base hors de l'application, échoue.
    /// </summary>
    public partial class AuditEntriesImmuables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE TRIGGER TR_AuditEntries_Immuable
                ON dbo.AuditEntries
                INSTEAD OF UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    RAISERROR('Le journal d''audit est immuable (FR-092) : aucune modification ni suppression n''est autorisee, meme par un acces direct a la base.', 16, 1);
                    ROLLBACK TRANSACTION;
                END
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS TR_AuditEntries_Immuable;");
        }
    }
}
