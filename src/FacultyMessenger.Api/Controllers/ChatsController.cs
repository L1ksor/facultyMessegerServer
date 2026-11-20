using FacultyMessenger.Api.Auth;
using FacultyMessenger.Api.Contracts.Chats;
using FacultyMessenger.Api.Contracts.Messages;
using FacultyMessenger.Api.Data;
using FacultyMessenger.Api.Domain.Entities;
using FacultyMessenger.Api.Domain.Enums;
using FacultyMessenger.Api.Hubs;
using FacultyMessenger.Api.Services.Chats;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace FacultyMessenger.Api.Controllers;

[ApiController]
[Route("api/chats")]
[Authorize]
public class ChatsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IChatAccessService _access;
    private readonly IHubContext<ChatHub> _hub;

    public ChatsController(AppDbContext db, IChatAccessService access, IHubContext<ChatHub> hub)
    {
        _db = db;
        _access = access;
        _hub = hub;
    }

    /// <summary>Список всех чатов, доступных текущему пользователю.</summary>
    [HttpGet]
    [ProducesResponseType<List<ChatSummaryDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<List<ChatSummaryDto>>> GetMyChats(
        [FromQuery] bool includeArchived = false,
        CancellationToken ct = default)
    {
        var userId = User.GetUserId();
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
            return Unauthorized();

        var query = _db.Chats.AsNoTracking().AsQueryable();

        if (!includeArchived)
            query = query.Where(c => !c.IsArchived);

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
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => new ChatSummaryDto(
                c.Id,
                c.Title,
                c.Type,
                new ChatSettingsDto(
                    c.AllowMembersSendMessages,
                    c.AllowMembersSendMedia,
                    c.AllowMembersPinMessages,
                    c.AllowMembersCreatePolls
                ),
                c.IsArchived,
                c.OwnerGroupId,
                c.CreatedById,
                c.CreatedAt,
                c.GroupBindings
                    .OrderBy(b => b.AcademicGroup.Name)
                    .Select(b => new BoundGroupDto(
                        b.AcademicGroupId,
                        b.AcademicGroup.Name,
                        b.AcademicGroup.Course,
                        b.AcademicGroup.Faculty))
                    .ToList(),
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

        return Ok(chats);
    }

    /// <summary>Детальная информация о чате, участниках, ролях и правах текущего пользователя.</summary>
    [HttpGet("{chatId:guid}")]
    [ProducesResponseType<ChatDetailsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ChatDetailsDto>> GetChat(Guid chatId, CancellationToken ct)
    {
        var user = await GetCurrentUserAsync(ct);
        if (user is null) return Unauthorized();

        if (!await _db.Chats.AnyAsync(c => c.Id == chatId, ct))
            return NotFound();

        if (!await _access.HasAccessAsync(chatId, user, ct))
            return Forbid();

        return Ok(await LoadChatDetailsAsync(chatId, user, ct));
    }

    /// <summary>Создание своей беседы (для подгруппы или проекта — доступно студентам, сотрудникам и админу).</summary>
    [HttpPost("custom")]
    [ProducesResponseType<ChatDetailsDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ChatDetailsDto>> CreateCustomChat(
        [FromBody] CreateCustomChatRequest request,
        CancellationToken ct)
    {
        var user = await GetCurrentUserAsync(ct);
        if (user is null) return Unauthorized();

        var chat = new Chat
        {
            Id = Guid.NewGuid(),
            Title = request.Title.Trim(),
            Type = ChatType.CustomGroup,
            AllowMembersSendMessages = true,
            AllowMembersSendMedia = true,
            AllowMembersPinMessages = true,
            AllowMembersCreatePolls = true,
            CreatedById = user.Id
        };

        _db.Chats.Add(chat);
        _db.ChatParticipants.Add(new ChatParticipant { ChatId = chat.Id, UserId = user.Id });

        if (request.ParticipantUserIds is { Count: > 0 })
        {
            var otherIds = request.ParticipantUserIds.Where(id => id != user.Id).Distinct().ToList();
            var existingIds = await _db.Users.Where(u => otherIds.Contains(u.Id)).Select(u => u.Id).ToListAsync(ct);
            foreach (var pid in existingIds)
                _db.ChatParticipants.Add(new ChatParticipant { ChatId = chat.Id, UserId = pid });
        }

        await _db.SaveChangesAsync(ct);
        var details = await LoadChatDetailsAsync(chat.Id, user, ct);
        return CreatedAtAction(nameof(GetChat), new { chatId = chat.Id }, details);
    }

    /// <summary>Добавление участника в беседу (CustomGroup или Subject).</summary>
    [HttpPost("{chatId:guid}/participants")]
    [ProducesResponseType<ChatDetailsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ChatDetailsDto>> AddParticipant(
        Guid chatId,
        [FromBody] AddParticipantRequest request,
        CancellationToken ct)
    {
        var user = await GetCurrentUserAsync(ct);
        if (user is null) return Unauthorized();

        var chat = await _db.Chats.FirstOrDefaultAsync(c => c.Id == chatId, ct);
        if (chat is null) return NotFound();

        if (chat.Type is ChatType.Direct or ChatType.AnnouncementChannel)
            return Problem(statusCode: StatusCodes.Status400BadRequest, detail: "В этот тип чата нельзя вручную добавлять участников.");

        if (!await _access.IsChatManagerAsync(chat, user, ct))
            return Forbid();

        if (!await _db.Users.AnyAsync(u => u.Id == request.UserId, ct))
            return NotFound();

        if (!await _db.ChatParticipants.AnyAsync(p => p.ChatId == chatId && p.UserId == request.UserId, ct))
        {
            _db.ChatParticipants.Add(new ChatParticipant { ChatId = chatId, UserId = request.UserId });
            await _db.SaveChangesAsync(ct);
        }

        return Ok(await LoadChatDetailsAsync(chatId, user, ct));
    }

    /// <summary>Удаление участника из беседы (или добровольный выход).</summary>
    [HttpDelete("{chatId:guid}/participants/{targetUserId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveParticipant(Guid chatId, Guid targetUserId, CancellationToken ct)
    {
        var user = await GetCurrentUserAsync(ct);
        if (user is null) return Unauthorized();

        var chat = await _db.Chats.FirstOrDefaultAsync(c => c.Id == chatId, ct);
        if (chat is null) return NotFound();

        if (targetUserId != user.Id && !await _access.IsChatManagerAsync(chat, user, ct))
            return Forbid();

        var participant = await _db.ChatParticipants
            .FirstOrDefaultAsync(p => p.ChatId == chatId && p.UserId == targetUserId, ct);
        if (participant is null)
            return NotFound();

        _db.ChatParticipants.Remove(participant);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Создание предметного чата преподавателем/сотрудником (Employee) или администратором.</summary>
    [HttpPost("subject")]
    [Authorize(Policy = Policies.EmployeeOrAdmin)]
    [ProducesResponseType<ChatDetailsDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ChatDetailsDto>> CreateSubjectChat(
        [FromBody] CreateSubjectChatRequest request,
        CancellationToken ct)
    {
        var user = await GetCurrentUserAsync(ct);
        if (user is null) return Unauthorized();

        var chat = new Chat
        {
            Id = Guid.NewGuid(),
            Title = request.Title.Trim(),
            Type = ChatType.Subject,
            AllowMembersSendMessages = request.AllowMembersSendMessages,
            AllowMembersSendMedia = request.AllowMembersSendMedia,
            AllowMembersPinMessages = request.AllowMembersPinMessages,
            AllowMembersCreatePolls = request.AllowMembersCreatePolls,
            CreatedById = user.Id
        };

        _db.Chats.Add(chat);
        _db.ChatParticipants.Add(new ChatParticipant { ChatId = chat.Id, UserId = user.Id });

        if (request.GroupIds is { Count: > 0 })
        {
            var distinctIds = request.GroupIds.Distinct().ToList();
            var existingGroupIds = await _db.AcademicGroups
                .Where(g => distinctIds.Contains(g.Id))
                .Select(g => g.Id)
                .ToListAsync(ct);

            if (existingGroupIds.Count != distinctIds.Count)
            {
                return Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Некоторые группы не найдены",
                    detail: "Проверьте корректность списка GroupIds.");
            }

            foreach (var gid in existingGroupIds)
                _db.ChatGroupBindings.Add(new ChatGroupBinding { ChatId = chat.Id, AcademicGroupId = gid });
        }

        await _db.SaveChangesAsync(ct);
        var details = await LoadChatDetailsAsync(chat.Id, user, ct);
        return CreatedAtAction(nameof(GetChat), new { chatId = chat.Id }, details);
    }

    /// <summary>Создание или получение личного чата с пользователем.</summary>
    [HttpPost("direct")]
    [ProducesResponseType<ChatDetailsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ChatDetailsDto>> GetOrCreateDirectChat(
        [FromBody] CreateDirectChatRequest request,
        CancellationToken ct)
    {
        var user = await GetCurrentUserAsync(ct);
        if (user is null) return Unauthorized();

        if (request.TargetUserId == user.Id)
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Некорректный получатель",
                detail: "Нельзя создать личный чат с самим собой.");
        }

        var targetUser = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == request.TargetUserId, ct);
        if (targetUser is null)
            return NotFound();

        var existingChatId = await _db.Chats
            .Where(c => c.Type == ChatType.Direct
                        && c.Participants.Any(p => p.UserId == user.Id)
                        && c.Participants.Any(p => p.UserId == request.TargetUserId))
            .Select(c => (Guid?)c.Id)
            .FirstOrDefaultAsync(ct);

        if (existingChatId.HasValue)
            return Ok(await LoadChatDetailsAsync(existingChatId.Value, user, ct));

        var chat = new Chat
        {
            Id = Guid.NewGuid(),
            Title = $"{user.FullName} — {targetUser.FullName}",
            Type = ChatType.Direct,
            CreatedById = user.Id
        };

        _db.Chats.Add(chat);
        _db.ChatParticipants.Add(new ChatParticipant { ChatId = chat.Id, UserId = user.Id });
        _db.ChatParticipants.Add(new ChatParticipant { ChatId = chat.Id, UserId = targetUser.Id });

        await _db.SaveChangesAsync(ct);
        return Ok(await LoadChatDetailsAsync(chat.Id, user, ct));
    }

    /// <summary>Привязка одной или нескольких академических групп к предметному чату в один клик.</summary>
    [HttpPost("{chatId:guid}/bind-groups")]
    [Authorize(Policy = Policies.EmployeeOrAdmin)]
    [ProducesResponseType<ChatDetailsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ChatDetailsDto>> BindGroups(
        Guid chatId,
        [FromBody] BindGroupsRequest request,
        CancellationToken ct)
    {
        var user = await GetCurrentUserAsync(ct);
        if (user is null) return Unauthorized();

        var chat = await _db.Chats.FirstOrDefaultAsync(c => c.Id == chatId, ct);
        if (chat is null) return NotFound();

        if (!await _access.IsChatManagerAsync(chat, user, ct))
            return Forbid();

        if (chat.Type == ChatType.Direct)
            return Problem(statusCode: StatusCodes.Status400BadRequest, detail: "К личному чату нельзя привязать учебные группы.");

        var requestedIds = request.GroupIds?.Distinct().ToList() ?? [];
        var requestedNames = request.GroupNames?
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim().ToLowerInvariant())
            .Distinct()
            .ToList() ?? [];

        if (requestedIds.Count == 0 && requestedNames.Count == 0)
            return Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Укажите GroupIds или GroupNames для привязки.");

        var targetGroups = await _db.AcademicGroups
            .Where(g => requestedIds.Contains(g.Id) || requestedNames.Contains(g.Name.ToLower()))
            .ToListAsync(ct);

        if (targetGroups.Count == 0)
            return Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Ни одна из указанных учебных групп не найдена.");

        var existingBoundIds = await _db.ChatGroupBindings
            .Where(b => b.ChatId == chatId)
            .Select(b => b.AcademicGroupId)
            .ToListAsync(ct);

        var addedGroupIds = new List<Guid>();
        foreach (var g in targetGroups)
        {
            if (!existingBoundIds.Contains(g.Id))
            {
                _db.ChatGroupBindings.Add(new ChatGroupBinding { ChatId = chatId, AcademicGroupId = g.Id });
                addedGroupIds.Add(g.Id);
            }
        }

        if (addedGroupIds.Count > 0)
        {
            await _db.SaveChangesAsync(ct);
            await _hub.Clients.Group(ChatHub.ChatGroup(chatId))
                .SendAsync("ChatGroupsBound", new { chatId, addedGroupIds }, ct);
        }

        return Ok(await LoadChatDetailsAsync(chatId, user, ct));
    }

    /// <summary>Гибкие настройки прав обычных участников беседы (режим канала, медиа, закрепление, опросы).</summary>
    [HttpPatch("{chatId:guid}/settings")]
    [ProducesResponseType<ChatDetailsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ChatDetailsDto>> UpdateSettings(
        Guid chatId,
        [FromBody] UpdateChatSettingsRequest request,
        CancellationToken ct)
    {
        var user = await GetCurrentUserAsync(ct);
        if (user is null) return Unauthorized();

        var chat = await _db.Chats.FirstOrDefaultAsync(c => c.Id == chatId, ct);
        if (chat is null) return NotFound();

        var perms = await _access.GetEffectivePermissionsAsync(chat, user, ct);
        if (!perms.HasFlag(ChatPermissions.CanManageChatSettings))
            return Forbid();

        chat.AllowMembersSendMessages = request.AllowMembersSendMessages;
        chat.AllowMembersSendMedia = request.AllowMembersSendMedia;
        chat.AllowMembersPinMessages = request.AllowMembersPinMessages;
        chat.AllowMembersCreatePolls = request.AllowMembersCreatePolls;
        await _db.SaveChangesAsync(ct);

        await _hub.Clients.Group(ChatHub.ChatGroup(chatId))
            .SendAsync("ChatUpdated", new
            {
                chatId,
                settings = new ChatSettingsDto(
                    chat.AllowMembersSendMessages,
                    chat.AllowMembersSendMedia,
                    chat.AllowMembersPinMessages,
                    chat.AllowMembersCreatePolls),
                chat.IsArchived
            }, ct);

        return Ok(await LoadChatDetailsAsync(chatId, user, ct));
    }

    /// <summary>Перевод чата в архив семестра (Employee / Admin).</summary>
    [HttpPatch("{chatId:guid}/archive")]
    [Authorize(Policy = Policies.EmployeeOrAdmin)]
    [ProducesResponseType<ChatDetailsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ChatDetailsDto>> SetArchived(
        Guid chatId,
        [FromBody] SetChatArchivedRequest request,
        CancellationToken ct)
    {
        var user = await GetCurrentUserAsync(ct);
        if (user is null) return Unauthorized();

        var chat = await _db.Chats.FirstOrDefaultAsync(c => c.Id == chatId, ct);
        if (chat is null) return NotFound();

        if (!await _access.IsChatManagerAsync(chat, user, ct))
            return Forbid();

        chat.IsArchived = request.IsArchived;
        await _db.SaveChangesAsync(ct);

        await _hub.Clients.Group(ChatHub.ChatGroup(chatId))
            .SendAsync("ChatUpdated", new { chatId, chat.IsArchived }, ct);

        return Ok(await LoadChatDetailsAsync(chatId, user, ct));
    }

    /// <summary>Вкладка со всеми файлами и голосовыми сообщениями беседы.</summary>
    [HttpGet("{chatId:guid}/files")]
    [HttpGet("{chatId:guid}/materials")]
    [ProducesResponseType<List<ChatFileDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<List<ChatFileDto>>> GetFiles(
        Guid chatId,
        [FromQuery] string? search = null,
        [FromQuery] MessageType? type = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        var user = await GetCurrentUserAsync(ct);
        if (user is null) return Unauthorized();

        if (!await _db.Chats.AnyAsync(c => c.Id == chatId, ct))
            return NotFound();

        if (!await _access.HasAccessAsync(chatId, user, ct))
            return Forbid();

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var query = _db.Attachments
            .AsNoTracking()
            .Where(a => a.Message.ChatId == chatId);

        if (type.HasValue)
            query = query.Where(a => a.Message.MessageType == type.Value);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            query = query.Where(a =>
                a.OriginalFileName.ToLower().Contains(term)
                || (a.Message.Text != null && a.Message.Text.ToLower().Contains(term)));
        }

        var files = await query
            .OrderByDescending(a => a.Message.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new ChatFileDto(
                a.Id,
                a.MessageId,
                a.Message.ChatId,
                a.OriginalFileName,
                a.ContentType,
                a.FileSize,
                a.DurationSeconds,
                $"/api/messages/attachments/{a.Id}/download",
                a.Message.Text,
                a.Message.MessageType,
                a.Message.SenderId,
                a.Message.Sender.FullName,
                a.Message.CreatedAt
            ))
            .ToListAsync(ct);

        return Ok(files);
    }

    // ---------- КОНСТРУКТОР РОЛЕЙ ЧАТА (ChatRole) ----------

    /// <summary>Список всех кастомных ролей чата (включая шаблоны «Замстаросты» и «Профорг»).</summary>
    [HttpGet("{chatId:guid}/roles")]
    [ProducesResponseType<List<ChatRoleDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<List<ChatRoleDto>>> GetChatRoles(Guid chatId, CancellationToken ct)
    {
        var user = await GetCurrentUserAsync(ct);
        if (user is null) return Unauthorized();

        if (!await _db.Chats.AnyAsync(c => c.Id == chatId, ct)) return NotFound();
        if (!await _access.HasAccessAsync(chatId, user, ct)) return Forbid();

        return Ok(await LoadChatRolesAsync(chatId, ct));
    }

    /// <summary>Создание кастомной роли с бейджем и точечными правами.</summary>
    [HttpPost("{chatId:guid}/roles")]
    [ProducesResponseType<ChatRoleDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ChatRoleDto>> CreateChatRole(
        Guid chatId,
        [FromBody] CreateChatRoleRequest request,
        CancellationToken ct)
    {
        var user = await GetCurrentUserAsync(ct);
        if (user is null) return Unauthorized();

        var chat = await _db.Chats.FirstOrDefaultAsync(c => c.Id == chatId, ct);
        if (chat is null) return NotFound();

        var perms = await _access.GetEffectivePermissionsAsync(chat, user, ct);
        if (!perms.HasFlag(ChatPermissions.CanManageRoles))
            return Forbid();

        var name = request.Name.Trim();
        var lower = name.ToLowerInvariant();
        if (await _db.ChatRoles.AnyAsync(r => r.ChatId == chatId && r.Name.ToLower() == lower, ct))
        {
            return Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Роль уже существует",
                detail: $"Роль с названием '{name}' уже есть в этом чате.");
        }

        var role = new ChatRole
        {
            Id = Guid.NewGuid(),
            ChatId = chatId,
            Name = name,
            BadgeColor = string.IsNullOrWhiteSpace(request.BadgeColor) ? "#2563eb" : request.BadgeColor.Trim(),
            Permissions = request.ToPermissions(),
            IsTemplate = false,
            CreatedAt = DateTime.UtcNow
        };

        _db.ChatRoles.Add(role);
        await _db.SaveChangesAsync(ct);

        await _hub.Clients.Group(ChatHub.ChatGroup(chatId))
            .SendAsync("ChatRolesUpdated", new { chatId }, ct);

        var dto = new ChatRoleDto(role.Id, role.ChatId, role.Name, role.BadgeColor, role.Permissions,
            _access.ToPermissionNames(role.Permissions), role.IsTemplate, []);
        return CreatedAtAction(nameof(GetChatRoles), new { chatId }, dto);
    }

    /// <summary>Редактирование кастомной роли чата.</summary>
    [HttpPut("{chatId:guid}/roles/{roleId:guid}")]
    [ProducesResponseType<ChatRoleDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ChatRoleDto>> UpdateChatRole(
        Guid chatId,
        Guid roleId,
        [FromBody] UpdateChatRoleRequest request,
        CancellationToken ct)
    {
        var user = await GetCurrentUserAsync(ct);
        if (user is null) return Unauthorized();

        var chat = await _db.Chats.FirstOrDefaultAsync(c => c.Id == chatId, ct);
        if (chat is null) return NotFound();

        var perms = await _access.GetEffectivePermissionsAsync(chat, user, ct);
        if (!perms.HasFlag(ChatPermissions.CanManageRoles))
            return Forbid();

        var role = await _db.ChatRoles
            .Include(r => r.Assignments)
            .FirstOrDefaultAsync(r => r.Id == roleId && r.ChatId == chatId, ct);
        if (role is null) return NotFound();

        var newName = request.Name.Trim();
        var lower = newName.ToLowerInvariant();
        if (!string.Equals(role.Name, newName, StringComparison.OrdinalIgnoreCase)
            && await _db.ChatRoles.AnyAsync(r => r.ChatId == chatId && r.Id != roleId && r.Name.ToLower() == lower, ct))
        {
            return Problem(statusCode: StatusCodes.Status409Conflict, detail: $"Роль '{newName}' уже существует в этом чате.");
        }

        role.Name = newName;
        role.BadgeColor = string.IsNullOrWhiteSpace(request.BadgeColor) ? role.BadgeColor : request.BadgeColor.Trim();
        role.Permissions = request.ToPermissions();
        await _db.SaveChangesAsync(ct);

        await _hub.Clients.Group(ChatHub.ChatGroup(chatId))
            .SendAsync("ChatRolesUpdated", new { chatId }, ct);

        return Ok(new ChatRoleDto(role.Id, role.ChatId, role.Name, role.BadgeColor, role.Permissions,
            _access.ToPermissionNames(role.Permissions), role.IsTemplate, role.Assignments.Select(a => a.UserId).ToList()));
    }

    /// <summary>Удаление кастомной роли чата.</summary>
    [HttpDelete("{chatId:guid}/roles/{roleId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteChatRole(Guid chatId, Guid roleId, CancellationToken ct)
    {
        var user = await GetCurrentUserAsync(ct);
        if (user is null) return Unauthorized();

        var chat = await _db.Chats.FirstOrDefaultAsync(c => c.Id == chatId, ct);
        if (chat is null) return NotFound();

        var perms = await _access.GetEffectivePermissionsAsync(chat, user, ct);
        if (!perms.HasFlag(ChatPermissions.CanManageRoles))
            return Forbid();

        var role = await _db.ChatRoles.FirstOrDefaultAsync(r => r.Id == roleId && r.ChatId == chatId, ct);
        if (role is null) return NotFound();

        _db.ChatRoles.Remove(role);
        await _db.SaveChangesAsync(ct);

        await _hub.Clients.Group(ChatHub.ChatGroup(chatId))
            .SendAsync("ChatRolesUpdated", new { chatId }, ct);

        return NoContent();
    }

    /// <summary>Выдача кастомной роли участнику чата.</summary>
    [HttpPost("{chatId:guid}/roles/{roleId:guid}/members/{targetUserId:guid}")]
    [ProducesResponseType<List<ChatRoleDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<List<ChatRoleDto>>> AssignChatRoleToMember(
        Guid chatId,
        Guid roleId,
        Guid targetUserId,
        CancellationToken ct)
    {
        var user = await GetCurrentUserAsync(ct);
        if (user is null) return Unauthorized();

        var chat = await _db.Chats.FirstOrDefaultAsync(c => c.Id == chatId, ct);
        if (chat is null) return NotFound();

        var perms = await _access.GetEffectivePermissionsAsync(chat, user, ct);
        if (!perms.HasFlag(ChatPermissions.CanManageRoles))
            return Forbid();

        if (!await _db.ChatRoles.AnyAsync(r => r.Id == roleId && r.ChatId == chatId, ct))
            return NotFound();

        var targetUser = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == targetUserId, ct);
        if (targetUser is null) return NotFound();

        if (!await _access.HasAccessAsync(chatId, targetUser, ct))
            return Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Указанный пользователь не является участником этого чата.");

        if (!await _db.ChatMemberRoles.AnyAsync(mr => mr.ChatId == chatId && mr.ChatRoleId == roleId && mr.UserId == targetUserId, ct))
        {
            _db.ChatMemberRoles.Add(new ChatMemberRole
            {
                ChatId = chatId,
                ChatRoleId = roleId,
                UserId = targetUserId,
                AssignedAt = DateTime.UtcNow
            });
            await _db.SaveChangesAsync(ct);

            await _hub.Clients.Group(ChatHub.ChatGroup(chatId))
                .SendAsync("ChatRolesUpdated", new { chatId, targetUserId, roleId }, ct);
        }

        return Ok(await LoadChatRolesAsync(chatId, ct));
    }

    /// <summary>Снятие кастомной роли с участника чата.</summary>
    [HttpDelete("{chatId:guid}/roles/{roleId:guid}/members/{targetUserId:guid}")]
    [ProducesResponseType<List<ChatRoleDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<List<ChatRoleDto>>> RevokeChatRoleFromMember(
        Guid chatId,
        Guid roleId,
        Guid targetUserId,
        CancellationToken ct)
    {
        var user = await GetCurrentUserAsync(ct);
        if (user is null) return Unauthorized();

        var chat = await _db.Chats.FirstOrDefaultAsync(c => c.Id == chatId, ct);
        if (chat is null) return NotFound();

        var perms = await _access.GetEffectivePermissionsAsync(chat, user, ct);
        if (!perms.HasFlag(ChatPermissions.CanManageRoles))
            return Forbid();

        var assignment = await _db.ChatMemberRoles
            .FirstOrDefaultAsync(mr => mr.ChatId == chatId && mr.ChatRoleId == roleId && mr.UserId == targetUserId, ct);
        if (assignment is null) return NotFound();

        _db.ChatMemberRoles.Remove(assignment);
        await _db.SaveChangesAsync(ct);

        await _hub.Clients.Group(ChatHub.ChatGroup(chatId))
            .SendAsync("ChatRolesUpdated", new { chatId, targetUserId, roleId }, ct);

        return Ok(await LoadChatRolesAsync(chatId, ct));
    }

    /// <summary>Настройка уведомлений для конкретного чата (true — вкл, false — выкл, null — как по умолчанию).</summary>
    [HttpPut("{chatId:guid}/notifications")]
    [ProducesResponseType<ChatNotificationSettingDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ChatNotificationSettingDto>> SetChatNotifications(
        Guid chatId,
        [FromBody] SetChatNotificationRequest request,
        CancellationToken ct)
    {
        var user = await GetCurrentUserAsync(ct);
        if (user is null) return Unauthorized();

        var chat = await _db.Chats.AsNoTracking().FirstOrDefaultAsync(c => c.Id == chatId, ct);
        if (chat is null) return NotFound();

        if (!await _access.HasAccessAsync(chatId, user, ct))
            return Forbid();

        var existing = await _db.UserChatNotificationSettings
            .FirstOrDefaultAsync(s => s.UserId == user.Id && s.ChatId == chatId, ct);

        if (request.NotificationsEnabled is null)
        {
            if (existing is not null) _db.UserChatNotificationSettings.Remove(existing);
        }
        else if (existing is null)
        {
            _db.UserChatNotificationSettings.Add(new UserChatNotificationSetting
            {
                UserId = user.Id,
                ChatId = chatId,
                NotificationsEnabled = request.NotificationsEnabled.Value,
                UpdatedAt = DateTime.UtcNow
            });
        }
        else
        {
            existing.NotificationsEnabled = request.NotificationsEnabled.Value;
            existing.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(ct);

        var effective = request.NotificationsEnabled ?? user.DefaultNotificationsEnabled;
        return Ok(new ChatNotificationSettingDto(chat.Id, chat.Title, chat.Type, effective, request.NotificationsEnabled));
    }

    // ---------- Helpers ----------

    private Task<User?> GetCurrentUserAsync(CancellationToken ct)
    {
        var userId = User.GetUserId();
        return _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
    }

    private async Task<List<ChatRoleDto>> LoadChatRolesAsync(Guid chatId, CancellationToken ct)
    {
        var raw = await _db.ChatRoles
            .AsNoTracking()
            .Where(r => r.ChatId == chatId)
            .OrderByDescending(r => r.IsTemplate)
            .ThenBy(r => r.Name)
            .Select(r => new
            {
                r.Id,
                r.ChatId,
                r.Name,
                r.BadgeColor,
                r.Permissions,
                r.IsTemplate,
                MemberIds = r.Assignments.Select(a => a.UserId).ToList()
            })
            .ToListAsync(ct);

        return raw.Select(r => new ChatRoleDto(
            r.Id, r.ChatId, r.Name, r.BadgeColor, r.Permissions,
            _access.ToPermissionNames(r.Permissions), r.IsTemplate, r.MemberIds)).ToList();
    }

    private async Task<ChatDetailsDto> LoadChatDetailsAsync(Guid chatId, User currentUser, CancellationToken ct)
    {
        var chat = await _db.Chats
            .AsNoTracking()
            .Include(c => c.CreatedBy)
            .Include(c => c.OwnerGroup)
            .Include(c => c.GroupBindings).ThenInclude(b => b.AcademicGroup)
            .Include(c => c.Participants).ThenInclude(p => p.User)
            .FirstAsync(c => c.Id == chatId, ct);

        var isManager = await _access.IsChatManagerAsync(chat, currentUser, ct);
        var myPerms = await _access.GetEffectivePermissionsAsync(chat, currentUser, ct);
        var roles = await LoadChatRolesAsync(chatId, ct);

        var overrideValue = await _db.UserChatNotificationSettings
            .Where(s => s.UserId == currentUser.Id && s.ChatId == chatId)
            .Select(s => (bool?)s.NotificationsEnabled)
            .FirstOrDefaultAsync(ct);

        // Собираем полный список участников чата (прямые участники + студенты привязанных групп + кураторы групп)
        var participantMap = new Dictionary<Guid, (User User, DateTime JoinedAt)>();
        foreach (var p in chat.Participants)
            participantMap[p.UserId] = (p.User, p.JoinedAt);

        var groupIds = chat.GroupBindings.Select(b => b.AcademicGroupId).ToHashSet();
        if (chat.OwnerGroupId.HasValue) groupIds.Add(chat.OwnerGroupId.Value);

        if (groupIds.Count > 0)
        {
            var curatorIds = chat.GroupBindings
                .Where(b => b.AcademicGroup.CuratorId.HasValue)
                .Select(b => b.AcademicGroup.CuratorId!.Value)
                .ToHashSet();
            if (chat.OwnerGroup?.CuratorId is Guid ownerCuratorId)
                curatorIds.Add(ownerCuratorId);

            var groupMembers = await _db.Users
                .AsNoTracking()
                .Where(u => (u.AcademicGroupId != null && groupIds.Contains(u.AcademicGroupId.Value))
                            || curatorIds.Contains(u.Id))
                .ToListAsync(ct);

            foreach (var gm in groupMembers)
            {
                if (!participantMap.ContainsKey(gm.Id))
                    participantMap[gm.Id] = (gm, chat.CreatedAt);
            }
        }

        var badgesMap = await _access.GetUserBadgesMapAsync(chatId, participantMap.Keys, ct);

        var participantsDto = participantMap.Values
            .OrderBy(x => x.User.Role)
            .ThenBy(x => x.User.FullName)
            .Select(x => new ChatParticipantDto(
                x.User.Id,
                x.User.FullName,
                x.User.Email,
                x.User.Role,
                x.JoinedAt,
                badgesMap.GetValueOrDefault(x.User.Id, [])
            ))
            .ToList();

        var boundGroupsDto = chat.GroupBindings
            .OrderBy(b => b.AcademicGroup.Name)
            .Select(b => new BoundGroupDto(
                b.AcademicGroupId,
                b.AcademicGroup.Name,
                b.AcademicGroup.Course,
                b.AcademicGroup.Faculty))
            .ToList();

        return new ChatDetailsDto(
            chat.Id,
            chat.Title,
            chat.Type,
            new ChatSettingsDto(
                chat.AllowMembersSendMessages,
                chat.AllowMembersSendMedia,
                chat.AllowMembersPinMessages,
                chat.AllowMembersCreatePolls
            ),
            chat.IsArchived,
            chat.OwnerGroupId,
            chat.CreatedById,
            chat.CreatedBy.FullName,
            chat.CreatedAt,
            boundGroupsDto,
            participantsDto,
            roles,
            isManager,
            _access.ToPermissionNames(myPerms),
            overrideValue ?? currentUser.DefaultNotificationsEnabled,
            overrideValue
        );
    }
}
