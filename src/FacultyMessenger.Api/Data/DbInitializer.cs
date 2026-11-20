using FacultyMessenger.Api.Configuration;
using FacultyMessenger.Api.Domain.Entities;
using FacultyMessenger.Api.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FacultyMessenger.Api.Data;

/// <summary>Применяет миграции и создаёт первого администратора и канал объявлений деканата.</summary>
public static class DbInitializer
{
    public const string FacultyAnnouncementsTitle = "Объявления деканата";

    public static async Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var seed = scope.ServiceProvider.GetRequiredService<IOptions<SeedAdminOptions>>().Value;
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(DbInitializer));

        await db.Database.MigrateAsync(ct);

        var adminEmail = seed.Email.Trim().ToLowerInvariant();
        var admin = await db.Users.FirstOrDefaultAsync(u => u.Role == UserRole.Admin, ct);
        if (admin is null)
        {
            admin = new User
            {
                FullName = seed.FullName,
                Email = adminEmail,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(seed.Password, workFactor: 12),
                Role = UserRole.Admin,
                MustChangePassword = true
            };
            db.Users.Add(admin);
            logger.LogWarning("Создан администратор по умолчанию {Email}. Смените пароль при первом входе.", adminEmail);
        }

        if (!await db.Chats.AnyAsync(c => c.Type == ChatType.AnnouncementChannel && c.OwnerGroupId == null, ct))
        {
            db.Chats.Add(new Chat
            {
                Title = FacultyAnnouncementsTitle,
                Type = ChatType.AnnouncementChannel,
                AllowMembersSendMessages = false,
                AllowMembersSendMedia = false,
                AllowMembersPinMessages = false,
                AllowMembersCreatePolls = false,
                CreatedBy = admin
            });
        }

        await db.SaveChangesAsync(ct);
    }
}
