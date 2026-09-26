using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GGdown.Core.Migrations.Sites
{
    /// <inheritdoc />
    public partial class IgnoreFollowing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IgnoredFollowing",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SiteId = table.Column<string>(type: "TEXT", nullable: false),
                    AccountId = table.Column<long>(type: "INTEGER", nullable: false),
                    RestId = table.Column<string>(type: "TEXT", nullable: false),
                    IgnoredAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IgnoredFollowing", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IgnoredFollowing_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IgnoredFollowing_AccountId",
                table: "IgnoredFollowing",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_IgnoredFollowing_SiteId_AccountId_RestId",
                table: "IgnoredFollowing",
                columns: new[] { "SiteId", "AccountId", "RestId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IgnoredFollowing");
        }
    }
}
