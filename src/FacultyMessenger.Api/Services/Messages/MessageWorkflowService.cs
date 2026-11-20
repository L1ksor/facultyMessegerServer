using FacultyMessenger.Api.Contracts.Chats;
using FacultyMessenger.Api.Contracts.Messages;
using FacultyMessenger.Api.Contracts.Polls;
using FacultyMessenger.Api.Data;
using FacultyMessenger.Api.Domain.Entities;
using FacultyMessenger.Api.Domain.Enums;
using FacultyMessenger.Api.Hubs;
using FacultyMessenger.Api.Services.Chats;
using FacultyMessenger.Api.Services.Common;
using FacultyMessenger.Api.Services.Storage;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace FacultyMessenger.Api.Services.Messages;

public interface IMessageWorkflowService
{
    Task<SendMessageResponse> SendTextMessageAsync(SendTextMessageRequest request, Guid userId, CancellationToken ct = default);
    Task<MessageDto> SetPinnedAsync(Guid messageId, bool isPinned, Guid userId, CancellationToken ct = default);
    Task DeleteMessageAsync(Guid messageId, Guid userId, CancellationToken ct = default);
    Task<MessageAcknowledgementDto> AcknowledgeAsync(Guid messageId, Guid userId, CancellationToken ct = default);
    Task<MessageAckStatusDto> GetAckStatusAsync(Guid messageId, Guid userId, Guid? groupId = null, CancellationToken ct = default);
    Task<MessageDto> LoadMessageDtoAsync(Guid messageId, Guid currentUserId, CancellationToken ct = default);
    Task BroadcastNewMessageAsync(Chat chat, MessageDto messageDto, CancellationToken ct = default);
}

public class MessageWorkflowService : IMessageWorkflowService
{
    private readonly AppDbContext _db;
    private readonly IChatAccessService _access;
    private readonly IFileStorageService _storage;
    private readonly IHubContext<ChatHub> _hub;

    public MessageWorkflowService(
        AppDbContext db,
        IChatAccessService access,
        IFileStorageService storage,
        IHubContext<ChatHub> hub)
    {
        _db = db;
        _access = access;
        _storage = storage;
        _hub = hub;
    }

    public async Task<SendMessageResponse> SendTextMessageAsync(
        SendTextMessageRequest request,
        Guid userId,
        CancellationToken ct = default)
    {
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new DomainNotFoundException("Пользователь не найден.");

        var chat = await _db.Chats.FirstOrDefaultAsync(c => c.Id == request.ChatId, ct)
            ?? throw new DomainNotFoundException("Чат не найден.");

        if (!await _access.HasAccessAsync(chat.Id, user, ct))
            throw new DomainForbiddenException("Нет доступа к указанному чату.");

        if (chat.IsArchived)
            throw new DomainForbiddenException("Чат находится в архиве семестра. Отправка новых сообщений закрыта.");

        var perms = await _access.GetEffectivePermissionsAsync(chat, user, ct);
        if (!perms.HasFlag(ChatPermissions.CanSendMessages))
            throw new DomainForbiddenException("Отправка сообщений в этот чат ограничена настройками беседы.");

        if (request.RequiresAcknowledgement && !perms.HasFlag(ChatPermissions.CanSendAcknowledgements))
            throw new DomainForbiddenException("Недостаточно прав для отправки объявлений с кнопкой «Ознакомлен».");

        var message = new Message
        {
            Id = Guid.NewGuid(),
            ChatId = chat.Id,
            SenderId = user.Id,
            Text = request.Text.Trim(),
            MessageType = MessageType.Text,
            RequiresAcknowledgement = request.RequiresAcknowledgement,
            CreatedAt = DateTime.UtcNow
        };

        _db.Messages.Add(message);
        await _db.SaveChangesAsync(ct);

        var dto = await LoadMessageDtoAsync(message.Id, user.Id, ct);
        await BroadcastNewMessageAsync(chat, dto, ct);

        return new SendMessageResponse(dto);
    }

    public async Task<MessageDto> SetPinnedAsync(Guid messageId, bool isPinned, Guid userId, CancellationToken ct = default)
    {
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new DomainNotFoundException("Пользователь не найден.");

        var message = await _db.Messages
            .Include(m => m.Chat)
            .FirstOrDefaultAsync(m => m.Id == messageId, ct)
            ?? throw new DomainNotFoundException("Сообщение не найдено.");

        if (!await _access.HasAccessAsync(message.ChatId, user, ct))
            throw new DomainForbiddenException("Нет доступа к чату.");

        var perms = await _access.GetEffectivePermissionsAsync(message.Chat, user, ct);
        if (!perms.HasFlag(ChatPermissions.CanPinMessages))
            throw new DomainForbiddenException("Закрепление сообщений в этом чате ограничено.");

        message.IsPinned = isPinned;
        await _db.SaveChangesAsync(ct);

        var dto = await LoadMessageDtoAsync(message.Id, user.Id, ct);
        await _hub.Clients.Group(ChatHub.ChatGroup(message.ChatId))
            .SendAsync("MessagePinned", new { messageId = message.Id, chatId = message.ChatId, isPinned }, ct);

        return dto;
    }

    public async Task DeleteMessageAsync(Guid messageId, Guid userId, CancellationToken ct = default)
    {
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new DomainNotFoundException("Пользователь не найден.");

        var message = await _db.Messages
            .Include(m => m.Chat)
            .Include(m => m.Attachments)
            .FirstOrDefaultAsync(m => m.Id == messageId, ct)
            ?? throw new DomainNotFoundException("Сообщение не найдено.");

        if (!await _access.HasAccessAsync(message.ChatId, user, ct))
            throw new DomainForbiddenException("Нет доступа к чату.");

        if (message.Chat.IsArchived)
            throw new DomainForbiddenException("Чат находится в архиве. Удаление сообщений запрещено.");

        var isAuthor = message.SenderId == user.Id;
        var perms = await _access.GetEffectivePermissionsAsync(message.Chat, user, ct);
        if (!isAuthor && !perms.HasFlag(ChatPermissions.CanDeleteMessages))
            throw new DomainForbiddenException("Удалять чужие сообщения могут только менеджеры чата или участники с правом CanDeleteMessages.");

        var storedKeys = message.Attachments.Select(a => a.StoredFilePath).ToList();
        var chatId = message.ChatId;

        _db.Messages.Remove(message);
        await _db.SaveChangesAsync(ct);

        foreach (var key in storedKeys)
            await _storage.DeleteFileAsync(key, ct);

        await _hub.Clients.Group(ChatHub.ChatGroup(chatId))
            .SendAsync("MessageDeleted", new { messageId, chatId }, ct);
    }

    public async Task<MessageAcknowledgementDto> AcknowledgeAsync(Guid messageId, Guid userId, CancellationToken ct = default)
    {
        var user = await _db.Users
            .AsNoTracking()
            .Include(u => u.AcademicGroup)
            .FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new DomainNotFoundException("Пользователь не найден.");

        var message = await _db.Messages
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == messageId, ct)
            ?? throw new DomainNotFoundException("Сообщение не найдено.");

        if (!await _access.HasAccessAsync(message.ChatId, user, ct))
            throw new DomainForbiddenException("Нет доступа к чату этого сообщения.");

        if (!message.RequiresAcknowledgement)
            throw new DomainBadRequestException("Данное сообщение не требует подтверждения прочтения.");

        var existing = await _db.MessageAcknowledgements
            .FirstOrDefaultAsync(a => a.MessageId == messageId && a.UserId == userId, ct);

        DateTime confirmedAt;
        if (existing is null)
        {
            confirmedAt = DateTime.UtcNow;
            _db.MessageAcknowledgements.Add(new MessageAcknowledgement
            {
                MessageId = messageId,
                UserId = userId,
                ConfirmedAt = confirmedAt
            });
            await _db.SaveChangesAsync(ct);
        }
        else
        {
            confirmedAt = existing.ConfirmedAt;
        }

        var totalAckCount = await _db.MessageAcknowledgements
            .CountAsync(a => a.MessageId == messageId, ct);

        var dto = new MessageAcknowledgementDto(
            messageId,
            message.ChatId,
            user.Id,
            user.FullName,
            user.AcademicGroup?.Name,
            confirmedAt,
            totalAckCount
        );

        await _hub.Clients.Group(ChatHub.ChatGroup(message.ChatId))
            .SendAsync("MessageAcknowledged", dto, ct);

        return dto;
    }

    public async Task<MessageAckStatusDto> GetAckStatusAsync(
        Guid messageId,
        Guid userId,
        Guid? groupId = null,
        CancellationToken ct = default)
    {
        var user = await _db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new DomainNotFoundException("Пользователь не найден.");

        var message = await _db.Messages
            .AsNoTracking()
            .Include(m => m.Chat)
                .ThenInclude(c => c.GroupBindings)
            .Include(m => m.Chat)
                .ThenInclude(c => c.Participants)
            .FirstOrDefaultAsync(m => m.Id == messageId, ct)
            ?? throw new DomainNotFoundException("Сообщение не найдено.");

        var perms = await _access.GetEffectivePermissionsAsync(message.Chat, user, ct);
        var canView = message.SenderId == user.Id
                      || user.Role is UserRole.Admin or UserRole.Employee
                      || perms.HasFlag(ChatPermissions.CanSendAcknowledgements);

        if (!canView || !await _access.HasAccessAsync(message.ChatId, user, ct))
            throw new DomainForbiddenException("Недостаточно прав для просмотра списка ознакомления.");

        var targetGroupIds = new HashSet<Guid>();
        if (groupId.HasValue)
        {
            targetGroupIds.Add(groupId.Value);
        }
        else if (user.Role == UserRole.Student && user.AcademicGroupId.HasValue)
        {
            targetGroupIds.Add(user.AcademicGroupId.Value);
        }
        else
        {
            if (message.Chat.OwnerGroupId.HasValue)
                targetGroupIds.Add(message.Chat.OwnerGroupId.Value);

            foreach (var b in message.Chat.GroupBindings)
                targetGroupIds.Add(b.AcademicGroupId);
        }

        var ackQuery = _db.MessageAcknowledgements
            .AsNoTracking()
            .Where(a => a.MessageId == messageId);

        if (targetGroupIds.Count > 0 && (groupId.HasValue || user.Role == UserRole.Student))
        {
            ackQuery = ackQuery.Where(a => a.User.AcademicGroupId != null && targetGroupIds.Contains(a.User.AcademicGroupId.Value));
        }

        var ackRecords = await ackQuery
            .OrderBy(a => a.ConfirmedAt)
            .Select(a => new AcknowledgedUserDto(
                a.UserId,
                a.User.FullName,
                a.User.Email,
                a.User.Role,
                a.User.AcademicGroupId,
                a.User.AcademicGroup != null ? a.User.AcademicGroup.Name : null,
                a.ConfirmedAt
            ))
            .ToListAsync(ct);

        var ackUserIds = ackRecords.Select(a => a.UserId).ToHashSet();

        List<PendingUserDto> pendingUsers;
        if (targetGroupIds.Count > 0)
        {
            pendingUsers = await _db.Users
                .AsNoTracking()
                .Where(u => u.AcademicGroupId != null
                            && targetGroupIds.Contains(u.AcademicGroupId.Value)
                            && u.Id != message.SenderId
                            && !ackUserIds.Contains(u.Id))
                .OrderBy(u => u.AcademicGroup!.Name)
                .ThenBy(u => u.FullName)
                .Select(u => new PendingUserDto(
                    u.Id,
                    u.FullName,
                    u.Email,
                    u.Role,
                    u.AcademicGroupId,
                    u.AcademicGroup != null ? u.AcademicGroup.Name : null
                ))
                .ToListAsync(ct);
        }
        else
        {
            var participantIds = message.Chat.Participants.Select(p => p.UserId).ToList();
            if (participantIds.Count > 0)
            {
                pendingUsers = await _db.Users
                    .AsNoTracking()
                    .Where(u => participantIds.Contains(u.Id)
                                && u.Id != message.SenderId
                                && !ackUserIds.Contains(u.Id))
                    .OrderBy(u => u.FullName)
                    .Select(u => new PendingUserDto(
                        u.Id,
                        u.FullName,
                        u.Email,
                        u.Role,
                        u.AcademicGroupId,
                        u.AcademicGroup != null ? u.AcademicGroup.Name : null
                    ))
                    .ToListAsync(ct);
            }
            else if (message.Chat.Type == ChatType.AnnouncementChannel && message.Chat.OwnerGroupId is null)
            {
                pendingUsers = await _db.Users
                    .AsNoTracking()
                    .Where(u => u.Id != message.SenderId && !ackUserIds.Contains(u.Id))
                    .OrderBy(u => u.FullName)
                    .Select(u => new PendingUserDto(
                        u.Id,
                        u.FullName,
                        u.Email,
                        u.Role,
                        u.AcademicGroupId,
                        u.AcademicGroup != null ? u.AcademicGroup.Name : null
                    ))
                    .ToListAsync(ct);
            }
            else
            {
                pendingUsers = [];
            }
        }

        return new MessageAckStatusDto(
            messageId,
            message.ChatId,
            message.RequiresAcknowledgement,
            ackRecords.Count,
            pendingUsers.Count,
            ackRecords,
            pendingUsers
        );
    }

    public async Task<MessageDto> LoadMessageDtoAsync(Guid messageId, Guid currentUserId, CancellationToken ct = default)
    {
        var dto = await _db.Messages
            .AsNoTracking()
            .Where(m => m.Id == messageId)
            .Select(m => new MessageDto(
                m.Id,
                m.ChatId,
                m.SenderId,
                m.Sender.FullName,
                m.Sender.Role,
                new List<ChatRoleBadgeDto>(),
                m.Text,
                m.MessageType,
                m.IsPinned,
                m.RequiresAcknowledgement,
                m.Acknowledgements.Any(a => a.UserId == currentUserId),
                m.Acknowledgements.Count,
                m.CreatedAt,
                m.Attachments.Select(a => new AttachmentDto(
                    a.Id,
                    a.OriginalFileName,
                    a.ContentType,
                    a.FileSize,
                    a.DurationSeconds,
                    $"/api/messages/attachments/{a.Id}/download"
                )).ToList(),
                m.Poll == null ? null : new PollDto(
                    m.Poll.Id,
                    m.Id,
                    m.ChatId,
                    m.Poll.Question,
                    m.Poll.IsClosed,
                    m.Poll.AllowMultipleChoice,
                    m.Poll.Options.SelectMany(o => o.Votes).Count(),
                    m.Poll.Options
                        .OrderBy(o => o.SortOrder)
                        .Select(o => new PollOptionDto(
                            o.Id,
                            o.OptionText,
                            o.MaxVotes,
                            o.SortOrder,
                            o.Votes.Count,
                            o.Votes.Any(v => v.UserId == currentUserId),
                            o.MaxVotes != null && o.Votes.Count >= o.MaxVotes,
                            o.Votes
                                .OrderBy(v => v.VotedAt)
                                .Select(v => new PollVoterDto(
                                    v.UserId,
                                    v.User.FullName,
                                    v.User.AcademicGroup != null ? v.User.AcademicGroup.Name : null,
                                    v.VotedAt))
                                .ToList()
                        ))
                        .ToList()
                )
            ))
            .FirstAsync(ct);

        var badgesMap = await _access.GetUserBadgesMapAsync(dto.ChatId, [dto.SenderId], ct);
        return dto with { SenderBadges = badgesMap.GetValueOrDefault(dto.SenderId, []) };
    }

    public async Task BroadcastNewMessageAsync(Chat chat, MessageDto messageDto, CancellationToken ct = default)
    {
        var targets = new List<string> { ChatHub.ChatGroup(chat.Id) };
        if (chat.Type == ChatType.Direct)
        {
            var recipientId = await _access.GetDirectRecipientIdAsync(chat.Id, messageDto.SenderId, ct);
            if (recipientId.HasValue)
                targets.Add(ChatHub.UserGroup(recipientId.Value));
        }

        await _hub.Clients.Groups(targets).SendAsync("ReceiveMessage", messageDto, ct);

        var notifyUserIds = await _access.GetSubscriberUserIdsForNotificationAsync(chat, messageDto.SenderId, ct);
        if (notifyUserIds.Count == 0)
            return;

        var preview = messageDto.Text;
        if (string.IsNullOrWhiteSpace(preview))
        {
            preview = messageDto.MessageType switch
            {
                MessageType.Voice => "Голосовое сообщение",
                MessageType.File => messageDto.Attachments.FirstOrDefault()?.OriginalFileName ?? "Файл",
                MessageType.Poll => messageDto.Poll?.Question ?? "Опрос",
                _ => "Новое сообщение"
            };
        }

        var notification = new NewMessageNotificationDto(
            messageDto.Id,
            chat.Id,
            chat.Title,
            chat.Type,
            messageDto.SenderId,
            messageDto.SenderName,
            preview,
            messageDto.MessageType,
            messageDto.CreatedAt
        );

        await _hub.Clients.Groups(notifyUserIds.Select(ChatHub.UserGroup).ToList())
            .SendAsync("NewMessageNotification", notification, ct);
    }
}
