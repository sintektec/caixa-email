using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sintek.Mail.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ContactsAndRecipientHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Contacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    GivenName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    FamilyName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Organization = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    JobTitle = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    PhoneNumber = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    ExternalId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Contacts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Contacts_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RecipientHistory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Address = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    UseCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecipientHistory", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecipientHistory_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ContactEmails",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ContactId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Address = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    Label = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    IsPrimary = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContactEmails", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContactEmails_Contacts_ContactId",
                        column: x => x.ContactId,
                        principalTable: "Contacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ContactEmails_Address",
                table: "ContactEmails",
                column: "Address");

            migrationBuilder.CreateIndex(
                name: "IX_ContactEmails_ContactId_Address",
                table: "ContactEmails",
                columns: new[] { "ContactId", "Address" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Contacts_AccountId_DisplayName",
                table: "Contacts",
                columns: new[] { "AccountId", "DisplayName" });

            migrationBuilder.CreateIndex(
                name: "IX_Contacts_AccountId_ExternalId",
                table: "Contacts",
                columns: new[] { "AccountId", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecipientHistory_AccountId_Address",
                table: "RecipientHistory",
                columns: new[] { "AccountId", "Address" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecipientHistory_AccountId_LastUsedAt",
                table: "RecipientHistory",
                columns: new[] { "AccountId", "LastUsedAt" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ContactEmails");

            migrationBuilder.DropTable(
                name: "RecipientHistory");

            migrationBuilder.DropTable(
                name: "Contacts");
        }
    }
}
