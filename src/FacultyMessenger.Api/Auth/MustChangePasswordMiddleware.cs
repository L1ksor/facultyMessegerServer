using FacultyMessenger.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FacultyMessenger.Api.Auth;

/// <summary>
/// Блокирует любые запросы аутентифицированного пользователя с флагом MustChangePassword,
/// кроме смены временного пароля. Флаг проверяется по БД, поэтому после смены пароля
/// старый токен не даёт «обойти» блокировку, а новый токен сразу работает.
/// </summary>
public class MustChangePasswordMiddleware
{
    private static readonly PathString[] AllowedPaths =
    [
        new("/api/auth/change-initial-password"),
        new("/api/auth/login"),
        new("/api/auth/eios-login")
    ];

    private readonly RequestDelegate _next;

    public MustChangePasswordMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, AppDbContext db)
    {
        var user = context.User;
        if (user.Identity?.IsAuthenticated == true
            && user.MustChangePassword()
            && !AllowedPaths.Any(p => context.Request.Path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase)))
        {
            var userId = user.GetUserId();
            var stillMustChange = await db.Users
                .Where(u => u.Id == userId)
                .Select(u => (bool?)u.MustChangePassword)
                .FirstOrDefaultAsync(context.RequestAborted);

            if (stillMustChange != false)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new ProblemDetails
                {
                    Status = StatusCodes.Status403Forbidden,
                    Title = "Требуется смена временного пароля",
                    Detail = "Смените пароль через POST /api/auth/change-initial-password и получите новый токен.",
                    Type = "password_change_required"
                }, context.RequestAborted);
                return;
            }
        }

        await _next(context);
    }
}
