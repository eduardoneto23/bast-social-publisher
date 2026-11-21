using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bast.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Horários fixos: os verticais passam a sair um por horário da lista (10:00, 16:00, 18:00) e os horizontais
    /// ganham a lista própria (08:00, 11:00, 14:00, 17:00, 20:00) no lugar da divisão igual entre duas janelas.
    /// A agenda existente passa para o modo de horários fixos.
    /// </summary>
    public partial class FixedTimes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "HorizontalPerDay", table: "schedule_settings");
            migrationBuilder.DropColumn(name: "HorizontalStartTime", table: "schedule_settings");
            migrationBuilder.DropColumn(name: "HorizontalEndTime", table: "schedule_settings");

            migrationBuilder.AddColumn<string>(
                name: "VerticalSlots",
                table: "schedule_settings",
                type: "TEXT",
                nullable: false,
                defaultValue: "10:00,16:00,18:00");

            migrationBuilder.AddColumn<string>(
                name: "HorizontalSlots",
                table: "schedule_settings",
                type: "TEXT",
                nullable: false,
                defaultValue: "08:00,11:00,14:00,17:00,20:00");

            migrationBuilder.Sql("UPDATE schedule_settings SET Mode = 'FixedTimes';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE schedule_settings SET Mode = 'Automatic' WHERE Mode = 'FixedTimes';");
            migrationBuilder.DropColumn(name: "VerticalSlots", table: "schedule_settings");
            migrationBuilder.DropColumn(name: "HorizontalSlots", table: "schedule_settings");

            migrationBuilder.AddColumn<string>(
                name: "HorizontalStartTime",
                table: "schedule_settings",
                type: "TEXT",
                nullable: false,
                defaultValue: "06:00:00");

            migrationBuilder.AddColumn<string>(
                name: "HorizontalEndTime",
                table: "schedule_settings",
                type: "TEXT",
                nullable: false,
                defaultValue: "23:00:00");

            migrationBuilder.AddColumn<int>(
                name: "HorizontalPerDay",
                table: "schedule_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 7);
        }
    }
}
