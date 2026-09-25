using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GGdown.Core.Migrations
{
    [DbContext(typeof(GGdown.Data.GGdownDbContext))]
    [Migration("20260903143000_AddAccountRestId")]
    public partial class AddAccountRestId : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RestId",
                table: "Accounts",
                type: "TEXT",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RestId",
                table: "Accounts");
        }
    }
}
