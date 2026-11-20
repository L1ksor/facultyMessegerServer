using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FacultyMessenger.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class RefactorRolesChatPermissionsAndGarage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_messages_users_VerifiedById",
                table: "messages");

            migrationBuilder.DropIndex(
                name: "IX_messages_ChatId_IsVerifiedMaterial",
                table: "messages");

            migrationBuilder.DropIndex(
                name: "IX_messages_VerifiedById",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "OfficeHours",
                table: "users");

            migrationBuilder.DropColumn(
                name: "IsVerifiedMaterial",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "VerifiedById",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "IsReadOnly",
                table: "chats");

            migrationBuilder.AddColumn<bool>(
                name: "AllowMembersCreatePolls",
                table: "chats",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "AllowMembersPinMessages",
                table: "chats",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "AllowMembersSendMedia",
                table: "chats",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "AllowMembersSendMessages",
                table: "chats",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CuratorId",
                table: "academic_groups",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LeaderId",
                table: "academic_groups",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "chat_roles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChatId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    BadgeColor = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Permissions = table.Column<int>(type: "integer", nullable: false),
                    IsTemplate = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_roles", x => x.Id);
                    table.UniqueConstraint("AK_chat_roles_ChatId_Id", x => new { x.ChatId, x.Id });
                    table.ForeignKey(
                        name: "FK_chat_roles_chats_ChatId",
                        column: x => x.ChatId,
                        principalTable: "chats",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "chat_member_roles",
                columns: table => new
                {
                    ChatId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChatRoleId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssignedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_member_roles", x => new { x.ChatId, x.UserId, x.ChatRoleId });
                    table.ForeignKey(
                        name: "FK_chat_member_roles_chat_roles_ChatId_ChatRoleId",
                        columns: x => new { x.ChatId, x.ChatRoleId },
                        principalTable: "chat_roles",
                        principalColumns: new[] { "ChatId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_chat_member_roles_chats_ChatId",
                        column: x => x.ChatId,
                        principalTable: "chats",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_chat_member_roles_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_academic_groups_CuratorId",
                table: "academic_groups",
                column: "CuratorId");

            migrationBuilder.CreateIndex(
                name: "IX_academic_groups_LeaderId",
                table: "academic_groups",
                column: "LeaderId");

            migrationBuilder.CreateIndex(
                name: "IX_chat_member_roles_ChatId_ChatRoleId",
                table: "chat_member_roles",
                columns: new[] { "ChatId", "ChatRoleId" });

            migrationBuilder.CreateIndex(
                name: "IX_chat_member_roles_ChatId_UserId",
                table: "chat_member_roles",
                columns: new[] { "ChatId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_chat_member_roles_UserId",
                table: "chat_member_roles",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_chat_roles_ChatId_Name",
                table: "chat_roles",
                columns: new[] { "ChatId", "Name" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_academic_groups_users_CuratorId",
                table: "academic_groups",
                column: "CuratorId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_academic_groups_users_LeaderId",
                table: "academic_groups",
                column: "LeaderId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_academic_groups_users_CuratorId",
                table: "academic_groups");

            migrationBuilder.DropForeignKey(
                name: "FK_academic_groups_users_LeaderId",
                table: "academic_groups");

            migrationBuilder.DropTable(
                name: "chat_member_roles");

            migrationBuilder.DropTable(
                name: "chat_roles");

            migrationBuilder.DropIndex(
                name: "IX_academic_groups_CuratorId",
                table: "academic_groups");

            migrationBuilder.DropIndex(
                name: "IX_academic_groups_LeaderId",
                table: "academic_groups");

            migrationBuilder.DropColumn(
                name: "AllowMembersCreatePolls",
                table: "chats");

            migrationBuilder.DropColumn(
                name: "AllowMembersPinMessages",
                table: "chats");

            migrationBuilder.DropColumn(
                name: "AllowMembersSendMedia",
                table: "chats");

            migrationBuilder.DropColumn(
                name: "AllowMembersSendMessages",
                table: "chats");

            migrationBuilder.DropColumn(
                name: "CuratorId",
                table: "academic_groups");

            migrationBuilder.DropColumn(
                name: "LeaderId",
                table: "academic_groups");

            migrationBuilder.AddColumn<string>(
                name: "OfficeHours",
                table: "users",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsVerifiedMaterial",
                table: "messages",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "VerifiedById",
                table: "messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsReadOnly",
                table: "chats",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_messages_ChatId_IsVerifiedMaterial",
                table: "messages",
                columns: new[] { "ChatId", "IsVerifiedMaterial" },
                filter: "\"IsVerifiedMaterial\" = TRUE");

            migrationBuilder.CreateIndex(
                name: "IX_messages_VerifiedById",
                table: "messages",
                column: "VerifiedById");

            migrationBuilder.AddForeignKey(
                name: "FK_messages_users_VerifiedById",
                table: "messages",
                column: "VerifiedById",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
