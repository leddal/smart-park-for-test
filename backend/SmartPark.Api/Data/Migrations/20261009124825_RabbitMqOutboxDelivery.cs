using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartPark.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class RabbitMqOutboxDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_IntegrationAttempts_OutboxMessageId_Attempt",
                table: "IntegrationAttempts");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ConsumedAt",
                table: "OutboxMessages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Generation",
                table: "OutboxMessages",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PublishAttempts",
                table: "OutboxMessages",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PublishFailures",
                table: "OutboxMessages",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PublishedAt",
                table: "OutboxMessages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Generation",
                table: "IntegrationAttempts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Stage",
                table: "IntegrationAttempts",
                type: "text",
                nullable: false,
                defaultValue: "Consume");

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationAttempts_OutboxMessageId_Generation_Stage_Attempt",
                table: "IntegrationAttempts",
                columns: new[] { "OutboxMessageId", "Generation", "Stage", "Attempt" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_IntegrationAttempts_OutboxMessageId_Generation_Stage_Attempt",
                table: "IntegrationAttempts");

            migrationBuilder.DropColumn(
                name: "ConsumedAt",
                table: "OutboxMessages");

            migrationBuilder.DropColumn(
                name: "Generation",
                table: "OutboxMessages");

            migrationBuilder.DropColumn(
                name: "PublishAttempts",
                table: "OutboxMessages");

            migrationBuilder.DropColumn(
                name: "PublishFailures",
                table: "OutboxMessages");

            migrationBuilder.DropColumn(
                name: "PublishedAt",
                table: "OutboxMessages");

            migrationBuilder.DropColumn(
                name: "Generation",
                table: "IntegrationAttempts");

            migrationBuilder.DropColumn(
                name: "Stage",
                table: "IntegrationAttempts");

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationAttempts_OutboxMessageId_Attempt",
                table: "IntegrationAttempts",
                columns: new[] { "OutboxMessageId", "Attempt" },
                unique: true);
        }
    }
}
