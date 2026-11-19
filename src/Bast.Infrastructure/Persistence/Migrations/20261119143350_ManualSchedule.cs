using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bast.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ManualSchedule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ManualMaxPerDay",
                table: "schedule_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.AddColumn<string>(
                name: "Mode",
                table: "schedule_settings",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "Automatic");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ManualMaxPerDay",
                table: "schedule_settings");

            migrationBuilder.DropColumn(
                name: "Mode",
                table: "schedule_settings");
        }
    }
}
