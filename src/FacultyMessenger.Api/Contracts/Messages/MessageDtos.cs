using System.ComponentModel.DataAnnotations;
using FacultyMessenger.Api.Contracts.Chats;
using FacultyMessenger.Api.Contracts.Polls;
using FacultyMessenger.Api.Domain.Enums;

namespace FacultyMessenger.Api.Contracts.Messages;

public record AttachmentDto(
    Guid Id,
    string OriginalFileName,
    string ContentType,
    long FileSize,
    int? DurationSeconds,
    string DownloadUrl
);

public record MessageDto(
    Guid Id,
    Guid ChatId,
    Guid SenderId,
    string SenderName,
    UserRole SenderRole,
    List<ChatRoleBadgeDto> SenderBadges,
    string? Text,
    MessageType MessageType,
    bool IsPinned,
    bool RequiresAcknowledgement,
    bool IsAcknowledgedByMe,
    int AcknowledgementsCount,
    DateTime CreatedAt,
    List<AttachmentDto> Attachments,
    PollDto? Poll = null
);

public record SendTextMessageRequest(
    [param: Required] Guid ChatId,
    [param: Required, MaxLength(8000)] string Text,
    bool RequiresAcknowledgement = false
);

public class UploadMessageFormRequest
{
    [Required]
    public Guid ChatId { get; set; }

    [Required]
    public IFormFile File { get; set; } = null!;

    [MaxLength(8000)]
    public string? Text { get; set; }

    public bool IsVoice { get; set; }

    [Range(0, 7200)]
    public int? DurationSeconds { get; set; }

    public bool RequiresAcknowledgement { get; set; }
}

public record SendMessageResponse(
    MessageDto Message
);

public record NewMessageNotificationDto(
    Guid MessageId,
    Guid ChatId,
    string ChatTitle,
    ChatType ChatType,
    Guid SenderId,
    string SenderName,
    string PreviewText,
    MessageType MessageType,
    DateTime CreatedAt
);

public record PinMessageRequest(bool IsPinned = true);

public record ChatFileDto(
    Guid AttachmentId,
    Guid MessageId,
    Guid ChatId,
    string OriginalFileName,
    string ContentType,
    long FileSize,
    int? DurationSeconds,
    string DownloadUrl,
    string? MessageText,
    MessageType MessageType,
    Guid SenderId,
    string SenderName,
    DateTime CreatedAt
);

public record MessageAcknowledgementDto(
    Guid MessageId,
    Guid ChatId,
    Guid UserId,
    string UserFullName,
    string? UserGroupName,
    DateTime ConfirmedAt,
    int TotalAcknowledgedCount
);

public record AcknowledgedUserDto(
    Guid UserId,
    string FullName,
    string Email,
    UserRole Role,
    Guid? AcademicGroupId,
    string? GroupName,
    DateTime ConfirmedAt
);

public record PendingUserDto(
    Guid UserId,
    string FullName,
    string Email,
    UserRole Role,
    Guid? AcademicGroupId,
    string? GroupName
);

public record MessageAckStatusDto(
    Guid MessageId,
    Guid ChatId,
    bool RequiresAcknowledgement,
    int AcknowledgedCount,
    int PendingCount,
    List<AcknowledgedUserDto> AcknowledgedUsers,
    List<PendingUserDto> PendingUsers
);
