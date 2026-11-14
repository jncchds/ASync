using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ASync.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PurgeJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PurgeJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedByUserId = table.Column<int>(type: "integer", nullable: false),
                    OwnerUserId = table.Column<int>(type: "integer", nullable: false),
                    OwnerName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    FolderId = table.Column<int>(type: "integer", nullable: false),
                    FolderName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    StorageBackendId = table.Column<int>(type: "integer", nullable: false),
                    StorageType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ProtectedStorageSettings = table.Column<string>(type: "text", nullable: false),
                    SharedStorage = table.Column<bool>(type: "boolean", nullable: false),
                    RootPath = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    FilesDeleted = table.Column<long>(type: "bigint", nullable: false),
                    BytesDeleted = table.Column<long>(type: "bigint", nullable: false),
                    FilesLeft = table.Column<long>(type: "bigint", nullable: false),
                    Message = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurgeJobs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PurgeJobs_RequestedByUserId",
                table: "PurgeJobs",
                column: "RequestedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PurgeJobs_Status",
                table: "PurgeJobs",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PurgeJobs");
        }
    }
}
