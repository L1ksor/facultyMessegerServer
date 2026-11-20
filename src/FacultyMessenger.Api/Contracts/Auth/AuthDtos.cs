using System.ComponentModel.DataAnnotations;
using FacultyMessenger.Api.Domain.Entities;
using FacultyMessenger.Api.Domain.Enums;

namespace FacultyMessenger.Api.Contracts.Auth;

public record LoginRequest(
    [param: Required, EmailAddress] string Email,
    [param: Required] string Password
);

public record EiosLoginRequest(
    [param: Required, MaxLength(150)] string Username,
    [param: Required] string Password,
    [param: MaxLength(32)] string? GroupName = null,
    [param: MaxLength(200)] string? FullName = null,
    bool UseDemoFallback = false
);

public record ChangeInitialPasswordRequest(
    [param: Required] string CurrentPassword,
    [param: Required, MinLength(8), MaxLength(128)] string NewPassword
);

public record UserProfileDto(
    Guid Id,
    string FullName,
    string Email,
    UserRole Role,
    Guid? AcademicGroupId,
    string? AcademicGroupName,
    bool MustChangePassword,
    bool IsEiosVerified,
    bool DefaultNotificationsEnabled
)
{
    public static UserProfileDto FromEntity(User user, string? groupName = null) => new(
        user.Id,
        user.FullName,
        user.Email,
        user.Role,
        user.AcademicGroupId,
        groupName ?? user.AcademicGroup?.Name,
        user.MustChangePassword,
        user.IsEiosVerified,
        user.DefaultNotificationsEnabled
    );
}

public record AuthResponse(
    string AccessToken,
    DateTime ExpiresAtUtc,
    bool MustChangePassword,
    UserProfileDto User
);
