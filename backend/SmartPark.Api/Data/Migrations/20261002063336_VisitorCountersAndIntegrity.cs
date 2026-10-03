using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartPark.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class VisitorCountersAndIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Unit",
                table: "VisitorCounterSamples",
                type: "text",
                nullable: false,
                defaultValue: "People");

            migrationBuilder.AddColumn<int>(
                name: "VisitorInsideCount",
                table: "Parks",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql("""
                UPDATE "Parks"
                SET "VisitorInsideCount" = COALESCE(
                    (SELECT GREATEST(0, SUM(CASE WHEN "Direction" = 'In' THEN "Count" ELSE -"Count" END))::integer FROM "VisitorCounterSamples"),
                    0);
                """);

            migrationBuilder.AddCheckConstraint(
                name: "CK_VisitorCounterSamples_Count",
                table: "VisitorCounterSamples",
                sql: "\"Count\" > 0 AND \"Count\" <= 10000");

            migrationBuilder.AddCheckConstraint(
                name: "CK_VisitorCounterSamples_Direction",
                table: "VisitorCounterSamples",
                sql: "\"Direction\" IN ('In', 'Out')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_VisitorCounterSamples_Source",
                table: "VisitorCounterSamples",
                sql: "\"Source\" IN ('Seed', 'Simulation', 'Manual')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_VisitorCounterSamples_Unit",
                table: "VisitorCounterSamples",
                sql: "\"Unit\" = 'People'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_TelemetrySamples_Source",
                table: "TelemetrySamples",
                sql: "\"Source\" IN ('Seed', 'Simulation', 'Manual')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Parks_VisitorInsideCount_NonNegative",
                table: "Parks",
                sql: "\"VisitorInsideCount\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ActivitySessions_Capacity",
                table: "ActivitySessions",
                sql: "\"Capacity\" > 0 AND \"ReservedCount\" >= 0 AND \"ReservedCount\" <= \"Capacity\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_VisitorCounterSamples_Count",
                table: "VisitorCounterSamples");

            migrationBuilder.DropCheckConstraint(
                name: "CK_VisitorCounterSamples_Direction",
                table: "VisitorCounterSamples");

            migrationBuilder.DropCheckConstraint(
                name: "CK_VisitorCounterSamples_Source",
                table: "VisitorCounterSamples");

            migrationBuilder.DropCheckConstraint(
                name: "CK_VisitorCounterSamples_Unit",
                table: "VisitorCounterSamples");

            migrationBuilder.DropCheckConstraint(
                name: "CK_TelemetrySamples_Source",
                table: "TelemetrySamples");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Parks_VisitorInsideCount_NonNegative",
                table: "Parks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ActivitySessions_Capacity",
                table: "ActivitySessions");

            migrationBuilder.DropColumn(
                name: "Unit",
                table: "VisitorCounterSamples");

            migrationBuilder.DropColumn(
                name: "VisitorInsideCount",
                table: "Parks");
        }
    }
}
