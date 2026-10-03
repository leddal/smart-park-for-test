using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartPark.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class EventReferenceConstraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_ParkEvents_AssetId",
                table: "ParkEvents",
                column: "AssetId");

            migrationBuilder.AddForeignKey(
                name: "FK_ParkEvents_Alerts_AlertId",
                table: "ParkEvents",
                column: "AlertId",
                principalTable: "Alerts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ParkEvents_Assets_AssetId",
                table: "ParkEvents",
                column: "AssetId",
                principalTable: "Assets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ParkEvents_Alerts_AlertId",
                table: "ParkEvents");

            migrationBuilder.DropForeignKey(
                name: "FK_ParkEvents_Assets_AssetId",
                table: "ParkEvents");

            migrationBuilder.DropIndex(
                name: "IX_ParkEvents_AssetId",
                table: "ParkEvents");
        }
    }
}
