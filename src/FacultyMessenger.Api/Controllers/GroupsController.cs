using FacultyMessenger.Api.Auth;
using FacultyMessenger.Api.Contracts.Auth;
using FacultyMessenger.Api.Contracts.Groups;
using FacultyMessenger.Api.Data;
using FacultyMessenger.Api.Domain.Entities;
using FacultyMessenger.Api.Domain.Enums;
using FacultyMessenger.Api.Services.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FacultyMessenger.Api.Controllers;

[ApiController]
[Route("api/groups")]
[Authorize]
public class GroupsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IUserImportService _importService;

    public GroupsController(AppDbContext db, IUserImportService importService)
    {
        _db = db;
        _importService = importService;
    }

    /// <summary>Список учебных групп со старостами и кураторами.</summary>
    [HttpGet]
    [ProducesResponseType<List<AcademicGroupDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<List<AcademicGroupDto>>> GetGroups(
        [FromQuery] int? course = null,
        [FromQuery] string? faculty = null,
        [FromQuery] string? search = null,
        CancellationToken ct = default)
    {
        var query = _db.AcademicGroups.AsNoTracking().AsQueryable();

        if (course.HasValue)
            query = query.Where(g => g.Course == course.Value);

        if (!string.IsNullOrWhiteSpace(faculty))
        {
            var fac = faculty.Trim().ToLowerInvariant();
            query = query.Where(g => g.Faculty.ToLower().Contains(fac));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            query = query.Where(g => g.Name.ToLower().Contains(term));
        }

        var items = await query
            .OrderBy(g => g.Course)
            .ThenBy(g => g.Name)
            .Select(g => new AcademicGroupDto(
                g.Id,
                g.Name,
                g.Course,
                g.Faculty,
                g.Students.Count,
                _db.Chats
                    .Where(c => c.OwnerGroupId == g.Id)
                    .Select(c => (Guid?)c.Id)
                    .FirstOrDefault(),
                g.Leader != null ? new GroupPersonDto(g.Leader.Id, g.Leader.FullName, g.Leader.Email) : null,
                g.Curator != null ? new GroupPersonDto(g.Curator.Id, g.Curator.FullName, g.Curator.Email) : null
            ))
            .ToListAsync(ct);

        return Ok(items);
    }

    /// <summary>Карточка учебной группы: ФИО старосты и куратора, ссылка на официальный чат.</summary>
    [HttpGet("{groupId:guid}")]
    [ProducesResponseType<AcademicGroupDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AcademicGroupDto>> GetGroup(Guid groupId, CancellationToken ct)
    {
        var item = await LoadGroupDtoAsync(groupId, ct);
        return item is null ? NotFound() : Ok(item);
    }

    /// <summary>Список студентов учебной группы.</summary>
    [HttpGet("{groupId:guid}/students")]
    [ProducesResponseType<List<UserProfileDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<List<UserProfileDto>>> GetGroupStudents(Guid groupId, CancellationToken ct)
    {
        if (!await _db.AcademicGroups.AnyAsync(g => g.Id == groupId, ct))
            return NotFound();

        var students = await _db.Users
            .AsNoTracking()
            .Include(u => u.AcademicGroup)
            .Where(u => u.AcademicGroupId == groupId)
            .OrderBy(u => u.FullName)
            .ToListAsync(ct);

        return Ok(students.Select(s => UserProfileDto.FromEntity(s)).ToList());
    }

    /// <summary>Создание учебной группы, её официального чата и шаблонов ролей «Замстаросты» и «Профорг».</summary>
    [HttpPost]
    [Authorize(Policy = Policies.AdminOnly)]
    [ProducesResponseType<AcademicGroupDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AcademicGroupDto>> CreateGroup(
        [FromBody] CreateAcademicGroupRequest request,
        CancellationToken ct)
    {
        var name = request.Name.Trim();
        var lower = name.ToLowerInvariant();
        if (await _db.AcademicGroups.AnyAsync(g => g.Name.ToLower() == lower, ct))
        {
            return Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Группа уже существует",
                detail: $"Учебная группа '{name}' уже зарегистрирована.");
        }

        if (request.CuratorId.HasValue)
        {
            var curator = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == request.CuratorId.Value, ct);
            if (curator is null || curator.Role != UserRole.Employee)
            {
                return Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Некорректный куратор",
                    detail: "Куратором группы может быть назначен только преподаватель/сотрудник (Employee).");
            }
        }

        var group = new AcademicGroup
        {
            Id = Guid.NewGuid(),
            Name = name,
            Course = request.Course,
            Faculty = request.Faculty.Trim(),
            CuratorId = request.CuratorId
        };

        _db.AcademicGroups.Add(group);
        _importService.CreateOfficialGroupChatWithTemplates(group, User.GetUserId());
        await _db.SaveChangesAsync(ct);

        var dto = await LoadGroupDtoAsync(group.Id, ct);
        return CreatedAtAction(nameof(GetGroup), new { groupId = group.Id }, dto);
    }

    /// <summary>Редактирование учебной группы (администратор).</summary>
    [HttpPut("{groupId:guid}")]
    [Authorize(Policy = Policies.AdminOnly)]
    [ProducesResponseType<AcademicGroupDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AcademicGroupDto>> UpdateGroup(
        Guid groupId,
        [FromBody] UpdateAcademicGroupRequest request,
        CancellationToken ct)
    {
        var group = await _db.AcademicGroups.FirstOrDefaultAsync(g => g.Id == groupId, ct);
        if (group is null)
            return NotFound();

        var newName = request.Name.Trim();
        var lower = newName.ToLowerInvariant();
        if (!string.Equals(group.Name, newName, StringComparison.OrdinalIgnoreCase) &&
            await _db.AcademicGroups.AnyAsync(g => g.Name.ToLower() == lower && g.Id != groupId, ct))
        {
            return Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Конфликт имени группы",
                detail: $"Группа '{newName}' уже существует.");
        }

        if (request.LeaderId.HasValue)
        {
            var leader = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == request.LeaderId.Value, ct);
            if (leader is null || leader.Role != UserRole.Student || leader.AcademicGroupId != groupId)
            {
                return Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Некорректный староста",
                    detail: "Старостой можно назначить только студента этой учебной группы.");
            }
            group.LeaderId = leader.Id;
        }

        if (request.CuratorId.HasValue)
        {
            var curator = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == request.CuratorId.Value, ct);
            if (curator is null || curator.Role != UserRole.Employee)
            {
                return Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Некорректный куратор",
                    detail: "Куратором группы может быть назначен только преподаватель/сотрудник (Employee).");
            }
            group.CuratorId = curator.Id;
        }

        group.Name = newName;
        group.Course = request.Course;
        group.Faculty = request.Faculty.Trim();

        var groupChat = await _db.Chats.FirstOrDefaultAsync(c => c.OwnerGroupId == group.Id, ct);
        if (groupChat is not null)
            groupChat.Title = $"Группа {group.Name}";

        await _db.SaveChangesAsync(ct);
        return Ok(await LoadGroupDtoAsync(group.Id, ct));
    }

    /// <summary>Назначение старосты учебной группы (из числа студентов этой группы).</summary>
    [HttpPut("{groupId:guid}/leader")]
    [Authorize(Policy = Policies.AdminOnly)]
    [ProducesResponseType<AcademicGroupDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AcademicGroupDto>> AssignLeader(
        Guid groupId,
        [FromBody] AssignGroupLeaderRequest request,
        CancellationToken ct)
    {
        var group = await _db.AcademicGroups.FirstOrDefaultAsync(g => g.Id == groupId, ct);
        if (group is null)
            return NotFound();

        var student = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == request.StudentUserId, ct);
        if (student is null || student.Role != UserRole.Student || student.AcademicGroupId != groupId)
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Некорректный староста",
                detail: "Старостой можно назначить только студента (Student), состоящего в этой учебной группе.");
        }

        group.LeaderId = student.Id;
        await _db.SaveChangesAsync(ct);

        return Ok(await LoadGroupDtoAsync(group.Id, ct));
    }

    /// <summary>Назначение или снятие куратора группы (из числа Employee; null — снять куратора).</summary>
    [HttpPut("{groupId:guid}/curator")]
    [Authorize(Policy = Policies.AdminOnly)]
    [ProducesResponseType<AcademicGroupDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AcademicGroupDto>> AssignCurator(
        Guid groupId,
        [FromBody] AssignGroupCuratorRequest request,
        CancellationToken ct)
    {
        var group = await _db.AcademicGroups.FirstOrDefaultAsync(g => g.Id == groupId, ct);
        if (group is null)
            return NotFound();

        if (request.CuratorUserId.HasValue)
        {
            var curator = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == request.CuratorUserId.Value, ct);
            if (curator is null || curator.Role != UserRole.Employee)
            {
                return Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Некорректный куратор",
                    detail: "Куратором группы может быть назначен только преподаватель/сотрудник (Employee).");
            }
            group.CuratorId = curator.Id;
        }
        else
        {
            group.CuratorId = null;
        }

        await _db.SaveChangesAsync(ct);
        return Ok(await LoadGroupDtoAsync(group.Id, ct));
    }

    /// <summary>Удаление учебной группы (администратор).</summary>
    [HttpDelete("{groupId:guid}")]
    [Authorize(Policy = Policies.AdminOnly)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteGroup(Guid groupId, CancellationToken ct)
    {
        var group = await _db.AcademicGroups.FirstOrDefaultAsync(g => g.Id == groupId, ct);
        if (group is null)
            return NotFound();

        var groupChats = await _db.Chats.Where(c => c.OwnerGroupId == group.Id).ToListAsync(ct);
        if (groupChats.Count > 0)
            _db.Chats.RemoveRange(groupChats);

        _db.AcademicGroups.Remove(group);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private async Task<AcademicGroupDto?> LoadGroupDtoAsync(Guid groupId, CancellationToken ct)
    {
        return await _db.AcademicGroups
            .AsNoTracking()
            .Where(g => g.Id == groupId)
            .Select(g => new AcademicGroupDto(
                g.Id,
                g.Name,
                g.Course,
                g.Faculty,
                g.Students.Count,
                _db.Chats
                    .Where(c => c.OwnerGroupId == g.Id)
                    .Select(c => (Guid?)c.Id)
                    .FirstOrDefault(),
                g.Leader != null ? new GroupPersonDto(g.Leader.Id, g.Leader.FullName, g.Leader.Email) : null,
                g.Curator != null ? new GroupPersonDto(g.Curator.Id, g.Curator.FullName, g.Curator.Email) : null
            ))
            .FirstOrDefaultAsync(ct);
    }
}
