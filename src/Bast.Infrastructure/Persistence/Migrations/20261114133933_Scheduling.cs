using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bast.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Scheduling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PendingMoveFolder",
                table: "videos",
                type: "TEXT",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ScheduledAtUtc",
                table: "videos",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Simulated",
                table: "publications",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "schedule_settings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    StartTime = table.Column<TimeOnly>(type: "TEXT", nullable: false),
                    EndTime = table.Column<TimeOnly>(type: "TEXT", nullable: false),
                    IntervalMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxPerDay = table.Column<int>(type: "INTEGER", nullable: false),
                    DaysMask = table.Column<int>(type: "INTEGER", nullable: false),
                    TimeZoneId = table.Column<string>(type: "TEXT", nullable: false),
                    PlatformsMask = table.Column<int>(type: "INTEGER", nullable: false),
                    MissedPolicy = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    MissedGraceMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    Paused = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_schedule_settings", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "schedule_settings");

            migrationBuilder.DropColumn(
                name: "PendingMoveFolder",
                table: "videos");

            migrationBuilder.DropColumn(
                name: "ScheduledAtUtc",
                table: "videos");

            migrationBuilder.DropColumn(
                name: "Simulated",
                table: "publications");
        }
    }
}
