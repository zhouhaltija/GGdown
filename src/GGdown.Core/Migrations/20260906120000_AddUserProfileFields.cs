using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GGdown.Core.Migrations
{
    [DbContext(typeof(GGdown.Data.GGdownDbContext))]
    [Migration("20260906120000_AddUserProfileFields")]
    public partial class AddUserProfileFields : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(name: "BannerUrl", table: "Users", type: "TEXT", nullable: true);
            migrationBuilder.AddColumn<string>(name: "Bio", table: "Users", type: "TEXT", nullable: true);
            migrationBuilder.AddColumn<long>(name: "FollowersCount", table: "Users", type: "INTEGER", nullable: true);
            migrationBuilder.AddColumn<long>(name: "MediaCount", table: "Users", type: "INTEGER", nullable: true);
            migrationBuilder.AddColumn<long>(name: "MediaCountAtDownload", table: "Users", type: "INTEGER", nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "BannerUrl", table: "Users");
            migrationBuilder.DropColumn(name: "Bio", table: "Users");
            migrationBuilder.DropColumn(name: "FollowersCount", table: "Users");
            migrationBuilder.DropColumn(name: "MediaCount", table: "Users");
            migrationBuilder.DropColumn(name: "MediaCountAtDownload", table: "Users");
        }
    }
}
