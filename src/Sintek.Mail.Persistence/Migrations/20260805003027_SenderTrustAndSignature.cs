using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sintek.Mail.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SenderTrustAndSignature : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DkimResult",
                table: "Messages",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "DmarcResult",
                table: "Messages",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "IsFlaggedAsSpamByServer",
                table: "Messages",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<double>(
                name: "SpamScore",
                table: "Messages",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SpfResult",
                table: "Messages",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Signature",
                table: "Accounts",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DkimResult",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "DmarcResult",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "IsFlaggedAsSpamByServer",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "SpamScore",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "SpfResult",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "Signature",
                table: "Accounts");
        }
    }
}
