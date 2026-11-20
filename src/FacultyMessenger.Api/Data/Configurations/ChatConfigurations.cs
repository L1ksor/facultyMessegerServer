using FacultyMessenger.Api.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FacultyMessenger.Api.Data.Configurations;

public class ChatConfiguration : IEntityTypeConfiguration<Chat>
{
    public void Configure(EntityTypeBuilder<Chat> b)
    {
        b.ToTable("chats");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();

        b.Property(x => x.Title).IsRequired().HasMaxLength(200);
        b.Property(x => x.Type).IsRequired().HasConversion<string>().HasMaxLength(30);
        b.Property(x => x.AllowMembersSendMessages).HasDefaultValue(true);
        b.Property(x => x.AllowMembersSendMedia).HasDefaultValue(true);
        b.Property(x => x.AllowMembersPinMessages).HasDefaultValue(true);
        b.Property(x => x.AllowMembersCreatePolls).HasDefaultValue(true);
        b.Property(x => x.CreatedAt).IsRequired();

        b.HasIndex(x => new { x.Type, x.IsArchived });
        b.HasIndex(x => x.OwnerGroupId).IsUnique().HasFilter("\"OwnerGroupId\" IS NOT NULL");

        b.HasOne(x => x.CreatedBy)
            .WithMany()
            .HasForeignKey(x => x.CreatedById)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasOne(x => x.OwnerGroup)
            .WithMany()
            .HasForeignKey(x => x.OwnerGroupId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

public class ChatGroupBindingConfiguration : IEntityTypeConfiguration<ChatGroupBinding>
{
    public void Configure(EntityTypeBuilder<ChatGroupBinding> b)
    {
        b.ToTable("chat_group_bindings");
        b.HasKey(x => new { x.ChatId, x.AcademicGroupId });

        b.HasOne(x => x.Chat)
            .WithMany(c => c.GroupBindings)
            .HasForeignKey(x => x.ChatId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasOne(x => x.AcademicGroup)
            .WithMany(g => g.ChatBindings)
            .HasForeignKey(x => x.AcademicGroupId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => x.AcademicGroupId);
    }
}

public class ChatParticipantConfiguration : IEntityTypeConfiguration<ChatParticipant>
{
    public void Configure(EntityTypeBuilder<ChatParticipant> b)
    {
        b.ToTable("chat_participants");
        b.HasKey(x => new { x.ChatId, x.UserId });

        b.HasOne(x => x.Chat)
            .WithMany(c => c.Participants)
            .HasForeignKey(x => x.ChatId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasOne(x => x.User)
            .WithMany(u => u.ChatParticipations)
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => x.UserId);
    }
}

public class UserChatNotificationSettingConfiguration : IEntityTypeConfiguration<UserChatNotificationSetting>
{
    public void Configure(EntityTypeBuilder<UserChatNotificationSetting> b)
    {
        b.ToTable("user_chat_notification_settings");
        b.HasKey(x => new { x.UserId, x.ChatId });

        b.Property(x => x.NotificationsEnabled).IsRequired();
        b.Property(x => x.UpdatedAt).IsRequired();

        b.HasOne(x => x.User)
            .WithMany(u => u.ChatNotificationSettings)
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasOne(x => x.Chat)
            .WithMany()
            .HasForeignKey(x => x.ChatId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => x.ChatId);
    }
}

public class ChatRoleConfiguration : IEntityTypeConfiguration<ChatRole>
{
    public void Configure(EntityTypeBuilder<ChatRole> b)
    {
        b.ToTable("chat_roles");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();

        b.Property(x => x.Name).IsRequired().HasMaxLength(60);
        b.Property(x => x.BadgeColor).IsRequired().HasMaxLength(20);
        b.Property(x => x.Permissions).IsRequired().HasConversion<int>();
        b.Property(x => x.CreatedAt).IsRequired();

        b.HasIndex(x => new { x.ChatId, x.Name }).IsUnique();
        b.HasAlternateKey(x => new { x.ChatId, x.Id });

        b.HasOne(x => x.Chat)
            .WithMany(c => c.Roles)
            .HasForeignKey(x => x.ChatId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class ChatMemberRoleConfiguration : IEntityTypeConfiguration<ChatMemberRole>
{
    public void Configure(EntityTypeBuilder<ChatMemberRole> b)
    {
        b.ToTable("chat_member_roles");
        b.HasKey(x => new { x.ChatId, x.UserId, x.ChatRoleId });
        b.Property(x => x.AssignedAt).IsRequired();

        b.HasOne(x => x.Chat)
            .WithMany(c => c.MemberRoles)
            .HasForeignKey(x => x.ChatId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasOne(x => x.User)
            .WithMany(u => u.ChatRoles)
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Составной FK гарантирует на уровне БД, что роль принадлежит именно этому чату
        b.HasOne(x => x.ChatRole)
            .WithMany(r => r.Assignments)
            .HasForeignKey(x => new { x.ChatId, x.ChatRoleId })
            .HasPrincipalKey(r => new { r.ChatId, r.Id })
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => new { x.ChatId, x.UserId });
    }
}
