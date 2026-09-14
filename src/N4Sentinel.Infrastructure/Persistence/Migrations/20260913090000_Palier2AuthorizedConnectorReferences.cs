using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace N4Sentinel.Infrastructure.Persistence.Migrations;

[DbContext(typeof(N4SentinelDbContext))]
[Migration("20260913090000_Palier2AuthorizedConnectorReferences")]
public sealed class Palier2AuthorizedConnectorReferences : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Palier2AuthorizedConnectorReferences",
            table: "Environments",
            type: "nvarchar(4000)",
            maxLength: 4000,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "Palier2AuthorizedConnectorReferences",
            table: "Environments");
    }
}
