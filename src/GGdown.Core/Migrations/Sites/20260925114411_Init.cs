using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GGdown.Core.Migrations.Sites
{
    /// <inheritdoc />
    public partial class Init : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Accounts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SiteId = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: true),
                    ScreenName = table.Column<string>(type: "TEXT", nullable: true),
                    RestId = table.Column<string>(type: "TEXT", nullable: true),
                    CookiePath = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    AddedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    VerifiedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Accounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FollowingCache",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SiteId = table.Column<string>(type: "TEXT", nullable: false),
                    RestId = table.Column<string>(type: "TEXT", nullable: false),
                    ScreenName = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: true),
                    AvatarUrl = table.Column<string>(type: "TEXT", nullable: true),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    FetchedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FollowingCache", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SiteSettings",
                columns: table => new
                {
                    SiteId = table.Column<string>(type: "TEXT", nullable: false),
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SiteSettings", x => new { x.SiteId, x.Key });
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SiteId = table.Column<string>(type: "TEXT", nullable: false),
                    RestId = table.Column<string>(type: "TEXT", nullable: false),
                    ScreenName = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: true),
                    AvatarUrl = table.Column<string>(type: "TEXT", nullable: true),
                    BannerUrl = table.Column<string>(type: "TEXT", nullable: true),
                    Bio = table.Column<string>(type: "TEXT", nullable: true),
                    ProfileUrl = table.Column<string>(type: "TEXT", nullable: true),
                    FollowersCount = table.Column<long>(type: "INTEGER", nullable: true),
                    MediaCount = table.Column<long>(type: "INTEGER", nullable: true),
                    MediaCountAtDownload = table.Column<long>(type: "INTEGER", nullable: true),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    OwnerAccountId = table.Column<long>(type: "INTEGER", nullable: true),
                    IsPinned = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsSkipped = table.Column<bool>(type: "INTEGER", nullable: false),
                    InDownloadList = table.Column<bool>(type: "INTEGER", nullable: false),
                    DownloadCount = table.Column<long>(type: "INTEGER", nullable: false),
                    LastDownloadAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AddedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Jobs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SiteId = table.Column<string>(type: "TEXT", nullable: false, defaultValue: ""),
                    AccountId = table.Column<long>(type: "INTEGER", nullable: false),
                    TargetKind = table.Column<int>(type: "INTEGER", nullable: false),
                    UserId = table.Column<long>(type: "INTEGER", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalFiles = table.Column<long>(type: "INTEGER", nullable: false),
                    DoneFiles = table.Column<long>(type: "INTEGER", nullable: false),
                    SkippedFiles = table.Column<long>(type: "INTEGER", nullable: false),
                    FailedFiles = table.Column<long>(type: "INTEGER", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Jobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Jobs_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Jobs_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Files",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SiteId = table.Column<string>(type: "TEXT", nullable: false, defaultValue: ""),
                    JobId = table.Column<long>(type: "INTEGER", nullable: false),
                    UserId = table.Column<long>(type: "INTEGER", nullable: true),
                    SourceItemId = table.Column<string>(type: "TEXT", nullable: true),
                    Url = table.Column<string>(type: "TEXT", nullable: false),
                    FilePath = table.Column<string>(type: "TEXT", nullable: false),
                    FileSize = table.Column<long>(type: "INTEGER", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Files", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Files_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_SiteId_IsActive",
                table: "Accounts",
                columns: new[] { "SiteId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_Files_JobId",
                table: "Files",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_Files_UserId",
                table: "Files",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_FollowingCache_SiteId_RestId",
                table: "FollowingCache",
                columns: new[] { "SiteId", "RestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FollowingCache_SiteId_SortOrder",
                table: "FollowingCache",
                columns: new[] { "SiteId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_AccountId",
                table: "Jobs",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_SiteId",
                table: "Jobs",
                column: "SiteId");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_Status",
                table: "Jobs",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_UserId",
                table: "Jobs",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Users_LastDownloadAt",
                table: "Users",
                column: "LastDownloadAt");

            migrationBuilder.CreateIndex(
                name: "IX_Users_ScreenName",
                table: "Users",
                column: "ScreenName");

            migrationBuilder.CreateIndex(
                name: "IX_Users_SiteId_RestId",
                table: "Users",
                columns: new[] { "SiteId", "RestId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Files");

            migrationBuilder.DropTable(
                name: "FollowingCache");

            migrationBuilder.DropTable(
                name: "SiteSettings");

            migrationBuilder.DropTable(
                name: "Jobs");

            migrationBuilder.DropTable(
                name: "Accounts");

            migrationBuilder.DropTable(
                name: "Users");
        }
    }
}
