using FacultyMessenger.Api.Auth;
using FacultyMessenger.Api.Contracts.Chats;
using FacultyMessenger.Api.Contracts.Messages;
using FacultyMessenger.Api.Contracts.Polls;
using FacultyMessenger.Api.Data;
using FacultyMessenger.Api.Domain.Entities;
using FacultyMessenger.Api.Domain.Enums;
using FacultyMessenger.Api.Services.Chats;
using FacultyMessenger.Api.Services.Common;
using FacultyMessenger.Api.Services.Messages;
using FacultyMessenger.Api.Services.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FacultyMessenger.Api.Controllers;

[ApiController]
[Route("api/messages")]
[Authorize]
public class MessagesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IChatAccessService _access;
    private readonly IFileStorageService _storage;
    private readonly IMessageWorkflowService _workflow;

    public MessagesController(
        AppDbContext db,
        IChatAccessService access,
        IFileStorageService storage,
        IMessageWorkflowService workflow)
    {
        _db = db;
        _access = access;
        _storage = storage;
        _workflow = workflow;
    }

    /// <summary>История сообщений чата (от новых к старым).</summary>
    [HttpGet("/api/chats/{chatId:guid}/messages")]
    [ProducesResponseType<List<MessageDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<ActionResult<List<MessageDto>>> GetChatMessages(
        Guid chatId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
        => GetMessagesInternalAsync(chatId, page, pageSize, ct);

    /// <summary>История сообщений чата по параметру chatId.</summary>
    [HttpGet]
    [ProducesResponseType<List<MessageDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<ActionResult<List<MessageDto>>> GetMessages(
        [FromQuery] Guid chatId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
        => GetMessagesInternalAsync(chatId, page, pageSize, ct);

    /// <summary>Отправка текстового сообщения в чат.</summary>
    [HttpPost]
    [ProducesResponseType<SendMessageResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SendMessageResponse>> SendTextMessage(
        [FromBody] SendTextMessageRequest request,
        CancellationToken ct)
    {
        try
        {
            var response = await _workflow.SendTextMessageAsync(request, User.GetUserId(), ct);
            return CreatedAtAction(nameof(GetChatMessages), new { chatId = request.ChatId }, response);
        }
        catch (DomainException ex)
        {
            return Problem(statusCode: ex.StatusCode, detail: ex.Message);
        }
    }

    /// <summary>Загрузка голосового или файлового сообщения в хранилище Garage S3.</summary>
    [HttpPost("upload")]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<SendMessageResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SendMessageResponse>> UploadMessage(
        [FromForm] UploadMessageFormRequest request,
        CancellationToken ct)
    {
        var userId = User.GetUserId();
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
            return Unauthorized();

        var chat = await _db.Chats.FirstOrDefaultAsync(c => c.Id == request.ChatId, ct);
        if (chat is null)
            return NotFound();

        if (!await _access.HasAccessAsync(chat.Id, user, ct))
            return Forbid();

        if (chat.IsArchived)
        {
            return Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Отправка запрещена",
                detail: "Чат находится в архиве семестра. Отправка новых сообщений закрыта.");
        }

        var perms = await _access.GetEffectivePermissionsAsync(chat, user, ct);
        if (!perms.HasFlag(ChatPermissions.CanSendMessages) || !perms.HasFlag(ChatPermissions.CanSendMedia))
        {
            return Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Отправка медиа ограничена",
                detail: "Отправка файлов и голосовых сообщений ограничена настройками этой беседы.");
        }

        if (request.RequiresAcknowledgement && !perms.HasFlag(ChatPermissions.CanSendAcknowledgements))
        {
            return Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Недостаточно прав",
                detail: "Недостаточно прав для отправки объявлений с кнопкой «Ознакомлен».");
        }

        StoredFileResult stored;
        try
        {
            stored = await _storage.SaveFileAsync(request.File, request.IsVoice, ct);
        }
        catch (FileValidationException ex)
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Ошибка валидации файла",
                detail: ex.Message);
        }

        var message = new Message
        {
            Id = Guid.NewGuid(),
            ChatId = chat.Id,
            SenderId = user.Id,
            Text = string.IsNullOrWhiteSpace(request.Text) ? null : request.Text.Trim(),
            MessageType = request.IsVoice ? MessageType.Voice : MessageType.File,
            RequiresAcknowledgement = request.RequiresAcknowledgement,
            CreatedAt = DateTime.UtcNow
        };

        var attachment = new Attachment
        {
            Id = Guid.NewGuid(),
            MessageId = message.Id,
            OriginalFileName = stored.OriginalFileName,
            StoredFilePath = stored.StoredFilePath,
            ContentType = stored.ContentType,
            FileSize = stored.FileSize,
            DurationSeconds = request.IsVoice ? request.DurationSeconds : null
        };

        _db.Messages.Add(message);
        _db.Attachments.Add(attachment);
        await _db.SaveChangesAsync(ct);

        var dto = await _workflow.LoadMessageDtoAsync(message.Id, user.Id, ct);
        await _workflow.BroadcastNewMessageAsync(chat, dto, ct);

        return CreatedAtAction(nameof(GetChatMessages), new { chatId = chat.Id }, new SendMessageResponse(dto));
    }

    /// <summary>Скачивание или потоковое воспроизведение вложения из Garage S3.</summary>
    [HttpGet("attachments/{attachmentId:guid}/download")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DownloadAttachment(Guid attachmentId, CancellationToken ct)
    {
        var userId = User.GetUserId();
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
            return Unauthorized();

        var attachment = await _db.Attachments
            .AsNoTracking()
            .Include(a => a.Message)
            .FirstOrDefaultAsync(a => a.Id == attachmentId, ct);

        if (attachment is null)
            return NotFound();

        if (!await _access.HasAccessAsync(attachment.Message.ChatId, user, ct))
            return Forbid();

        try
        {
            var fileResult = await _storage.OpenReadAsync(attachment.StoredFilePath, attachment.ContentType, ct);
            if (fileResult.ResourceToDispose is not null)
                HttpContext.Response.RegisterForDispose(fileResult.ResourceToDispose);

            return File(
                fileResult.Stream,
                fileResult.ContentType,
                attachment.OriginalFileName,
                enableRangeProcessing: fileResult.IsSeekable
            );
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }
    }

    /// <summary>Закрепление или открепление сообщения в чате.</summary>
    [HttpPatch("{id:guid}/pin")]
    [ProducesResponseType<MessageDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MessageDto>> PinMessage(
        Guid id,
        [FromBody] PinMessageRequest? request,
        CancellationToken ct)
    {
        try
        {
            return Ok(await _workflow.SetPinnedAsync(id, request?.IsPinned ?? true, User.GetUserId(), ct));
        }
        catch (DomainException ex)
        {
            return Problem(statusCode: ex.StatusCode, detail: ex.Message);
        }
    }

    /// <summary>Удаление сообщения (автор сообщения или участник с правом CanDeleteMessages).</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteMessage(Guid id, CancellationToken ct)
    {
        try
        {
            await _workflow.DeleteMessageAsync(id, User.GetUserId(), ct);
            return NoContent();
        }
        catch (DomainException ex)
        {
            return Problem(statusCode: ex.StatusCode, detail: ex.Message);
        }
    }

    /// <summary>Подтверждение прочтения важного сообщения («Ознакомлен»).</summary>
    [HttpPost("{id:guid}/ack")]
    [ProducesResponseType<MessageAcknowledgementDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MessageAcknowledgementDto>> Acknowledge(Guid id, CancellationToken ct)
    {
        try
        {
            return Ok(await _workflow.AcknowledgeAsync(id, User.GetUserId(), ct));
        }
        catch (DomainException ex)
        {
            return Problem(statusCode: ex.StatusCode, detail: ex.Message);
        }
    }

    /// <summary>Статус ознакомления по группе/чату: кто подтвердил прочтение и кто ещё не прочитал.</summary>
    [HttpGet("{id:guid}/ack-status")]
    [ProducesResponseType<MessageAckStatusDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MessageAckStatusDto>> GetAckStatus(
        Guid id,
        [FromQuery] Guid? groupId = null,
        CancellationToken ct = default)
    {
        try
        {
            return Ok(await _workflow.GetAckStatusAsync(id, User.GetUserId(), groupId, ct));
        }
        catch (DomainException ex)
        {
            return Problem(statusCode: ex.StatusCode, detail: ex.Message);
        }
    }

    private async Task<ActionResult<List<MessageDto>>> GetMessagesInternalAsync(
        Guid chatId,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        var userId = User.GetUserId();
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
            return Unauthorized();

        if (!await _db.Chats.AnyAsync(c => c.Id == chatId, ct))
            return NotFound();

        if (!await _access.HasAccessAsync(chatId, user, ct))
            return Forbid();

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var messages = await _db.Messages
            .AsNoTracking()
            .Where(m => m.ChatId == chatId)
            .OrderByDescending(m => m.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
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
                m.Acknowledgements.Any(a => a.UserId == userId),
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
                            o.Votes.Any(v => v.UserId == userId),
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
            .ToListAsync(ct);

        var badgesMap = await _access.GetUserBadgesMapAsync(chatId, messages.Select(m => m.SenderId), ct);
        var enriched = messages
            .Select(m => m with { SenderBadges = badgesMap.GetValueOrDefault(m.SenderId, []) })
            .ToList();

        return Ok(enriched);
    }
}
