using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FacultyMessenger.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceQuietHoursWithNotificationSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsDndEnabled",
                table: "users");

            migrationBuilder.DropColumn(
                name: "WorkDayEnd",
                table: "users");

            migrationBuilder.DropColumn(
                name: "WorkDayStart",
                table: "users");

            migrationBuilder.DropColumn(
                name: "IsSilent",
                table: "messages");

            migrationBuilder.AddColumn<bool>(
                name: "DefaultNotificationsEnabled",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "user_chat_notification_settings",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChatId = table.Column<Guid>(type: "uuid", nullable: false),
                    NotificationsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_chat_notification_settings", x => new { x.UserId, x.ChatId });
                    table.ForeignKey(
                        name: "FK_user_chat_notification_settings_chats_ChatId",
                        column: x => x.ChatId,
                        principalTable: "chats",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_user_chat_notification_settings_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_user_chat_notification_settings_ChatId",
                table: "user_chat_notification_settings",
                column: "ChatId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_chat_notification_settings");

            migrationBuilder.DropColumn(
                name: "DefaultNotificationsEnabled",
                table: "users");

            migrationBuilder.AddColumn<bool>(
                name: "IsDndEnabled",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "WorkDayEnd",
                table: "users",
                type: "time without time zone",
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "WorkDayStart",
                table: "users",
                type: "time without time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsSilent",
                table: "messages",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }
    }
}
