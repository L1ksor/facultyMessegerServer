using FacultyMessenger.Api.Auth;
using FacultyMessenger.Api.Contracts.Auth;
using FacultyMessenger.Api.Contracts.Chats;
using FacultyMessenger.Api.Data;
using FacultyMessenger.Api.Domain.Entities;
using FacultyMessenger.Api.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FacultyMessenger.Api.Controllers;

[ApiController]
[Route("api/users")]
[Authorize]
public class UsersController : ControllerBase
{
    private readonly AppDbContext _db;

    public UsersController(AppDbContext db) => _db = db;

    /// <summary>Справочник пользователей платформы (для создания личных чатов и своих бесед).</summary>
    [HttpGet("directory")]
    [ProducesResponseType<List<UserProfileDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<List<UserProfileDto>>> GetDirectory(
        [FromQuery] UserRole? role = null,
        [FromQuery] Guid? groupId = null,
        [FromQuery] string? search = null,
        [FromQuery] int limit = 50,
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 200);
        var query = _db.Users
            .AsNoTracking()
            .Include(u => u.AcademicGroup)
            .AsQueryable();

        if (role.HasValue)
            query = query.Where(u => u.Role == role.Value);

        if (groupId.HasValue)
            query = query.Where(u => u.AcademicGroupId == groupId.Value);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            query = query.Where(u => u.FullName.ToLower().Contains(term) || u.Email.ToLower().Contains(term));
        }

        var users = await query
            .OrderBy(u => u.Role)
            .ThenBy(u => u.FullName)
            .Take(limit)
            .ToListAsync(ct);

        return Ok(users.Select(u => UserProfileDto.FromEntity(u)).ToList());
    }

    /// <summary>Персональные настройки уведомлений: режим по умолчанию и список чатов.</summary>
    [HttpGet("me/notifications")]
    [ProducesResponseType<UserNotificationPreferencesDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<UserNotificationPreferencesDto>> GetNotificationPreferences(CancellationToken ct)
    {
        var userId = User.GetUserId();
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
            return Unauthorized();

        return Ok(await LoadNotificationPreferencesAsync(user, ct));
    }

    /// <summary>Обновление режима уведомлений по умолчанию (включая новые чаты) и/или пакетное изменение исключений по чатам.</summary>
    [HttpPut("me/notifications")]
    [ProducesResponseType<UserNotificationPreferencesDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<UserNotificationPreferencesDto>> UpdateNotificationPreferences(
        [FromBody] BatchUpdateChatNotificationsRequest request,
        CancellationToken ct)
    {
        var userId = User.GetUserId();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
            return Unauthorized();

        if (request.DefaultNotificationsEnabled.HasValue)
            user.DefaultNotificationsEnabled = request.DefaultNotificationsEnabled.Value;

        if (request.Chats is { Count: > 0 })
        {
            var chatIds = request.Chats.Select(c => c.ChatId).Distinct().ToList();
            var existingSettings = await _db.UserChatNotificationSettings
                .Where(s => s.UserId == userId && chatIds.Contains(s.ChatId))
                .ToDictionaryAsync(s => s.ChatId, ct);

            var validChatIds = new HashSet<Guid>(
                await _db.Chats.Where(c => chatIds.Contains(c.Id)).Select(c => c.Id).ToListAsync(ct));

            foreach (var item in request.Chats)
            {
                if (!validChatIds.Contains(item.ChatId))
                    continue;

                existingSettings.TryGetValue(item.ChatId, out var current);
                if (item.NotificationsEnabled is null)
                {
                    if (current is not null)
                        _db.UserChatNotificationSettings.Remove(current);
                }
                else if (current is null)
                {
                    _db.UserChatNotificationSettings.Add(new UserChatNotificationSetting
                    {
                        UserId = userId,
                        ChatId = item.ChatId,
                        NotificationsEnabled = item.NotificationsEnabled.Value,
                        UpdatedAt = DateTime.UtcNow
                    });
                }
                else
                {
                    current.NotificationsEnabled = item.NotificationsEnabled.Value;
                    current.UpdatedAt = DateTime.UtcNow;
                }
            }
        }

        await _db.SaveChangesAsync(ct);
        return Ok(await LoadNotificationPreferencesAsync(user, ct));
    }

    /// <summary>Список участников учебной группы текущего пользователя.</summary>
    [HttpGet("group/members")]
    [ProducesResponseType<List<UserProfileDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<List<UserProfileDto>>> GetMyGroupMembers(CancellationToken ct)
    {
        var userId = User.GetUserId();
        var groupId = await _db.Users
            .Where(u => u.Id == userId)
            .Select(u => u.AcademicGroupId)
            .FirstOrDefaultAsync(ct);

        if (groupId is null)
            return Ok(new List<UserProfileDto>());

        var members = await _db.Users
            .AsNoTracking()
            .Include(u => u.AcademicGroup)
            .Where(u => u.AcademicGroupId == groupId)
            .OrderBy(u => u.FullName)
            .ToListAsync(ct);

        return Ok(members.Select(m => UserProfileDto.FromEntity(m)).ToList());
    }

    private async Task<UserNotificationPreferencesDto> LoadNotificationPreferencesAsync(User user, CancellationToken ct)
    {
        var query = _db.Chats.AsNoTracking().Where(c => !c.IsArchived);
        if (user.Role != UserRole.Admin)
        {
            query = query.Where(c =>
                c.Participants.Any(p => p.UserId == user.Id)
                || (user.AcademicGroupId != null && (c.OwnerGroupId == user.AcademicGroupId || c.GroupBindings.Any(b => b.AcademicGroupId == user.AcademicGroupId)))
                || (c.OwnerGroup != null && c.OwnerGroup.CuratorId == user.Id)
                || c.GroupBindings.Any(b => b.AcademicGroup.CuratorId == user.Id)
                || (c.Type == ChatType.AnnouncementChannel && c.OwnerGroupId == null));
        }
        else
        {
            query = query.Where(c => c.Type != ChatType.Direct || c.Participants.Any(p => p.UserId == user.Id));
        }

        var chats = await query
            .OrderBy(c => c.Title)
            .Select(c => new ChatNotificationSettingDto(
                c.Id,
                c.Title,
                c.Type,
                _db.UserChatNotificationSettings
                    .Where(s => s.UserId == user.Id && s.ChatId == c.Id)
                    .Select(s => (bool?)s.NotificationsEnabled)
                    .FirstOrDefault() ?? user.DefaultNotificationsEnabled,
                _db.UserChatNotificationSettings
                    .Where(s => s.UserId == user.Id && s.ChatId == c.Id)
                    .Select(s => (bool?)s.NotificationsEnabled)
                    .FirstOrDefault()
            ))
            .ToListAsync(ct);

        return new UserNotificationPreferencesDto(user.DefaultNotificationsEnabled, chats);
    }
}
