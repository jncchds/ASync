using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ASync.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FolderChangeDelay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ChangeDelaySeconds",
                table: "Folders",
                type: "integer",
                nullable: false,
                defaultValue: 5);

            migrationBuilder.AddColumn<int>(
                name: "ChangeMaxDelaySeconds",
                table: "Folders",
                type: "integer",
                nullable: false,
                defaultValue: 600);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ChangeDelaySeconds",
                table: "Folders");

            migrationBuilder.DropColumn(
                name: "ChangeMaxDelaySeconds",
                table: "Folders");
        }
    }
}
