using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bast.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Production : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoPublishApproved",
                table: "schedule_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "production_control",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Stopped = table.Column<bool>(type: "INTEGER", nullable: false),
                    StopReason = table.Column<string>(type: "TEXT", nullable: true),
                    StoppedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_control", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "production_jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceFileName = table.Column<string>(type: "TEXT", nullable: false),
                    SourcePath = table.Column<string>(type: "TEXT", nullable: false),
                    Fingerprint = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    ReceivedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DurationSeconds = table.Column<double>(type: "REAL", nullable: false),
                    Width = table.Column<int>(type: "INTEGER", nullable: false),
                    Height = table.Column<int>(type: "INTEGER", nullable: false),
                    Fps = table.Column<double>(type: "REAL", nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    AudioTracks = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    CurrentStep = table.Column<string>(type: "TEXT", nullable: false),
                    Progress = table.Column<int>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    Authorized = table.Column<bool>(type: "INTEGER", nullable: false),
                    AuthorizedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Paused = table.Column<bool>(type: "INTEGER", nullable: false),
                    PauseReason = table.Column<string>(type: "TEXT", nullable: true),
                    BlockedReason = table.Column<string>(type: "TEXT", nullable: true),
                    WorkFolder = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_jobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "production_logs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Level = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    Step = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    Program = table.Column<string>(type: "TEXT", nullable: true),
                    File = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_logs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "production_steps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FinishedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Output = table.Column<string>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_steps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_production_steps_production_jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "production_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_production_jobs_Fingerprint",
                table: "production_jobs",
                column: "Fingerprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_jobs_Status",
                table: "production_jobs",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_production_logs_JobId_AtUtc",
                table: "production_logs",
                columns: new[] { "JobId", "AtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_production_steps_JobId_Name",
                table: "production_steps",
                columns: new[] { "JobId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "production_control");

            migrationBuilder.DropTable(
                name: "production_logs");

            migrationBuilder.DropTable(
                name: "production_steps");

            migrationBuilder.DropTable(
                name: "production_jobs");

            migrationBuilder.DropColumn(
                name: "AutoPublishApproved",
                table: "schedule_settings");
        }
    }
}
