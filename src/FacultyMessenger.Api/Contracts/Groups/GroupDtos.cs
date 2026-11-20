using System.ComponentModel.DataAnnotations;

namespace FacultyMessenger.Api.Contracts.Groups;

public record GroupPersonDto(
    Guid Id,
    string FullName,
    string Email
);

public record AcademicGroupDto(
    Guid Id,
    string Name,
    int Course,
    string Faculty,
    int StudentsCount,
    Guid? GroupChatId,
    GroupPersonDto? Leader,
    GroupPersonDto? Curator
);

public record CreateAcademicGroupRequest(
    [param: Required, MaxLength(32)] string Name,
    [param: Range(1, 6)] int Course,
    [param: Required, MaxLength(200)] string Faculty,
    Guid? CuratorId = null
);

public record UpdateAcademicGroupRequest(
    [param: Required, MaxLength(32)] string Name,
    [param: Range(1, 6)] int Course,
    [param: Required, MaxLength(200)] string Faculty,
    Guid? LeaderId = null,
    Guid? CuratorId = null
);

public record AssignGroupLeaderRequest(
    [param: Required] Guid StudentUserId
);

public record AssignGroupCuratorRequest(
    Guid? CuratorUserId
);
