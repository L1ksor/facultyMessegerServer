using System.Security.Claims;

namespace FacultyMessenger.Api.Auth;

public static class ClaimsPrincipalExtensions
{
    public static Guid GetUserId(this ClaimsPrincipal principal)
    {
        var raw = principal.FindFirstValue(AppClaims.UserId)
                  ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(raw, out var id)
            ? id
            : throw new UnauthorizedAccessException("В токене отсутствует идентификатор пользователя.");
    }

    public static Guid? GetGroupId(this ClaimsPrincipal principal)
        => Guid.TryParse(principal.FindFirstValue(AppClaims.GroupId), out var id) ? id : null;

    public static bool MustChangePassword(this ClaimsPrincipal principal)
        => string.Equals(principal.FindFirstValue(AppClaims.MustChangePassword), "true", StringComparison.OrdinalIgnoreCase);
}
