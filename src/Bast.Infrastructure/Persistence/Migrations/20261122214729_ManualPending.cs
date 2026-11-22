using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bast.Infrastructure.Persistence.Migrations
{
    /// <summary>Pasta PENDENTE MANUAL: verticais publicados no automático esperam a confirmação do envio manual (ligado por padrão).</summary>
    public partial class ManualPending : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ManualDoneAtUtc",
                table: "videos",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ManualPendingSinceUtc",
                table: "videos",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ManualPosting",
                table: "schedule_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ManualDoneAtUtc",
                table: "videos");

            migrationBuilder.DropColumn(
                name: "ManualPendingSinceUtc",
                table: "videos");

            migrationBuilder.DropColumn(
                name: "ManualPosting",
                table: "schedule_settings");
        }
    }
}
