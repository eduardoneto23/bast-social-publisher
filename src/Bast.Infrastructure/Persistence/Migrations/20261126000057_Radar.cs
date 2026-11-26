using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bast.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Radar : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "radar_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FinishedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Trigger = table.Column<string>(type: "TEXT", nullable: false),
                    Summary = table.Column<string>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    Shortfall = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_radar_runs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "radar_settings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    DailyTime = table.Column<string>(type: "TEXT", nullable: false),
                    ManualRequestedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    YouTubeSearchBudget = table.Column<int>(type: "INTEGER", nullable: false),
                    SearchesDate = table.Column<string>(type: "TEXT", nullable: true),
                    SearchesUsed = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_radar_settings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "radar_tags",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Synonyms = table.Column<string>(type: "TEXT", nullable: false),
                    IncludeTerms = table.Column<string>(type: "TEXT", nullable: false),
                    ExcludeTerms = table.Column<string>(type: "TEXT", nullable: false),
                    Active = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_radar_tags", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "radar_video_snapshots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    VideoId = table.Column<string>(type: "TEXT", nullable: false),
                    AtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Views = table.Column<long>(type: "INTEGER", nullable: true),
                    Likes = table.Column<long>(type: "INTEGER", nullable: true),
                    Comments = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_radar_video_snapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "studio_productions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Code = table.Column<string>(type: "TEXT", nullable: false),
                    SuggestionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TagName = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Format = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    PlanJson = table.Column<string>(type: "TEXT", nullable: false),
                    SourcesJson = table.Column<string>(type: "TEXT", nullable: false),
                    Folder = table.Column<string>(type: "TEXT", nullable: false),
                    FolderName = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    StatusDetail = table.Column<string>(type: "TEXT", nullable: true),
                    FilesJson = table.Column<string>(type: "TEXT", nullable: false),
                    MissingScenes = table.Column<string>(type: "TEXT", nullable: true),
                    RecordingFinishedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReleasedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_studio_productions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "radar_suggestions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TagId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TagName = table.Column<string>(type: "TEXT", nullable: false),
                    Format = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Rank = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Category = table.Column<string>(type: "TEXT", nullable: false),
                    Market = table.Column<string>(type: "TEXT", nullable: false),
                    Opportunity = table.Column<string>(type: "TEXT", nullable: false),
                    DiscoveredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SourcesJson = table.Column<string>(type: "TEXT", nullable: false),
                    AnalysisJson = table.Column<string>(type: "TEXT", nullable: false),
                    PlanJson = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    SavedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IgnoredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    StudioProductionId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_radar_suggestions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_radar_suggestions_radar_runs_RunId",
                        column: x => x.RunId,
                        principalTable: "radar_runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_radar_runs_StartedAtUtc",
                table: "radar_runs",
                column: "StartedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_radar_suggestions_RunId_Format_Rank",
                table: "radar_suggestions",
                columns: new[] { "RunId", "Format", "Rank" });

            migrationBuilder.CreateIndex(
                name: "IX_radar_suggestions_TagId",
                table: "radar_suggestions",
                column: "TagId");

            migrationBuilder.CreateIndex(
                name: "IX_radar_tags_Name",
                table: "radar_tags",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_radar_video_snapshots_VideoId_AtUtc",
                table: "radar_video_snapshots",
                columns: new[] { "VideoId", "AtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_studio_productions_Code",
                table: "studio_productions",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_studio_productions_SuggestionId",
                table: "studio_productions",
                column: "SuggestionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "radar_settings");

            migrationBuilder.DropTable(
                name: "radar_suggestions");

            migrationBuilder.DropTable(
                name: "radar_tags");

            migrationBuilder.DropTable(
                name: "radar_video_snapshots");

            migrationBuilder.DropTable(
                name: "studio_productions");

            migrationBuilder.DropTable(
                name: "radar_runs");
        }
    }
}
