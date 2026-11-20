using FacultyMessenger.Api.Domain.Enums;

namespace FacultyMessenger.Api.Domain.Entities;

/// <summary>Пользователь системы (Student, Employee, Admin).</summary>
public class User
{
    public Guid Id { get; set; }
    public string FullName { get; set; } = null!;

    /// <summary>Уникальный логин. Хранится в нижнем регистре.</summary>
    public string Email { get; set; } = null!;

    public string PasswordHash { get; set; } = null!;
    public UserRole Role { get; set; } = UserRole.Student;

    public Guid? AcademicGroupId { get; set; }
    public AcademicGroup? AcademicGroup { get; set; }

    public bool MustChangePassword { get; set; }
    public bool IsEiosVerified { get; set; }

    /// <summary>Внешний идентификатор пользователя в ЭИОС вуза (для повторного входа).</summary>
    public string? EiosExternalId { get; set; }

    /// <summary>
    /// Получать ли уведомления по умолчанию (в том числе от новых чатов и пользователей).
    /// false — уведомления приходят только от явно выбранных пользователем чатов.
    /// </summary>
    public bool DefaultNotificationsEnabled { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<AcademicGroup> CuratedGroups { get; set; } = new List<AcademicGroup>();
    public ICollection<ChatParticipant> ChatParticipations { get; set; } = new List<ChatParticipant>();
    public ICollection<UserChatNotificationSetting> ChatNotificationSettings { get; set; } = new List<UserChatNotificationSetting>();
    public ICollection<ChatMemberRole> ChatRoles { get; set; } = new List<ChatMemberRole>();
    public ICollection<Message> SentMessages { get; set; } = new List<Message>();
    public ICollection<MessageAcknowledgement> Acknowledgements { get; set; } = new List<MessageAcknowledgement>();
    public ICollection<PollVote> PollVotes { get; set; } = new List<PollVote>();
}
