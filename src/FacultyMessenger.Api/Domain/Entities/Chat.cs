using FacultyMessenger.Api.Domain.Enums;

namespace FacultyMessenger.Api.Domain.Entities;

/// <summary>Чат: личный, официальный чат группы, предметный, канал объявлений или своя беседа.</summary>
public class Chat
{
    public Guid Id { get; set; }
    public string Title { get; set; } = null!;
    public ChatType Type { get; set; }

    // Гибкие настройки прав обычных участников (свобода по умолчанию, как в Telegram/Discord)
    public bool AllowMembersSendMessages { get; set; } = true;
    public bool AllowMembersSendMedia { get; set; } = true;
    public bool AllowMembersPinMessages { get; set; } = true;
    public bool AllowMembersCreatePolls { get; set; } = true;

    /// <summary>Чат закрыт по итогам семестра (история и файлы сохраняются).</summary>
    public bool IsArchived { get; set; }

    /// <summary>Для официального группового чата — группа-владелец (староста и куратор получают полные права).</summary>
    public Guid? OwnerGroupId { get; set; }
    public AcademicGroup? OwnerGroup { get; set; }

    public Guid CreatedById { get; set; }
    public User CreatedBy { get; set; } = null!;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<ChatGroupBinding> GroupBindings { get; set; } = new List<ChatGroupBinding>();
    public ICollection<ChatParticipant> Participants { get; set; } = new List<ChatParticipant>();
    public ICollection<ChatRole> Roles { get; set; } = new List<ChatRole>();
    public ICollection<ChatMemberRole> MemberRoles { get; set; } = new List<ChatMemberRole>();
    public ICollection<Message> Messages { get; set; } = new List<Message>();
}

/// <summary>Привязка учебной группы к чату: все студенты группы и её куратор получают доступ.</summary>
public class ChatGroupBinding
{
    public Guid ChatId { get; set; }
    public Chat Chat { get; set; } = null!;

    public Guid AcademicGroupId { get; set; }
    public AcademicGroup AcademicGroup { get; set; } = null!;

    public DateTime BoundAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Индивидуальный участник чата (личные чаты, свои беседы, преподаватели).</summary>
public class ChatParticipant
{
    public Guid ChatId { get; set; }
    public Chat Chat { get; set; } = null!;

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Персональная настройка уведомлений пользователя для конкретного чата.</summary>
public class UserChatNotificationSetting
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public Guid ChatId { get; set; }
    public Chat Chat { get; set; } = null!;

    public bool NotificationsEnabled { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Кастомная роль внутри беседы (конструктор ролей с бейджем и точечными правами).</summary>
public class ChatRole
{
    public Guid Id { get; set; }

    public Guid ChatId { get; set; }
    public Chat Chat { get; set; } = null!;

    public string Name { get; set; } = null!;
    public string BadgeColor { get; set; } = "#2563eb";
    public ChatPermissions Permissions { get; set; } = ChatPermissions.None;
    public bool IsTemplate { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<ChatMemberRole> Assignments { get; set; } = new List<ChatMemberRole>();
}

/// <summary>Назначение кастомной роли участнику конкретного чата.</summary>
public class ChatMemberRole
{
    public Guid ChatId { get; set; }
    public Chat Chat { get; set; } = null!;

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public Guid ChatRoleId { get; set; }
    public ChatRole ChatRole { get; set; } = null!;

    public DateTime AssignedAt { get; set; } = DateTime.UtcNow;
}
