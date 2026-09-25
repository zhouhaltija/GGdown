using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GGdown.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddFollowingCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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

            migrationBuilder.CreateIndex(
                name: "IX_FollowingCache_SiteId_RestId",
                table: "FollowingCache",
                columns: new[] { "SiteId", "RestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FollowingCache_SiteId_SortOrder",
                table: "FollowingCache",
                columns: new[] { "SiteId", "SortOrder" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FollowingCache");
        }
    }
}
