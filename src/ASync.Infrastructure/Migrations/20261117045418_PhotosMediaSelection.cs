using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ASync.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PhotosMediaSelection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "MediaBytes",
                table: "FolderDevices",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MediaPhotos",
                table: "FolderDevices",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MediaSelectionJson",
                table: "FolderDevices",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MediaVideos",
                table: "FolderDevices",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MediaAccess",
                table: "Devices",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "MediaAlbumsAt",
                table: "Devices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MediaAlbumsJson",
                table: "Devices",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MediaBytes",
                table: "FolderDevices");

            migrationBuilder.DropColumn(
                name: "MediaPhotos",
                table: "FolderDevices");

            migrationBuilder.DropColumn(
                name: "MediaSelectionJson",
                table: "FolderDevices");

            migrationBuilder.DropColumn(
                name: "MediaVideos",
                table: "FolderDevices");

            migrationBuilder.DropColumn(
                name: "MediaAccess",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "MediaAlbumsAt",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "MediaAlbumsJson",
                table: "Devices");
        }
    }
}
