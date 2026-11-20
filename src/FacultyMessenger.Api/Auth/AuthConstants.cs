using FacultyMessenger.Api.Domain.Enums;

namespace FacultyMessenger.Api.Auth;

/// <summary>Имена кастомных JWT-claims.</summary>
public static class AppClaims
{
    public const string UserId = "uid";
    public const string GroupId = "gid";
    public const string MustChangePassword = "mcp";
}

/// <summary>Имена политик авторизации (3-уровневая RBAC).</summary>
public static class Policies
{
    public const string AdminOnly = nameof(AdminOnly);
    public const string EmployeeOrAdmin = nameof(EmployeeOrAdmin);
}

public static class Roles
{
    public const string Admin = nameof(UserRole.Admin);
    public const string Employee = nameof(UserRole.Employee);
    public const string Student = nameof(UserRole.Student);
}
