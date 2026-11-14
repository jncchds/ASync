using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ASync.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FolderMaintenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CheckIntervalDays",
                table: "Folders",
                type: "integer",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddColumn<int>(
                name: "CheckReadDataPercent",
                table: "Folders",
                type: "integer",
                nullable: false,
                defaultValue: 10);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastCheckAt",
                table: "Folders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastCheckBy",
                table: "Folders",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastCheckMessage",
                table: "Folders",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "LastCheckOk",
                table: "Folders",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastPruneAt",
                table: "Folders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastPruneBy",
                table: "Folders",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastPruneMessage",
                table: "Folders",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "LastPruneOk",
                table: "Folders",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PruneIntervalDays",
                table: "Folders",
                type: "integer",
                nullable: false,
                defaultValue: 7);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CheckIntervalDays",
                table: "Folders");

            migrationBuilder.DropColumn(
                name: "CheckReadDataPercent",
                table: "Folders");

            migrationBuilder.DropColumn(
                name: "LastCheckAt",
                table: "Folders");

            migrationBuilder.DropColumn(
                name: "LastCheckBy",
                table: "Folders");

            migrationBuilder.DropColumn(
                name: "LastCheckMessage",
                table: "Folders");

            migrationBuilder.DropColumn(
                name: "LastCheckOk",
                table: "Folders");

            migrationBuilder.DropColumn(
                name: "LastPruneAt",
                table: "Folders");

            migrationBuilder.DropColumn(
                name: "LastPruneBy",
                table: "Folders");

            migrationBuilder.DropColumn(
                name: "LastPruneMessage",
                table: "Folders");

            migrationBuilder.DropColumn(
                name: "LastPruneOk",
                table: "Folders");

            migrationBuilder.DropColumn(
                name: "PruneIntervalDays",
                table: "Folders");
        }
    }
}
