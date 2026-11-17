using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ASync.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DevicesAddFolders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CreatedByDeviceId",
                table: "Folders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByDeviceName",
                table: "Folders",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "MayAddFolders",
                table: "Devices",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreatedByDeviceId",
                table: "Folders");

            migrationBuilder.DropColumn(
                name: "CreatedByDeviceName",
                table: "Folders");

            migrationBuilder.DropColumn(
                name: "MayAddFolders",
                table: "Devices");
        }
    }
}
