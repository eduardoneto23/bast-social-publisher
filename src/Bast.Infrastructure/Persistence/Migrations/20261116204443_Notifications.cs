using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bast.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Notifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "notifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    DedupKey = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SentAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DiscardedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notifications", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_notifications_DedupKey",
                table: "notifications",
                column: "DedupKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_notifications_SentAtUtc_DiscardedAtUtc_CreatedAtUtc",
                table: "notifications",
                columns: new[] { "SentAtUtc", "DiscardedAtUtc", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notifications");
        }
    }
}
