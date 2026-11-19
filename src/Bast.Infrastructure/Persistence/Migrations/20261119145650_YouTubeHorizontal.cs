using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bast.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class YouTubeHorizontal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Lane",
                table: "videos",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "Standard");

            migrationBuilder.AddColumn<TimeOnly>(
                name: "HorizontalEndTime",
                table: "schedule_settings",
                type: "TEXT",
                nullable: false,
                defaultValue: new TimeOnly(23, 0, 0));

            migrationBuilder.AddColumn<int>(
                name: "HorizontalPerDay",
                table: "schedule_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 7);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "HorizontalStartTime",
                table: "schedule_settings",
                type: "TEXT",
                nullable: false,
                defaultValue: new TimeOnly(6, 0, 0));

            migrationBuilder.AddColumn<string>(
                name: "Warning",
                table: "publications",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Lane",
                table: "videos");

            migrationBuilder.DropColumn(
                name: "HorizontalEndTime",
                table: "schedule_settings");

            migrationBuilder.DropColumn(
                name: "HorizontalPerDay",
                table: "schedule_settings");

            migrationBuilder.DropColumn(
                name: "HorizontalStartTime",
                table: "schedule_settings");

            migrationBuilder.DropColumn(
                name: "Warning",
                table: "publications");
        }
    }
}
