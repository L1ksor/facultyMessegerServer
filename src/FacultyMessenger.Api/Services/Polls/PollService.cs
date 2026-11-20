using FacultyMessenger.Api.Contracts.Messages;
using FacultyMessenger.Api.Contracts.Polls;
using FacultyMessenger.Api.Data;
using FacultyMessenger.Api.Domain.Entities;
using FacultyMessenger.Api.Domain.Enums;
using FacultyMessenger.Api.Hubs;
using FacultyMessenger.Api.Services.Chats;
using FacultyMessenger.Api.Services.Common;
using FacultyMessenger.Api.Services.Messages;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace FacultyMessenger.Api.Services.Polls;

public interface IPollService
{
    Task<MessageDto> CreatePollAsync(CreatePollRequest request, Guid creatorUserId, CancellationToken ct = default);
    Task<PollDto> GetPollAsync(Guid pollId, Guid currentUserId, CancellationToken ct = default);
    Task<PollDto> VoteAsync(Guid optionId, Guid userId, CancellationToken ct = default);
    Task<PollDto> UnvoteAsync(Guid optionId, Guid userId, CancellationToken ct = default);
    Task<PollDto> SetClosedAsync(Guid pollId, bool isClosed, Guid userId, CancellationToken ct = default);
}

public class PollService : IPollService
{
    private readonly AppDbContext _db;
    private readonly IChatAccessService _access;
    private readonly IMessageWorkflowService _workflow;
    private readonly IHubContext<ChatHub> _hub;

    public PollService(AppDbContext db, IChatAccessService access, IMessageWorkflowService workflow, IHubContext<ChatHub> hub)
    {
        _db = db;
        _access = access;
        _workflow = workflow;
        _hub = hub;
    }

    public async Task<MessageDto> CreatePollAsync(CreatePollRequest request, Guid creatorUserId, CancellationToken ct = default)
    {
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == creatorUserId, ct)
            ?? throw new DomainNotFoundException("Пользователь не найден.");

        var chat = await _db.Chats.FirstOrDefaultAsync(c => c.Id == request.ChatId, ct)
            ?? throw new DomainNotFoundException("Чат не найден.");

        if (!await _access.HasAccessAsync(chat.Id, user, ct))
            throw new DomainForbiddenException("Нет доступа к указанному чату.");

        if (chat.IsArchived)
            throw new DomainForbiddenException("Чат находится в архиве семестра. Создание опросов запрещено.");

        var perms = await _access.GetEffectivePermissionsAsync(chat, user, ct);
        if (!perms.HasFlag(ChatPermissions.CanCreatePolls))
            throw new DomainForbiddenException("Создание опросов в этом чате ограничено настройками беседы.");

        if (request.RequiresAcknowledgement && !perms.HasFlag(ChatPermissions.CanSendAcknowledgements))
            throw new DomainForbiddenException("Недостаточно прав для отправки опроса с кнопкой «Ознакомлен».");

        var cleanedOptions = request.Options
            .Where(o => !string.IsNullOrWhiteSpace(o.OptionText))
            .ToList();

        if (cleanedOptions.Count < 2)
            throw new DomainBadRequestException("В опросе должно быть минимум два непустых варианта ответа.");

        var message = new Message
        {
            Id = Guid.NewGuid(),
            ChatId = chat.Id,
            SenderId = user.Id,
            Text = request.Question.Trim(),
            MessageType = MessageType.Poll,
            RequiresAcknowledgement = request.RequiresAcknowledgement,
            CreatedAt = DateTime.UtcNow
        };

        var poll = new Poll
        {
            Id = Guid.NewGuid(),
            MessageId = message.Id,
            Question = request.Question.Trim(),
            AllowMultipleChoice = request.AllowMultipleChoice,
            IsClosed = false
        };

        for (var i = 0; i < cleanedOptions.Count; i++)
        {
            var opt = cleanedOptions[i];
            poll.Options.Add(new PollOption
            {
                Id = Guid.NewGuid(),
                PollId = poll.Id,
                OptionText = opt.OptionText.Trim(),
                MaxVotes = opt.MaxVotes,
                SortOrder = i
            });
        }

        _db.Messages.Add(message);
        _db.Polls.Add(poll);
        await _db.SaveChangesAsync(ct);

        var messageDto = await _workflow.LoadMessageDtoAsync(message.Id, user.Id, ct);
        await _workflow.BroadcastNewMessageAsync(chat, messageDto, ct);

        return messageDto;
    }

    public async Task<PollDto> GetPollAsync(Guid pollId, Guid currentUserId, CancellationToken ct = default)
    {
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == currentUserId, ct)
            ?? throw new DomainNotFoundException("Пользователь не найден.");

        var chatId = await _db.Polls
            .Where(p => p.Id == pollId)
            .Select(p => (Guid?)p.Message.ChatId)
            .FirstOrDefaultAsync(ct)
            ?? throw new DomainNotFoundException("Опрос не найден.");

        if (!await _access.HasAccessAsync(chatId, user, ct))
            throw new DomainForbiddenException("Нет доступа к чату опроса.");

        return await LoadPollDtoAsync(pollId, currentUserId, ct);
    }

    public async Task<PollDto> VoteAsync(Guid optionId, Guid userId, CancellationToken ct = default)
    {
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new DomainNotFoundException("Пользователь не найден.");

        var strategy = _db.Database.CreateExecutionStrategy();
        var result = await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            var option = await _db.PollOptions
                .FromSqlInterpolated($"SELECT *, xmin FROM poll_options WHERE \"Id\" = {optionId} FOR UPDATE")
                .FirstOrDefaultAsync(ct)
                ?? throw new DomainNotFoundException("Вариант опроса не найден.");

            var poll = await _db.Polls
                .Include(p => p.Message)
                    .ThenInclude(m => m.Chat)
                .FirstOrDefaultAsync(p => p.Id == option.PollId, ct)
                ?? throw new DomainNotFoundException("Опрос не найден.");

            if (!await _access.HasAccessAsync(poll.Message.ChatId, user, ct))
                throw new DomainForbiddenException("Нет доступа к чату опроса.");

            if (poll.Message.Chat.IsArchived)
                throw new DomainForbiddenException("Чат находится в архиве. Голосование закрыто.");

            if (poll.IsClosed)
                throw new DomainConflictException("Опрос закрыт для голосования.");

            var alreadyVotedThisOption = await _db.PollVotes
                .AnyAsync(v => v.PollOptionId == option.Id && v.UserId == userId, ct);
            if (alreadyVotedThisOption)
                throw new DomainConflictException("Вы уже проголосовали за этот вариант.");

            if (!poll.AllowMultipleChoice)
            {
                var alreadyVotedInPoll = await _db.PollVotes
                    .AnyAsync(v => v.PollId == poll.Id && v.UserId == userId, ct);
                if (alreadyVotedInPoll)
                    throw new DomainConflictException("В этом опросе можно выбрать только один вариант. Сначала отмените предыдущий голос.");
            }

            var currentVotes = await _db.PollVotes
                .CountAsync(v => v.PollOptionId == option.Id, ct);

            if (option.MaxVotes.HasValue && currentVotes >= option.MaxVotes.Value)
                throw new DomainConflictException("Лимит мест по выбранному варианту исчерпан.");

            _db.PollVotes.Add(new PollVote
            {
                Id = Guid.NewGuid(),
                PollId = poll.Id,
                PollOptionId = option.Id,
                UserId = userId,
                VotedAt = DateTime.UtcNow
            });

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            return (poll.Id, poll.Message.ChatId);
        });

        var dto = await LoadPollDtoAsync(result.Id, userId, ct);
        await _hub.Clients.Group(ChatHub.ChatGroup(result.ChatId))
            .SendAsync("PollUpdated", dto, ct);

        return dto;
    }

    public async Task<PollDto> UnvoteAsync(Guid optionId, Guid userId, CancellationToken ct = default)
    {
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new DomainNotFoundException("Пользователь не найден.");

        var option = await _db.PollOptions
            .Include(o => o.Poll)
                .ThenInclude(p => p.Message)
                    .ThenInclude(m => m.Chat)
            .FirstOrDefaultAsync(o => o.Id == optionId, ct)
            ?? throw new DomainNotFoundException("Вариант опроса не найден.");

        if (!await _access.HasAccessAsync(option.Poll.Message.ChatId, user, ct))
            throw new DomainForbiddenException("Нет доступа к чату опроса.");

        if (option.Poll.Message.Chat.IsArchived)
            throw new DomainForbiddenException("Чат находится в архиве. Изменение голоса невозможно.");

        if (option.Poll.IsClosed)
            throw new DomainConflictException("Опрос закрыт для голосования.");

        var vote = await _db.PollVotes
            .FirstOrDefaultAsync(v => v.PollOptionId == optionId && v.UserId == userId, ct)
            ?? throw new DomainNotFoundException("Ваш голос за этот вариант не найден.");

        _db.PollVotes.Remove(vote);
        await _db.SaveChangesAsync(ct);

        var dto = await LoadPollDtoAsync(option.PollId, userId, ct);
        await _hub.Clients.Group(ChatHub.ChatGroup(option.Poll.Message.ChatId))
            .SendAsync("PollUpdated", dto, ct);

        return dto;
    }

    public async Task<PollDto> SetClosedAsync(Guid pollId, bool isClosed, Guid userId, CancellationToken ct = default)
    {
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new DomainNotFoundException("Пользователь не найден.");

        var poll = await _db.Polls
            .Include(p => p.Message)
                .ThenInclude(m => m.Chat)
            .FirstOrDefaultAsync(p => p.Id == pollId, ct)
            ?? throw new DomainNotFoundException("Опрос не найден.");

        var isAuthor = poll.Message.SenderId == user.Id;
        var isManager = await _access.IsChatManagerAsync(poll.Message.Chat, user, ct);
        if (!isAuthor && !isManager)
            throw new DomainForbiddenException("Закрывать или открывать опрос могут только его автор или менеджеры чата.");

        poll.IsClosed = isClosed;
        await _db.SaveChangesAsync(ct);

        var dto = await LoadPollDtoAsync(poll.Id, userId, ct);
        await _hub.Clients.Group(ChatHub.ChatGroup(poll.Message.ChatId))
            .SendAsync("PollUpdated", dto, ct);

        return dto;
    }

    private async Task<PollDto> LoadPollDtoAsync(Guid pollId, Guid currentUserId, CancellationToken ct)
    {
        return await _db.Polls
            .AsNoTracking()
            .Where(p => p.Id == pollId)
            .Select(p => new PollDto(
                p.Id,
                p.MessageId,
                p.Message.ChatId,
                p.Question,
                p.IsClosed,
                p.AllowMultipleChoice,
                p.Options.SelectMany(o => o.Votes).Count(),
                p.Options
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
            ))
            .FirstAsync(ct);
    }
}
