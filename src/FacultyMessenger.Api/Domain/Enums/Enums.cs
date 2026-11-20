namespace FacultyMessenger.Api.Domain.Enums;

/// <summary>Глобальные роли на платформе (3 уровня).</summary>
public enum UserRole
{
    Admin = 0,
    Employee = 1,
    Student = 2
}

/// <summary>Тип чата.</summary>
public enum ChatType
{
    Direct = 0,
    Group = 1,
    Subject = 2,
    AnnouncementChannel = 3,
    CustomGroup = 4
}

/// <summary>Тип сообщения.</summary>
public enum MessageType
{
    Text = 0,
    Voice = 1,
    File = 2,
    Poll = 3
}

/// <summary>Точечные права кастомных ролей внутри беседы (конструктор ролей).</summary>
[Flags]
public enum ChatPermissions
{
    None = 0,
    CanSendMessages = 1 << 0,
    CanSendMedia = 1 << 1,
    CanPinMessages = 1 << 2,
    CanDeleteMessages = 1 << 3,
    CanCreatePolls = 1 << 4,
    CanSendAcknowledgements = 1 << 5,
    CanManageRoles = 1 << 6,
    CanManageChatSettings = 1 << 7,

    All = CanSendMessages
          | CanSendMedia
          | CanPinMessages
          | CanDeleteMessages
          | CanCreatePolls
          | CanSendAcknowledgements
          | CanManageRoles
          | CanManageChatSettings
}
