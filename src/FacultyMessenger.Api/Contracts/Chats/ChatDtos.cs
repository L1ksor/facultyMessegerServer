using System.ComponentModel.DataAnnotations;
using FacultyMessenger.Api.Domain.Enums;

namespace FacultyMessenger.Api.Contracts.Chats;

public record BoundGroupDto(Guid Id, string Name, int Course, string Faculty);

public record ChatRoleBadgeDto(
    Guid? RoleId,
    string Name,
    string BadgeColor
);

public record ChatRoleDto(
    Guid Id,
    Guid ChatId,
    string Name,
    string BadgeColor,
    ChatPermissions Permissions,
    List<string> PermissionNames,
    bool IsTemplate,
    List<Guid> MemberUserIds
);

public record ChatSettingsDto(
    bool AllowMembersSendMessages,
    bool AllowMembersSendMedia,
    bool AllowMembersPinMessages,
    bool AllowMembersCreatePolls
);

public record ChatParticipantDto(
    Guid UserId,
    string FullName,
    string Email,
    UserRole Role,
    DateTime JoinedAt,
    List<ChatRoleBadgeDto> Badges
);

public record ChatSummaryDto(
    Guid Id,
    string Title,
    ChatType Type,
    ChatSettingsDto Settings,
    bool IsArchived,
    Guid? OwnerGroupId,
    Guid CreatedById,
    DateTime CreatedAt,
    List<BoundGroupDto> BoundGroups,
    bool NotificationsEnabled = true,
    bool? NotificationOverride = null
);

public record ChatDetailsDto(
    Guid Id,
    string Title,
    ChatType Type,
    ChatSettingsDto Settings,
    bool IsArchived,
    Guid? OwnerGroupId,
    Guid CreatedById,
    string CreatedByName,
    DateTime CreatedAt,
    List<BoundGroupDto> BoundGroups,
    List<ChatParticipantDto> Participants,
    List<ChatRoleDto> Roles,
    bool IsManager,
    List<string> MyPermissions,
    bool NotificationsEnabled = true,
    bool? NotificationOverride = null
);

public record CreateSubjectChatRequest(
    [param: Required, MaxLength(200)] string Title,
    List<Guid>? GroupIds = null,
    bool AllowMembersSendMessages = true,
    bool AllowMembersSendMedia = true,
    bool AllowMembersPinMessages = false,
    bool AllowMembersCreatePolls = false
);

public record CreateCustomChatRequest(
    [param: Required, MaxLength(200)] string Title,
    List<Guid>? ParticipantUserIds = null
);

public record CreateDirectChatRequest(
    [param: Required] Guid TargetUserId
);

public record AddParticipantRequest(
    [param: Required] Guid UserId
);

public record BindGroupsRequest(
    List<Guid>? GroupIds = null,
    List<string>? GroupNames = null
);

public record UpdateChatSettingsRequest(
    bool AllowMembersSendMessages,
    bool AllowMembersSendMedia,
    bool AllowMembersPinMessages,
    bool AllowMembersCreatePolls
);

public record SetChatArchivedRequest(bool IsArchived);

public record CreateChatRoleRequest(
    [param: Required, MaxLength(60)] string Name,
    [param: MaxLength(20)] string BadgeColor = "#2563eb",
    bool CanSendMessages = true,
    bool CanSendMedia = true,
    bool CanPinMessages = false,
    bool CanDeleteMessages = false,
    bool CanCreatePolls = false,
    bool CanSendAcknowledgements = false,
    bool CanManageRoles = false,
    bool CanManageChatSettings = false
)
{
    public ChatPermissions ToPermissions()
    {
        var p = ChatPermissions.None;
        if (CanSendMessages) p |= ChatPermissions.CanSendMessages;
        if (CanSendMedia) p |= ChatPermissions.CanSendMedia;
        if (CanPinMessages) p |= ChatPermissions.CanPinMessages;
        if (CanDeleteMessages) p |= ChatPermissions.CanDeleteMessages;
        if (CanCreatePolls) p |= ChatPermissions.CanCreatePolls;
        if (CanSendAcknowledgements) p |= ChatPermissions.CanSendAcknowledgements;
        if (CanManageRoles) p |= ChatPermissions.CanManageRoles;
        if (CanManageChatSettings) p |= ChatPermissions.CanManageChatSettings;
        return p;
    }
}

public record UpdateChatRoleRequest(
    [param: Required, MaxLength(60)] string Name,
    [param: MaxLength(20)] string BadgeColor = "#2563eb",
    bool CanSendMessages = true,
    bool CanSendMedia = true,
    bool CanPinMessages = false,
    bool CanDeleteMessages = false,
    bool CanCreatePolls = false,
    bool CanSendAcknowledgements = false,
    bool CanManageRoles = false,
    bool CanManageChatSettings = false
)
{
    public ChatPermissions ToPermissions()
    {
        var p = ChatPermissions.None;
        if (CanSendMessages) p |= ChatPermissions.CanSendMessages;
        if (CanSendMedia) p |= ChatPermissions.CanSendMedia;
        if (CanPinMessages) p |= ChatPermissions.CanPinMessages;
        if (CanDeleteMessages) p |= ChatPermissions.CanDeleteMessages;
        if (CanCreatePolls) p |= ChatPermissions.CanCreatePolls;
        if (CanSendAcknowledgements) p |= ChatPermissions.CanSendAcknowledgements;
        if (CanManageRoles) p |= ChatPermissions.CanManageRoles;
        if (CanManageChatSettings) p |= ChatPermissions.CanManageChatSettings;
        return p;
    }
}

public record SetChatNotificationRequest(bool? NotificationsEnabled);

public record BatchChatNotificationItem(
    [param: Required] Guid ChatId,
    bool? NotificationsEnabled
);

public record BatchUpdateChatNotificationsRequest(
    bool? DefaultNotificationsEnabled,
    List<BatchChatNotificationItem>? Chats = null
);

public record ChatNotificationSettingDto(
    Guid ChatId,
    string ChatTitle,
    ChatType ChatType,
    bool EffectiveNotificationsEnabled,
    bool? OverrideValue
);

public record UserNotificationPreferencesDto(
    bool DefaultNotificationsEnabled,
    List<ChatNotificationSettingDto> Chats
);
