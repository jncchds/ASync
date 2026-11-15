using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ASync.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class QuotasUsageHealth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "QuotaBytes",
                table: "Users",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CheckFailures",
                table: "StorageBackends",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "UserQuotaBytes",
                table: "StorageBackends",
                type: "bigint",
                nullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "FolderId",
                table: "AlertIncidents",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<int>(
                name: "StorageBackendId",
                table: "AlertIncidents",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StorageBackendName",
                table: "AlertIncidents",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "FolderUsages",
                columns: table => new
                {
                    FolderId = table.Column<int>(type: "integer", nullable: false),
                    Bytes = table.Column<long>(type: "bigint", nullable: false),
                    Files = table.Column<long>(type: "bigint", nullable: false),
                    HistoryBytes = table.Column<long>(type: "bigint", nullable: true),
                    DataBytes = table.Column<long>(type: "bigint", nullable: true),
                    Snapshots = table.Column<int>(type: "integer", nullable: true),
                    PendingBytes = table.Column<long>(type: "bigint", nullable: false),
                    ScannedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ScanError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FolderUsages", x => x.FolderId);
                    table.ForeignKey(
                        name: "FK_FolderUsages_Folders_FolderId",
                        column: x => x.FolderId,
                        principalTable: "Folders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AlertIncidents_StorageBackendId",
                table: "AlertIncidents",
                column: "StorageBackendId");

            migrationBuilder.AddForeignKey(
                name: "FK_AlertIncidents_StorageBackends_StorageBackendId",
                table: "AlertIncidents",
                column: "StorageBackendId",
                principalTable: "StorageBackends",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AlertIncidents_StorageBackends_StorageBackendId",
                table: "AlertIncidents");

            migrationBuilder.DropTable(
                name: "FolderUsages");

            migrationBuilder.DropIndex(
                name: "IX_AlertIncidents_StorageBackendId",
                table: "AlertIncidents");

            migrationBuilder.DropColumn(
                name: "QuotaBytes",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "CheckFailures",
                table: "StorageBackends");

            migrationBuilder.DropColumn(
                name: "UserQuotaBytes",
                table: "StorageBackends");

            migrationBuilder.DropColumn(
                name: "StorageBackendId",
                table: "AlertIncidents");

            migrationBuilder.DropColumn(
                name: "StorageBackendName",
                table: "AlertIncidents");

            migrationBuilder.AlterColumn<int>(
                name: "FolderId",
                table: "AlertIncidents",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);
        }
    }
}
