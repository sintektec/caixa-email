using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sintek.Mail.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SenderReputationLists : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SenderReputations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Address = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    Domain = table.Column<string>(type: "TEXT", maxLength: 253, nullable: true),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SenderReputations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SenderReputations_AccountId",
                table: "SenderReputations",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_SenderReputations_Kind",
                table: "SenderReputations",
                column: "Kind");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SenderReputations");
        }
    }
}
