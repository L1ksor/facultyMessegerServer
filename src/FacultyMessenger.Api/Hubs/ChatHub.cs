using FacultyMessenger.Api.Auth;
using FacultyMessenger.Api.Contracts.Messages;
using FacultyMessenger.Api.Contracts.Polls;
using FacultyMessenger.Api.Data;
using FacultyMessenger.Api.Domain.Enums;
using FacultyMessenger.Api.Services.Chats;
using FacultyMessenger.Api.Services.Common;
using FacultyMessenger.Api.Services.Messages;
using FacultyMessenger.Api.Services.Polls;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace FacultyMessenger.Api.Hubs;

/// <summary>SignalR-хаб академического мессенджера (/hubs/chat).</summary>
[Authorize]
public class ChatHub : Hub
{
    private readonly AppDbContext _db;
    private readonly IChatAccessService _access;
    private readonly IMessageWorkflowService _workflow;
    private readonly IPollService _polls;

    public ChatHub(
        AppDbContext db,
        IChatAccessService access,
        IMessageWorkflowService workflow,
        IPollService polls)
    {
        _db = db;
        _access = access;
        _workflow = workflow;
        _polls = polls;
    }

    public static string UserGroup(Guid userId) => $"user:{userId}";
    public static string ChatGroup(Guid chatId) => $"chat:{chatId}";

    public override async Task OnConnectedAsync()
    {
        var principal = Context.User!;
        if (principal.MustChangePassword())
        {
            Context.Abort();
            return;
        }

        var userId = principal.GetUserId();
        var groupId = await _db.Users
            .Where(u => u.Id == userId)
            .Select(u => u.AcademicGroupId)
            .FirstOrDefaultAsync(Context.ConnectionAborted);

        var chatIds = await _db.Chats
            .Where(c => c.Participants.Any(p => p.UserId == userId)
                        || (groupId != null && c.GroupBindings.Any(b => b.AcademicGroupId == groupId))
                        || (groupId != null && c.OwnerGroupId == groupId)
                        || (c.OwnerGroup != null && c.OwnerGroup.CuratorId == userId)
                        || c.GroupBindings.Any(b => b.AcademicGroup.CuratorId == userId)
                        || (c.Type == ChatType.AnnouncementChannel && c.OwnerGroupId == null))
            .Select(c => c.Id)
            .ToListAsync(Context.ConnectionAborted);

        await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(userId), Context.ConnectionAborted);
        foreach (var chatId in chatIds)
            await Groups.AddToGroupAsync(Context.ConnectionId, ChatGroup(chatId), Context.ConnectionAborted);

        await base.OnConnectedAsync();
    }

    /// <summary>Подписка соединения на новый чат.</summary>
    public async Task JoinChat(Guid chatId)
    {
        var userId = Context.User!.GetUserId();
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, Context.ConnectionAborted)
            ?? throw new HubException("Пользователь не найден.");

        if (!await _access.HasAccessAsync(chatId, user, Context.ConnectionAborted))
            throw new HubException("Нет доступа к указанному чату.");

        await Groups.AddToGroupAsync(Context.ConnectionId, ChatGroup(chatId), Context.ConnectionAborted);
    }

    public Task LeaveChat(Guid chatId)
        => Groups.RemoveFromGroupAsync(Context.ConnectionId, ChatGroup(chatId), Context.ConnectionAborted);

    public async Task<SendMessageResponse> SendMessage(SendTextMessageRequest request)
    {
        try
        {
            return await _workflow.SendTextMessageAsync(request, Context.User!.GetUserId(), Context.ConnectionAborted);
        }
        catch (DomainException ex)
        {
            throw new HubException(ex.Message);
        }
    }

    public async Task<MessageDto> PinMessage(Guid messageId, bool isPinned)
    {
        try
        {
            return await _workflow.SetPinnedAsync(messageId, isPinned, Context.User!.GetUserId(), Context.ConnectionAborted);
        }
        catch (DomainException ex)
        {
            throw new HubException(ex.Message);
        }
    }

    public async Task DeleteMessage(Guid messageId)
    {
        try
        {
            await _workflow.DeleteMessageAsync(messageId, Context.User!.GetUserId(), Context.ConnectionAborted);
        }
        catch (DomainException ex)
        {
            throw new HubException(ex.Message);
        }
    }

    public async Task<MessageAcknowledgementDto> AcknowledgeMessage(Guid messageId)
    {
        try
        {
            return await _workflow.AcknowledgeAsync(messageId, Context.User!.GetUserId(), Context.ConnectionAborted);
        }
        catch (DomainException ex)
        {
            throw new HubException(ex.Message);
        }
    }

    public async Task<PollDto> VotePoll(Guid optionId)
    {
        try
        {
            return await _polls.VoteAsync(optionId, Context.User!.GetUserId(), Context.ConnectionAborted);
        }
        catch (DomainException ex)
        {
            throw new HubException(ex.Message);
        }
    }
}
