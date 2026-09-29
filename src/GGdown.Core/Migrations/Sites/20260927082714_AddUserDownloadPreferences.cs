using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GGdown.Core.Migrations.Sites
{
    /// <inheritdoc />
    public partial class AddUserDownloadPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ContentSelection",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateOnly>(
                name: "DownloadSince",
                table: "Users",
                type: "TEXT",
                nullable: true);

            // 从旧的 Pixiv 站点开关迁移单类型偏好；两项都开启或都关闭时使用新的“全部”默认值。
            migrationBuilder.Sql("""
                UPDATE "Users" SET "ContentSelection" = 1
                WHERE "SiteId" = 'pixiv'
                  AND EXISTS (SELECT 1 FROM "SiteSettings" WHERE "SiteId" = 'pixiv' AND "Key" = 'download_novels' AND lower("Value") = 'false')
                  AND NOT EXISTS (SELECT 1 FROM "SiteSettings" WHERE "SiteId" = 'pixiv' AND "Key" = 'download_artworks' AND lower("Value") = 'false');
                """);
            migrationBuilder.Sql("""
                UPDATE "Users" SET "ContentSelection" = 2
                WHERE "SiteId" = 'pixiv'
                  AND EXISTS (SELECT 1 FROM "SiteSettings" WHERE "SiteId" = 'pixiv' AND "Key" = 'download_artworks' AND lower("Value") = 'false')
                  AND NOT EXISTS (SELECT 1 FROM "SiteSettings" WHERE "SiteId" = 'pixiv' AND "Key" = 'download_novels' AND lower("Value") = 'false');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ContentSelection",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "DownloadSince",
                table: "Users");
        }
    }
}
