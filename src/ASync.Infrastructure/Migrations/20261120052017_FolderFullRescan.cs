using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ASync.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FolderFullRescan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FullRescanHours",
                table: "Folders",
                type: "integer",
                nullable: false,
                defaultValue: 12);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FullRescanHours",
                table: "Folders");
        }
    }
}
