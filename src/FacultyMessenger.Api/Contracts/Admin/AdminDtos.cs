using System.ComponentModel.DataAnnotations;
using FacultyMessenger.Api.Contracts.Auth;
using FacultyMessenger.Api.Domain.Enums;

namespace FacultyMessenger.Api.Contracts.Admin;

public record CreateUserRequest(
    [param: Required, MaxLength(200)] string FullName,
    [param: Required, EmailAddress] string Email,
    UserRole Role = UserRole.Student,
    Guid? AcademicGroupId = null,
    string? GroupName = null,
    [param: Range(1, 6)] int? Course = null,
    string? Faculty = null,
    [param: MinLength(8), MaxLength(128)] string? TemporaryPassword = null,
    bool SendEmail = true
);

public record CreateUserResponse(
    UserProfileDto User,
    string TemporaryPassword,
    bool EmailSent,
    string? EmailError
);

public record AssignRoleRequest(
    [param: Required] UserRole Role,
    Guid? AcademicGroupId = null
);

public class ImportUsersFormRequest
{
    [Required]
    public IFormFile File { get; set; } = null!;

    public string DefaultFaculty { get; set; } = "Факультет информационных технологий";

    public bool SendEmails { get; set; } = true;
}

public record ImportedUserItemDto(
    int RowNumber,
    Guid UserId,
    string FullName,
    string Email,
    UserRole Role,
    string? GroupName,
    string TemporaryPassword,
    bool EmailSent,
    string? EmailError
);

public record ImportUsersResultDto(
    int TotalRows,
    int CreatedCount,
    int SkippedCount,
    int GroupsCreatedCount,
    int EmailsSentCount,
    List<ImportedUserItemDto> CreatedUsers,
    List<string> Errors
);
