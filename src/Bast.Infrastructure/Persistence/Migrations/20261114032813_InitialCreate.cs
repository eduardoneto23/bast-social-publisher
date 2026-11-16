using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bast.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "import_issues",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    FolderName = table.Column<string>(type: "TEXT", nullable: false),
                    Code = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    MovedToPath = table.Column<string>(type: "TEXT", nullable: true),
                    FirstSeenAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ResolvedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_import_issues", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "videos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    FolderName = table.Column<string>(type: "TEXT", nullable: false),
                    SequenceNumber = table.Column<long>(type: "INTEGER", nullable: true),
                    ContentHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Generation = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    CurrentFolder = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    RelativePath = table.Column<string>(type: "TEXT", nullable: false),
                    PendingMovePath = table.Column<string>(type: "TEXT", nullable: true),
                    VideoFileName = table.Column<string>(type: "TEXT", nullable: false),
                    CaptionFileName = table.Column<string>(type: "TEXT", nullable: false),
                    ThumbnailFileName = table.Column<string>(type: "TEXT", nullable: true),
                    CaptionText = table.Column<string>(type: "TEXT", nullable: false),
                    FileSizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    DurationSeconds = table.Column<double>(type: "REAL", nullable: false),
                    Width = table.Column<int>(type: "INTEGER", nullable: false),
                    Height = table.Column<int>(type: "INTEGER", nullable: false),
                    VideoCodec = table.Column<string>(type: "TEXT", nullable: false),
                    AudioCodec = table.Column<string>(type: "TEXT", nullable: true),
                    FrameRate = table.Column<double>(type: "REAL", nullable: false),
                    BitRate = table.Column<long>(type: "INTEGER", nullable: true),
                    FormatName = table.Column<string>(type: "TEXT", nullable: false),
                    Note = table.Column<string>(type: "TEXT", nullable: true),
                    ImportedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_videos", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "publications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    VideoId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Platform = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    ScheduledAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NextRetryAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    ExternalId = table.Column<string>(type: "TEXT", nullable: true),
                    ExternalUrl = table.Column<string>(type: "TEXT", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "TEXT", nullable: false),
                    LeaseOwner = table.Column<string>(type: "TEXT", nullable: true),
                    LeaseExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_publications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_publications_videos_VideoId",
                        column: x => x.VideoId,
                        principalTable: "videos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "publication_attempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PublicationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Number = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FinishedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ResultState = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    ErrorCode = table.Column<string>(type: "TEXT", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_publication_attempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_publication_attempts_publications_PublicationId",
                        column: x => x.PublicationId,
                        principalTable: "publications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_import_issues_FolderName_Code_ResolvedAtUtc",
                table: "import_issues",
                columns: new[] { "FolderName", "Code", "ResolvedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_publication_attempts_PublicationId_Number",
                table: "publication_attempts",
                columns: new[] { "PublicationId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_publications_IdempotencyKey",
                table: "publications",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_publications_State_ScheduledAtUtc",
                table: "publications",
                columns: new[] { "State", "ScheduledAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_publications_VideoId_Platform",
                table: "publications",
                columns: new[] { "VideoId", "Platform" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_videos_ContentHash_Generation",
                table: "videos",
                columns: new[] { "ContentHash", "Generation" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_videos_State",
                table: "videos",
                column: "State");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "import_issues");

            migrationBuilder.DropTable(
                name: "publication_attempts");

            migrationBuilder.DropTable(
                name: "publications");

            migrationBuilder.DropTable(
                name: "videos");
        }
    }
}
