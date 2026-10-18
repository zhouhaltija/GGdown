using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GGdown.Core.Migrations.Sites
{
    /// <inheritdoc />
    public partial class HardenQueueAndAccountState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FollowingCache_SiteId_RestId",
                table: "FollowingCache");

            migrationBuilder.DropIndex(
                name: "IX_FollowingCache_SiteId_SortOrder",
                table: "FollowingCache");

            migrationBuilder.AddColumn<long>(
                name: "MediaCountSnapshot",
                table: "Jobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "StatisticsPending",
                table: "Jobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "AccountId",
                table: "FollowingCache",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "EventSequence",
                table: "Files",
                type: "INTEGER",
                nullable: true);

            // 旧版本可能留下多个活动账号；保留最新一条，原账号与历史记录不删除。
            migrationBuilder.Sql("UPDATE Accounts SET IsActive = 0 WHERE IsActive = 1 AND Id NOT IN (SELECT MAX(Id) FROM Accounts WHERE IsActive = 1 GROUP BY SiteId);");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_StatisticsPending",
                table: "Jobs",
                column: "StatisticsPending");

            migrationBuilder.CreateIndex(
                name: "IX_FollowingCache_SiteId_AccountId_RestId",
                table: "FollowingCache",
                columns: new[] { "SiteId", "AccountId", "RestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FollowingCache_SiteId_AccountId_SortOrder",
                table: "FollowingCache",
                columns: new[] { "SiteId", "AccountId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_Files_CreatedAt_Id",
                table: "Files",
                columns: new[] { "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Files_JobId_EventSequence",
                table: "Files",
                columns: new[] { "JobId", "EventSequence" },
                unique: true,
                filter: "\"EventSequence\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Files_UserId_CreatedAt_Id",
                table: "Files",
                columns: new[] { "UserId", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_SiteId",
                table: "Accounts",
                column: "SiteId",
                unique: true,
                filter: "\"IsActive\" = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Jobs_StatisticsPending",
                table: "Jobs");

            migrationBuilder.DropIndex(
                name: "IX_FollowingCache_SiteId_AccountId_RestId",
                table: "FollowingCache");

            migrationBuilder.DropIndex(
                name: "IX_FollowingCache_SiteId_AccountId_SortOrder",
                table: "FollowingCache");

            migrationBuilder.DropIndex(
                name: "IX_Files_CreatedAt_Id",
                table: "Files");

            migrationBuilder.DropIndex(
                name: "IX_Files_JobId_EventSequence",
                table: "Files");

            migrationBuilder.DropIndex(
                name: "IX_Files_UserId_CreatedAt_Id",
                table: "Files");

            migrationBuilder.DropIndex(
                name: "IX_Accounts_SiteId",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "MediaCountSnapshot",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "StatisticsPending",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "AccountId",
                table: "FollowingCache");

            migrationBuilder.DropColumn(
                name: "EventSequence",
                table: "Files");

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
    }
}
