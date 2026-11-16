using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ASync.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PushTargetsAndRunConditions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "BatteryNotLow",
                table: "FolderDevices",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ChargingOnly",
                table: "FolderDevices",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "UnmeteredOnly",
                table: "FolderDevices",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastPushAt",
                table: "Devices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastPushError",
                table: "Devices",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "LastPushOk",
                table: "Devices",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PushRegisteredAt",
                table: "Devices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PushTarget",
                table: "Devices",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PushType",
                table: "Devices",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BatteryNotLow",
                table: "FolderDevices");

            migrationBuilder.DropColumn(
                name: "ChargingOnly",
                table: "FolderDevices");

            migrationBuilder.DropColumn(
                name: "UnmeteredOnly",
                table: "FolderDevices");

            migrationBuilder.DropColumn(
                name: "LastPushAt",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "LastPushError",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "LastPushOk",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "PushRegisteredAt",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "PushTarget",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "PushType",
                table: "Devices");
        }
    }
}
