using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GalleryGUI.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddUserIsSkipped : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsSkipped",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsSkipped",
                table: "Users");
        }
    }
}
