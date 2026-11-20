using FacultyMessenger.Api.Auth;
using FacultyMessenger.Api.Contracts.Auth;
using FacultyMessenger.Api.Data;
using FacultyMessenger.Api.Domain.Entities;
using FacultyMessenger.Api.Domain.Enums;
using FacultyMessenger.Api.Services.Auth;
using FacultyMessenger.Api.Services.Common;
using FacultyMessenger.Api.Services.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FacultyMessenger.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IJwtTokenService _jwtService;
    private readonly IUserImportService _importService;
    private readonly IEiosClient _eiosClient;

    public AuthController(
        AppDbContext db,
        IJwtTokenService jwtService,
        IUserImportService importService,
        IEiosClient eiosClient)
    {
        _db = db;
        _jwtService = jwtService;
        _importService = importService;
        _eiosClient = eiosClient;
    }

    /// <summary>Вход по email и паролю.</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [ProducesResponseType<AuthResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<AuthResponse>> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var user = await _db.Users
            .Include(u => u.AcademicGroup)
            .FirstOrDefaultAsync(u => u.Email == email, ct);

        if (user is null || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
        {
            return Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Неверные учётные данные",
                detail: "Неверный email или пароль.");
        }

        var (token, expiresAt) = _jwtService.GenerateToken(user);
        return Ok(new AuthResponse(
            token,
            expiresAt,
            user.MustChangePassword,
            UserProfileDto.FromEntity(user)
        ));
    }

    /// <summary>Вход и авторегистрация через ЭИОС КГУ (my.kursksu.ru) с начальной ролью Student.</summary>
    [HttpPost("eios-login")]
    [AllowAnonymous]
    [ProducesResponseType<AuthResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<AuthResponse>> EiosLogin([FromBody] EiosLoginRequest request, CancellationToken ct)
    {
        var username = request.Username.Trim();
        EiosProfileResult profile;

        if (request.UseDemoFallback)
        {
            var lower = username.ToLowerInvariant();
            var email = lower.Contains('@') ? lower : $"{lower}@demo.kursksu.ru";
            var fullName = string.IsNullOrWhiteSpace(request.FullName) ? username : request.FullName.Trim();
            profile = new EiosProfileResult($"demo:{lower}", fullName, email, request.GroupName, null);
        }
        else
        {
            try
            {
                profile = await _eiosClient.AuthenticateAsync(username, request.Password, ct);
            }
            catch (DomainUnauthorizedException ex)
            {
                return Problem(
                    statusCode: StatusCodes.Status401Unauthorized,
                    title: "Ошибка авторизации ЭИОС КГУ",
                    detail: ex.Message);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Офлайн-фолбэк: если my.kursksu.ru недоступен, проверяем кэшированный хэш уже зарегистрированного ЭИОС-пользователя
                var fallbackLower = username.ToLowerInvariant();
                var cachedUser = await _db.Users
                    .Include(u => u.AcademicGroup)
                    .FirstOrDefaultAsync(u =>
                        u.IsEiosVerified
                        && (u.Email == fallbackLower
                            || u.Email == $"{fallbackLower}@eios.kursksu.ru"
                            || u.EiosExternalId == $"ksu:{fallbackLower}"), ct);

                if (cachedUser is null || !BCrypt.Net.BCrypt.Verify(request.Password, cachedUser.PasswordHash))
                {
                    return Problem(
                        statusCode: StatusCodes.Status503ServiceUnavailable,
                        title: "Сервер ЭИОС КГУ недоступен",
                        detail: $"Не удалось связаться с my.kursksu.ru: {ex.Message}");
                }

                var (cachedToken, cachedExp) = _jwtService.GenerateToken(cachedUser);
                return Ok(new AuthResponse(
                    cachedToken,
                    cachedExp,
                    cachedUser.MustChangePassword,
                    UserProfileDto.FromEntity(cachedUser)
                ));
            }
        }

        var user = await _db.Users
            .Include(u => u.AcademicGroup)
            .FirstOrDefaultAsync(u => u.EiosExternalId == profile.ExternalId, ct)
            ?? await _db.Users
                .Include(u => u.AcademicGroup)
                .FirstOrDefaultAsync(u => u.Email == profile.Email, ct);

        var isNewUser = user is null;
        if (user is null)
        {
            user = new User
            {
                Id = Guid.NewGuid(),
                FullName = profile.FullName,
                Email = profile.Email,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password, workFactor: 10),
                // При регистрации через ЭИОС всегда присваивается начальная роль Student
                Role = UserRole.Student,
                MustChangePassword = false,
                IsEiosVerified = true,
                EiosExternalId = profile.ExternalId
            };
            _db.Users.Add(user);
            await _db.SaveChangesAsync(ct);
        }
        else
        {
            // При повторном входе роль не перезаписываем, чтобы сохранить повышения (Leader, Teacher и т.д.)
            user.IsEiosVerified = true;
            user.EiosExternalId = profile.ExternalId;
            if (!string.IsNullOrWhiteSpace(profile.FullName))
                user.FullName = profile.FullName;
            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password, workFactor: 10);
        }

        var resolvedGroupName = !string.IsNullOrWhiteSpace(profile.GroupName)
            ? profile.GroupName
            : (string.IsNullOrWhiteSpace(request.GroupName) ? null : request.GroupName.Trim());

        string? groupName = user.AcademicGroup?.Name;
        if (resolvedGroupName is not null)
        {
            var creatorId = await _db.Users
                .Where(u => u.Role == UserRole.Admin)
                .Select(u => (Guid?)u.Id)
                .FirstOrDefaultAsync(ct) ?? user.Id;

            var group = await _importService.EnsureGroupWithChatAsync(
                resolvedGroupName,
                null,
                profile.Faculty,
                creatorId,
                ct);

            user.AcademicGroupId = group.Id;
            user.AcademicGroup = group;
            groupName = group.Name;
        }

        if (!isNewUser || resolvedGroupName is not null)
        {
            await _db.SaveChangesAsync(ct);
        }

        var (token, expiresAt) = _jwtService.GenerateToken(user);
        return Ok(new AuthResponse(
            token,
            expiresAt,
            user.MustChangePassword,
            UserProfileDto.FromEntity(user, groupName)
        ));
    }

    /// <summary>Смена временного пароля при первом входе.</summary>
    [HttpPost("change-initial-password")]
    [Authorize]
    [ProducesResponseType<AuthResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AuthResponse>> ChangeInitialPassword(
        [FromBody] ChangeInitialPasswordRequest request,
        CancellationToken ct)
    {
        var userId = User.GetUserId();
        var user = await _db.Users
            .Include(u => u.AcademicGroup)
            .FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user is null)
            return Unauthorized();

        if (!BCrypt.Net.BCrypt.Verify(request.CurrentPassword, user.PasswordHash))
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Неверный текущий пароль",
                detail: "Введённый текущий пароль не совпадает.");
        }

        if (request.NewPassword == request.CurrentPassword)
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Пароль не изменён",
                detail: "Новый пароль должен отличаться от временного пароля.");
        }

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword, workFactor: 12);
        user.MustChangePassword = false;
        await _db.SaveChangesAsync(ct);

        var (token, expiresAt) = _jwtService.GenerateToken(user);
        return Ok(new AuthResponse(
            token,
            expiresAt,
            user.MustChangePassword,
            UserProfileDto.FromEntity(user)
        ));
    }

    /// <summary>Получение профиля текущего пользователя.</summary>
    [HttpGet("me")]
    [Authorize]
    [ProducesResponseType<UserProfileDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<UserProfileDto>> GetMe(CancellationToken ct)
    {
        var userId = User.GetUserId();
        var user = await _db.Users
            .AsNoTracking()
            .Include(u => u.AcademicGroup)
            .FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user is null)
            return Unauthorized();

        return Ok(UserProfileDto.FromEntity(user));
    }
}
