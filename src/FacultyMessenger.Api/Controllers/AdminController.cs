using FacultyMessenger.Api.Auth;
using FacultyMessenger.Api.Contracts.Admin;
using FacultyMessenger.Api.Contracts.Auth;
using FacultyMessenger.Api.Data;
using FacultyMessenger.Api.Domain.Entities;
using FacultyMessenger.Api.Domain.Enums;
using FacultyMessenger.Api.Services.Auth;
using FacultyMessenger.Api.Services.Email;
using FacultyMessenger.Api.Services.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FacultyMessenger.Api.Controllers;

[ApiController]
[Route("api/admin")]
[Authorize(Policy = Policies.AdminOnly)]
public class AdminController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IUserImportService _importService;
    private readonly IEmailService _emailService;
    private readonly ILogger<AdminController> _logger;

    public AdminController(
        AppDbContext db,
        IUserImportService importService,
        IEmailService emailService,
        ILogger<AdminController> logger)
    {
        _db = db;
        _importService = importService;
        _emailService = emailService;
        _logger = logger;
    }

    /// <summary>Пакетный импорт пользователей из CSV (колонки: FullName;Email;GroupName;Role).</summary>
    [HttpPost("import-users")]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<ImportUsersResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ImportUsersResultDto>> ImportUsers(
        [FromForm] ImportUsersFormRequest request,
        CancellationToken ct)
    {
        if (request.File is null || request.File.Length == 0)
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Пустой файл",
                detail: "Выберите непустой CSV-файл для импорта.");
        }

        var ext = Path.GetExtension(request.File.FileName);
        var isCsvExt = string.Equals(ext, ".csv", StringComparison.OrdinalIgnoreCase);
        var isCsvMime = string.Equals(request.File.ContentType, "text/csv", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(request.File.ContentType, "application/vnd.ms-excel", StringComparison.OrdinalIgnoreCase);

        if (!isCsvExt && !isCsvMime)
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Неверный формат файла",
                detail: "Поддерживаются только CSV-файлы (.csv).");
        }

        var adminId = User.GetUserId();
        await using var stream = request.File.OpenReadStream();
        var result = await _importService.ImportFromCsvAsync(stream, adminId, request.DefaultFaculty, request.SendEmails, ct);
        return Ok(result);
    }

    /// <summary>Ручная регистрация пользователя сотрудником деканата.</summary>
    [HttpPost("users")]
    [ProducesResponseType<CreateUserResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CreateUserResponse>> CreateUser(
        [FromBody] CreateUserRequest request,
        CancellationToken ct)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        if (await _db.Users.AnyAsync(u => u.Email == email, ct))
        {
            return Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Пользователь уже существует",
                detail: $"Пользователь с email '{email}' уже зарегистрирован.");
        }

        AcademicGroup? group = null;
        if (request.Role == UserRole.Student)
        {
            if (request.AcademicGroupId.HasValue)
            {
                group = await _db.AcademicGroups.FirstOrDefaultAsync(g => g.Id == request.AcademicGroupId.Value, ct);
                if (group is null)
                {
                    return Problem(
                        statusCode: StatusCodes.Status400BadRequest,
                        title: "Группа не найдена",
                        detail: $"Учебная группа {request.AcademicGroupId} не найдена.");
                }
            }
            else if (!string.IsNullOrWhiteSpace(request.GroupName))
            {
                group = await _importService.EnsureGroupWithChatAsync(
                    request.GroupName,
                    request.Course,
                    request.Faculty,
                    User.GetUserId(),
                    ct);
            }
        }

        var tempPassword = string.IsNullOrWhiteSpace(request.TemporaryPassword)
            ? PasswordGenerator.Generate()
            : request.TemporaryPassword;

        var user = new User
        {
            Id = Guid.NewGuid(),
            FullName = request.FullName.Trim(),
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(tempPassword, workFactor: 12),
            Role = request.Role,
            AcademicGroupId = group?.Id,
            AcademicGroup = group,
            MustChangePassword = true
        };

        _db.Users.Add(user);
        await _db.SaveChangesAsync(ct);

        var emailSent = false;
        string? emailError = null;
        if (request.SendEmail)
        {
            try
            {
                await _emailService.SendTemporaryPasswordAsync(user.Email, user.FullName, tempPassword, ct);
                emailSent = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                emailError = ex.Message;
                _logger.LogWarning(ex, "Ошибка отправки временного пароля на {Email}", user.Email);
            }
        }

        var response = new CreateUserResponse(
            UserProfileDto.FromEntity(user),
            tempPassword,
            emailSent,
            emailError
        );

        return CreatedAtAction(nameof(GetUsers), new { search = user.Email }, response);
    }

    /// <summary>Назначение глобальной роли (Student / Employee / Admin) и/или учебной группы пользователю.</summary>
    [HttpPut("users/{userId:guid}/role")]
    [ProducesResponseType<UserProfileDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<UserProfileDto>> AssignRole(
        Guid userId,
        [FromBody] AssignRoleRequest request,
        CancellationToken ct)
    {
        var user = await _db.Users
            .Include(u => u.AcademicGroup)
            .FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user is null)
            return NotFound();

        if (request.AcademicGroupId.HasValue)
        {
            var group = await _db.AcademicGroups.FirstOrDefaultAsync(g => g.Id == request.AcademicGroupId.Value, ct);
            if (group is null)
            {
                return Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Группа не найдена",
                    detail: $"Учебная группа {request.AcademicGroupId} не найдена.");
            }

            user.AcademicGroupId = group.Id;
            user.AcademicGroup = group;
        }

        // Если пользователь перестал быть студентом — снимаем статус старосты с прежней группы
        if (request.Role != UserRole.Student)
        {
            var ledGroups = await _db.AcademicGroups.Where(g => g.LeaderId == user.Id).ToListAsync(ct);
            foreach (var g in ledGroups) g.LeaderId = null;
            user.AcademicGroupId = null;
            user.AcademicGroup = null;
        }

        user.Role = request.Role;
        await _db.SaveChangesAsync(ct);

        return Ok(UserProfileDto.FromEntity(user));
    }

    /// <summary>Список пользователей с фильтрацией и пагинацией.</summary>
    [HttpGet("users")]
    [ProducesResponseType<List<UserProfileDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<List<UserProfileDto>>> GetUsers(
        [FromQuery] UserRole? role = null,
        [FromQuery] Guid? groupId = null,
        [FromQuery] string? search = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var query = _db.Users
            .AsNoTracking()
            .Include(u => u.AcademicGroup)
            .AsQueryable();

        if (role.HasValue)
            query = query.Where(u => u.Role == role.Value);

        if (groupId.HasValue)
            query = query.Where(u => u.AcademicGroupId == groupId.Value);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            query = query.Where(u => u.FullName.ToLower().Contains(term) || u.Email.ToLower().Contains(term));
        }

        var users = await query
            .OrderBy(u => u.FullName)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return Ok(users.Select(u => UserProfileDto.FromEntity(u)).ToList());
    }
}
