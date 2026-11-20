using FacultyMessenger.Api.Contracts.Chats;
using FacultyMessenger.Api.Data;
using FacultyMessenger.Api.Domain.Entities;
using FacultyMessenger.Api.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace FacultyMessenger.Api.Services.Chats;

public interface IChatAccessService
{
    Task<bool> HasAccessAsync(Guid chatId, User user, CancellationToken ct = default);
    Task<bool> IsChatManagerAsync(Chat chat, User user, CancellationToken ct = default);
    Task<ChatPermissions> GetEffectivePermissionsAsync(Chat chat, User user, CancellationToken ct = default);
    Task<Dictionary<Guid, List<ChatRoleBadgeDto>>> GetUserBadgesMapAsync(Guid chatId, IEnumerable<Guid> userIds, CancellationToken ct = default);
    Task<List<Guid>> GetSubscriberUserIdsForNotificationAsync(Chat chat, Guid senderId, CancellationToken ct = default);
    Task<Guid?> GetDirectRecipientIdAsync(Guid chatId, Guid senderId, CancellationToken ct = default);
    List<string> ToPermissionNames(ChatPermissions permissions);
}

public class ChatAccessService : IChatAccessService
{
    private readonly AppDbContext _db;

    public ChatAccessService(AppDbContext db) => _db = db;

    public async Task<bool> HasAccessAsync(Guid chatId, User user, CancellationToken ct = default)
    {
        var chatInfo = await _db.Chats
            .AsNoTracking()
            .Where(c => c.Id == chatId)
            .Select(c => new
            {
                c.Type,
                c.OwnerGroupId,
                OwnerGroupCuratorId = c.OwnerGroup != null ? c.OwnerGroup.CuratorId : null,
                IsParticipant = c.Participants.Any(p => p.UserId == user.Id),
                HasGroupBinding = user.AcademicGroupId != null &&
                                  c.GroupBindings.Any(b => b.AcademicGroupId == user.AcademicGroupId),
                IsCuratorOfBoundGroup = c.GroupBindings.Any(b => b.AcademicGroup.CuratorId == user.Id)
            })
            .FirstOrDefaultAsync(ct);

        if (chatInfo is null)
            return false;

        if (user.Role == UserRole.Admin && chatInfo.Type != ChatType.Direct)
            return true;

        if (chatInfo.Type == ChatType.AnnouncementChannel && chatInfo.OwnerGroupId is null)
            return true;

        if (user.AcademicGroupId.HasValue && chatInfo.OwnerGroupId == user.AcademicGroupId)
            return true;

        if (chatInfo.OwnerGroupCuratorId == user.Id || chatInfo.IsCuratorOfBoundGroup)
            return true;

        return chatInfo.IsParticipant || chatInfo.HasGroupBinding;
    }

    public async Task<bool> IsChatManagerAsync(Chat chat, User user, CancellationToken ct = default)
    {
        if (user.Role == UserRole.Admin && chat.Type != ChatType.Direct)
            return true;

        if (chat.Type is ChatType.CustomGroup or ChatType.Subject or ChatType.AnnouncementChannel
            && chat.CreatedById == user.Id)
        {
            return true;
        }

        // В официальном чате учебной группы староста и куратор получают полные права управления
        if (chat.OwnerGroupId.HasValue)
        {
            var groupLeaders = await _db.AcademicGroups
                .AsNoTracking()
                .Where(g => g.Id == chat.OwnerGroupId.Value)
                .Select(g => new { g.LeaderId, g.CuratorId })
                .FirstOrDefaultAsync(ct);

            if (groupLeaders is not null && (groupLeaders.LeaderId == user.Id || groupLeaders.CuratorId == user.Id))
                return true;
        }

        // В предметном чате все добавленные преподаватели (Employee) являются менеджерами
        if (chat.Type == ChatType.Subject && user.Role == UserRole.Employee)
        {
            return await _db.ChatParticipants
                .AnyAsync(p => p.ChatId == chat.Id && p.UserId == user.Id, ct);
        }

        return false;
    }

    public async Task<ChatPermissions> GetEffectivePermissionsAsync(Chat chat, User user, CancellationToken ct = default)
    {
        if (chat.IsArchived)
            return ChatPermissions.None;

        if (await IsChatManagerAsync(chat, user, ct))
            return ChatPermissions.All;

        if (chat.Type == ChatType.Direct)
        {
            return ChatPermissions.CanSendMessages
                   | ChatPermissions.CanSendMedia
                   | ChatPermissions.CanPinMessages
                   | ChatPermissions.CanCreatePolls;
        }

        var perms = ChatPermissions.None;

        // Базовые права обычных участников из настроек беседы
        if (chat.AllowMembersSendMessages) perms |= ChatPermissions.CanSendMessages;
        if (chat.AllowMembersSendMedia) perms |= ChatPermissions.CanSendMedia;
        if (chat.AllowMembersPinMessages) perms |= ChatPermissions.CanPinMessages;
        if (chat.AllowMembersCreatePolls) perms |= ChatPermissions.CanCreatePolls;

        // Добавляем права всех назначенных кастомных ролей участника в этом чате
        var rolePerms = await _db.ChatMemberRoles
            .AsNoTracking()
            .Where(mr => mr.ChatId == chat.Id && mr.UserId == user.Id)
            .Select(mr => mr.ChatRole.Permissions)
            .ToListAsync(ct);

        foreach (var rp in rolePerms)
            perms |= rp;

        // В предметном чате староста или куратор привязанной группы может отправлять объявления с «Ознакомлен»
        if (chat.Type == ChatType.Subject)
        {
            var isBoundGroupLeaderOrCurator = await _db.ChatGroupBindings
                .AnyAsync(b => b.ChatId == chat.Id
                               && (b.AcademicGroup.LeaderId == user.Id || b.AcademicGroup.CuratorId == user.Id), ct);
            if (isBoundGroupLeaderOrCurator)
                perms |= ChatPermissions.CanSendAcknowledgements;
        }

        return perms;
    }

    public async Task<Dictionary<Guid, List<ChatRoleBadgeDto>>> GetUserBadgesMapAsync(
        Guid chatId,
        IEnumerable<Guid> userIds,
        CancellationToken ct = default)
    {
        var ids = userIds.Distinct().ToList();
        var map = ids.ToDictionary(id => id, _ => new List<ChatRoleBadgeDto>());
        if (ids.Count == 0)
            return map;

        // Системные бейджи Староста / Куратор в официальном чате группы
        var ownerGroup = await _db.Chats
            .AsNoTracking()
            .Where(c => c.Id == chatId && c.OwnerGroupId != null)
            .Select(c => new { c.OwnerGroup!.LeaderId, c.OwnerGroup.CuratorId })
            .FirstOrDefaultAsync(ct);

        if (ownerGroup is not null)
        {
            if (ownerGroup.LeaderId.HasValue && map.TryGetValue(ownerGroup.LeaderId.Value, out var leaderBadges))
                leaderBadges.Add(new ChatRoleBadgeDto(null, "Староста", "#d97706"));

            if (ownerGroup.CuratorId.HasValue && map.TryGetValue(ownerGroup.CuratorId.Value, out var curatorBadges))
                curatorBadges.Add(new ChatRoleBadgeDto(null, "Куратор", "#7c3aed"));
        }

        // Кастомные роли из конструктора ролей чата
        var assignments = await _db.ChatMemberRoles
            .AsNoTracking()
            .Where(mr => mr.ChatId == chatId && ids.Contains(mr.UserId))
            .OrderBy(mr => mr.ChatRole.Name)
            .Select(mr => new
            {
                mr.UserId,
                mr.ChatRoleId,
                mr.ChatRole.Name,
                mr.ChatRole.BadgeColor
            })
            .ToListAsync(ct);

        foreach (var a in assignments)
        {
            if (map.TryGetValue(a.UserId, out var list))
                list.Add(new ChatRoleBadgeDto(a.ChatRoleId, a.Name, a.BadgeColor));
        }

        return map;
    }

    public async Task<List<Guid>> GetSubscriberUserIdsForNotificationAsync(Chat chat, Guid senderId, CancellationToken ct = default)
    {
        IQueryable<User> candidateQuery;

        if (chat.Type == ChatType.AnnouncementChannel && chat.OwnerGroupId is null)
        {
            candidateQuery = _db.Users.AsNoTracking().Where(u => u.Id != senderId);
        }
        else
        {
            var boundGroups = await _db.ChatGroupBindings
                .Where(b => b.ChatId == chat.Id)
                .Select(b => new { b.AcademicGroupId, b.AcademicGroup.CuratorId })
                .ToListAsync(ct);

            var boundGroupIds = boundGroups.Select(g => g.AcademicGroupId).ToList();
            var curatorIds = boundGroups.Where(g => g.CuratorId.HasValue).Select(g => g.CuratorId!.Value).ToList();

            if (chat.OwnerGroupId.HasValue)
            {
                if (!boundGroupIds.Contains(chat.OwnerGroupId.Value))
                    boundGroupIds.Add(chat.OwnerGroupId.Value);

                var ownerCurator = await _db.AcademicGroups
                    .Where(g => g.Id == chat.OwnerGroupId.Value)
                    .Select(g => g.CuratorId)
                    .FirstOrDefaultAsync(ct);
                if (ownerCurator.HasValue && !curatorIds.Contains(ownerCurator.Value))
                    curatorIds.Add(ownerCurator.Value);
            }

            var participantIds = await _db.ChatParticipants
                .Where(p => p.ChatId == chat.Id && p.UserId != senderId)
                .Select(p => p.UserId)
                .ToListAsync(ct);

            var directUserIds = participantIds.Concat(curatorIds).Distinct().ToList();

            candidateQuery = _db.Users.AsNoTracking().Where(u =>
                u.Id != senderId && (
                    directUserIds.Contains(u.Id)
                    || (u.AcademicGroupId != null && boundGroupIds.Contains(u.AcademicGroupId.Value))
                ));
        }

        return await candidateQuery
            .Where(u =>
                _db.UserChatNotificationSettings
                    .Where(s => s.UserId == u.Id && s.ChatId == chat.Id)
                    .Select(s => (bool?)s.NotificationsEnabled)
                    .FirstOrDefault() ?? u.DefaultNotificationsEnabled)
            .Select(u => u.Id)
            .ToListAsync(ct);
    }

    public async Task<Guid?> GetDirectRecipientIdAsync(Guid chatId, Guid senderId, CancellationToken ct = default)
    {
        return await _db.ChatParticipants
            .Where(p => p.ChatId == chatId && p.UserId != senderId)
            .Select(p => (Guid?)p.UserId)
            .FirstOrDefaultAsync(ct);
    }

    public List<string> ToPermissionNames(ChatPermissions permissions)
    {
        var result = new List<string>();
        foreach (var flag in Enum.GetValues<ChatPermissions>())
        {
            if (flag is ChatPermissions.None or ChatPermissions.All)
                continue;
            if (permissions.HasFlag(flag))
                result.Add(flag.ToString());
        }
        return result;
    }
}
