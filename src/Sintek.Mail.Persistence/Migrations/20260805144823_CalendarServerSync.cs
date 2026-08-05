using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sintek.Mail.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CalendarServerSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RawICalendar",
                table: "CalendarEvents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RemoteCalendarId",
                table: "CalendarEvents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RemoteETag",
                table: "CalendarEvents",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RemoteHref",
                table: "CalendarEvents",
                type: "TEXT",
                maxLength: 2048,
                nullable: true);

            // 1 = LocalOnly, e não 0 = Synced. O EF deriva o padrão do valor default do
            // CLR, que aqui é a resposta errada: todo compromisso que já existe nasceu sem
            // servidor, e marcá-lo como sincronizado faria BindToRemoteCalendar não o
            // promover a PendingCreate — ele nunca subiria para lugar nenhum.
            migrationBuilder.AddColumn<int>(
                name: "SyncState",
                table: "CalendarEvents",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "CalendarProvider",
                table: "Accounts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "CalendarSyncEnabled",
                table: "Accounts",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "CalendarUrl",
                table: "Accounts",
                type: "TEXT",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "RemoteCalendars",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Provider = table.Column<int>(type: "INTEGER", nullable: false),
                    CollectionUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Color = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    IsReadOnly = table.Column<bool>(type: "INTEGER", nullable: false),
                    SyncEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    SyncToken = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    CTag = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    LastSyncAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastSyncError = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemoteCalendars", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RemoteCalendars_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEvents_RemoteCalendarId_RemoteHref",
                table: "CalendarEvents",
                columns: new[] { "RemoteCalendarId", "RemoteHref" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEvents_RemoteCalendarId_SyncState",
                table: "CalendarEvents",
                columns: new[] { "RemoteCalendarId", "SyncState" });

            migrationBuilder.CreateIndex(
                name: "IX_RemoteCalendars_AccountId_CollectionUrl",
                table: "RemoteCalendars",
                columns: new[] { "AccountId", "CollectionUrl" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_CalendarEvents_RemoteCalendars_RemoteCalendarId",
                table: "CalendarEvents",
                column: "RemoteCalendarId",
                principalTable: "RemoteCalendars",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CalendarEvents_RemoteCalendars_RemoteCalendarId",
                table: "CalendarEvents");

            migrationBuilder.DropTable(
                name: "RemoteCalendars");

            migrationBuilder.DropIndex(
                name: "IX_CalendarEvents_RemoteCalendarId_RemoteHref",
                table: "CalendarEvents");

            migrationBuilder.DropIndex(
                name: "IX_CalendarEvents_RemoteCalendarId_SyncState",
                table: "CalendarEvents");

            migrationBuilder.DropColumn(
                name: "RawICalendar",
                table: "CalendarEvents");

            migrationBuilder.DropColumn(
                name: "RemoteCalendarId",
                table: "CalendarEvents");

            migrationBuilder.DropColumn(
                name: "RemoteETag",
                table: "CalendarEvents");

            migrationBuilder.DropColumn(
                name: "RemoteHref",
                table: "CalendarEvents");

            migrationBuilder.DropColumn(
                name: "SyncState",
                table: "CalendarEvents");

            migrationBuilder.DropColumn(
                name: "CalendarProvider",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "CalendarSyncEnabled",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "CalendarUrl",
                table: "Accounts");
        }
    }
}
