using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using FacultyMessenger.Api.Contracts.Admin;
using FacultyMessenger.Api.Data;
using FacultyMessenger.Api.Domain.Entities;
using FacultyMessenger.Api.Domain.Enums;
using FacultyMessenger.Api.Services.Auth;
using FacultyMessenger.Api.Services.Email;
using Microsoft.EntityFrameworkCore;

namespace FacultyMessenger.Api.Services.Users;

public interface IUserImportService
{
    Task<AcademicGroup> EnsureGroupWithChatAsync(string groupName, int? course, string? faculty, Guid creatorUserId, CancellationToken ct);
    Chat CreateOfficialGroupChatWithTemplates(AcademicGroup group, Guid creatorUserId);
    Task<ImportUsersResultDto> ImportFromCsvAsync(Stream csvStream, Guid adminUserId, string defaultFaculty, bool sendEmails, CancellationToken ct);
}

public class UserImportService : IUserImportService
{
    private const string DefaultFacultyFallback = "Факультет информационных технологий";
    private static readonly EmailAddressAttribute EmailValidator = new();

    private readonly AppDbContext _db;
    private readonly IEmailService _emailService;
    private readonly ILogger<UserImportService> _logger;

    public UserImportService(AppDbContext db, IEmailService emailService, ILogger<UserImportService> logger)
    {
        _db = db;
        _emailService = emailService;
        _logger = logger;
    }

    public async Task<AcademicGroup> EnsureGroupWithChatAsync(
        string groupName,
        int? course,
        string? faculty,
        Guid creatorUserId,
        CancellationToken ct)
    {
        var normalized = groupName.Trim();
        var lower = normalized.ToLowerInvariant();

        var existing = await _db.AcademicGroups
            .FirstOrDefaultAsync(g => g.Name.ToLower() == lower, ct);

        if (existing is not null)
            return existing;

        var group = CreateGroupWithChat(normalized, course, faculty ?? DefaultFacultyFallback, creatorUserId, out _);
        await _db.SaveChangesAsync(ct);
        return group;
    }

    public Chat CreateOfficialGroupChatWithTemplates(AcademicGroup group, Guid creatorUserId)
    {
        var chat = new Chat
        {
            Id = Guid.NewGuid(),
            Title = $"Группа {group.Name}",
            Type = ChatType.Group,
            OwnerGroupId = group.Id,
            CreatedById = creatorUserId,
            AllowMembersSendMessages = true,
            AllowMembersSendMedia = true,
            AllowMembersPinMessages = true,
            AllowMembersCreatePolls = true
        };

        _db.Chats.Add(chat);
        _db.ChatGroupBindings.Add(new ChatGroupBinding
        {
            ChatId = chat.Id,
            AcademicGroupId = group.Id
        });

        // Шаблоны ролей для чатов учебных групп: «Замстаросты» и «Профорг»
        _db.ChatRoles.AddRange(
            new ChatRole
            {
                Id = Guid.NewGuid(),
                ChatId = chat.Id,
                Name = "Замстаросты",
                BadgeColor = "#2563eb",
                IsTemplate = true,
                Permissions = ChatPermissions.CanSendMessages
                              | ChatPermissions.CanSendMedia
                              | ChatPermissions.CanPinMessages
                              | ChatPermissions.CanDeleteMessages
                              | ChatPermissions.CanCreatePolls
                              | ChatPermissions.CanSendAcknowledgements
                              | ChatPermissions.CanManageRoles
            },
            new ChatRole
            {
                Id = Guid.NewGuid(),
                ChatId = chat.Id,
                Name = "Профорг",
                BadgeColor = "#16a34a",
                IsTemplate = true,
                Permissions = ChatPermissions.CanSendMessages
                              | ChatPermissions.CanSendMedia
                              | ChatPermissions.CanPinMessages
                              | ChatPermissions.CanCreatePolls
                              | ChatPermissions.CanSendAcknowledgements
            }
        );

        return chat;
    }

    public async Task<ImportUsersResultDto> ImportFromCsvAsync(
        Stream csvStream,
        Guid adminUserId,
        string defaultFaculty,
        bool sendEmails,
        CancellationToken ct)
    {
        var errors = new List<string>();
        var createdEntries = new List<(int RowNumber, User User, string TempPassword, string? GroupName)>();
        var groupsCreatedCount = 0;
        var totalRows = 0;
        var skippedCount = 0;

        var facultyFallback = string.IsNullOrWhiteSpace(defaultFaculty)
            ? DefaultFacultyFallback
            : defaultFaculty.Trim();

        var existingEmails = new HashSet<string>(
            await _db.Users.Select(u => u.Email.ToLower()).ToListAsync(ct),
            StringComparer.OrdinalIgnoreCase);

        var groupCache = await _db.AcademicGroups
            .ToDictionaryAsync(g => g.Name, g => g, StringComparer.OrdinalIgnoreCase, ct);

        var groupChatCache = await _db.Chats
            .Where(c => c.OwnerGroupId != null)
            .ToDictionaryAsync(c => c.OwnerGroupId!.Value, c => c.Id, ct);

        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = ";",
            HasHeaderRecord = true,
            MissingFieldFound = null,
            HeaderValidated = null,
            TrimOptions = TrimOptions.Trim,
            PrepareHeaderForMatch = args => args.Header.Trim().ToLowerInvariant()
        };

        using var reader = new StreamReader(csvStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        using var csv = new CsvReader(reader, config);

        if (!await csv.ReadAsync() || !csv.ReadHeader())
        {
            errors.Add("CSV-файл пуст или не содержит строку заголовков.");
            return new ImportUsersResultDto(0, 0, 0, 0, 0, [], errors);
        }

        var pendingLeaders = new List<(AcademicGroup Group, User User)>();

        while (await csv.ReadAsync())
        {
            ct.ThrowIfCancellationRequested();
            totalRows++;
            var rowNum = csv.Context.Parser?.Row ?? (totalRows + 1);

            var fullName = csv.GetField("fullname")?.Trim();
            var emailRaw = csv.GetField("email")?.Trim();
            var groupName = csv.GetField("groupname")?.Trim();
            var roleRaw = csv.GetField("role")?.Trim();

            if (string.IsNullOrWhiteSpace(fullName))
            {
                skippedCount++;
                errors.Add($"Строка {rowNum}: пустое поле FullName.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(emailRaw) || !EmailValidator.IsValid(emailRaw))
            {
                skippedCount++;
                errors.Add($"Строка {rowNum}: некорректный Email '{emailRaw}'.");
                continue;
            }

            var email = emailRaw.ToLowerInvariant();
            if (!existingEmails.Add(email))
            {
                skippedCount++;
                errors.Add($"Строка {rowNum}: пользователь с Email '{email}' уже существует или дублируется в файле.");
                continue;
            }

            // Поддерживаем 3 глобальные роли + удобные алиасы Teacher -> Employee, Leader -> Student + староста группы
            var assignAsGroupLeader = false;
            UserRole role;
            if (string.Equals(roleRaw, "Teacher", StringComparison.OrdinalIgnoreCase))
            {
                role = UserRole.Employee;
            }
            else if (string.Equals(roleRaw, "Leader", StringComparison.OrdinalIgnoreCase))
            {
                role = UserRole.Student;
                assignAsGroupLeader = true;
            }
            else if (!Enum.TryParse(roleRaw, ignoreCase: true, out role) || !Enum.IsDefined(role))
            {
                skippedCount++;
                errors.Add($"Строка {rowNum}: неизвестная роль '{roleRaw}'. Допустимы: Student, Employee, Admin (или алиасы Leader, Teacher).");
                continue;
            }

            AcademicGroup? group = null;
            if (!string.IsNullOrWhiteSpace(groupName))
            {
                if (!groupCache.TryGetValue(groupName, out group))
                {
                    int? course = null;
                    if (csv.TryGetField<string>("course", out var courseRaw) && int.TryParse(courseRaw, out var parsedCourse) && parsedCourse is >= 1 and <= 6)
                        course = parsedCourse;

                    csv.TryGetField<string>("faculty", out var rowFaculty);
                    var resolvedFaculty = string.IsNullOrWhiteSpace(rowFaculty) ? facultyFallback : rowFaculty.Trim();

                    group = CreateGroupWithChat(groupName, course, resolvedFaculty, adminUserId, out var createdChat);
                    groupCache[group.Name] = group;
                    groupChatCache[group.Id] = createdChat.Id;
                    groupsCreatedCount++;
                }
            }

            var tempPassword = PasswordGenerator.Generate();
            var user = new User
            {
                Id = Guid.NewGuid(),
                FullName = fullName,
                Email = email,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(tempPassword, workFactor: 10),
                Role = role,
                AcademicGroupId = role == UserRole.Student ? group?.Id : null,
                MustChangePassword = true
            };

            _db.Users.Add(user);
            if (assignAsGroupLeader && group is not null)
                pendingLeaders.Add((group, user));

            createdEntries.Add((rowNum, user, tempPassword, role == UserRole.Student ? group?.Name : null));
        }

        if (createdEntries.Count > 0 || groupsCreatedCount > 0)
        {
            await _db.SaveChangesAsync(ct);

            if (pendingLeaders.Count > 0)
            {
                foreach (var (grp, leader) in pendingLeaders)
                    grp.LeaderId = leader.Id;
                await _db.SaveChangesAsync(ct);
            }
        }

        var resultItems = new List<ImportedUserItemDto>(createdEntries.Count);
        var emailsSentCount = 0;

        foreach (var entry in createdEntries)
        {
            var emailSent = false;
            string? emailError = null;

            if (sendEmails)
            {
                try
                {
                    await _emailService.SendTemporaryPasswordAsync(entry.User.Email, entry.User.FullName, entry.TempPassword, ct);
                    emailSent = true;
                    emailsSentCount++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    emailError = ex.Message;
                    _logger.LogWarning(ex, "Не удалось отправить письмо с паролем пользователю {Email}", entry.User.Email);
                }
            }

            resultItems.Add(new ImportedUserItemDto(
                entry.RowNumber,
                entry.User.Id,
                entry.User.FullName,
                entry.User.Email,
                entry.User.Role,
                entry.GroupName,
                entry.TempPassword,
                emailSent,
                emailError
            ));
        }

        return new ImportUsersResultDto(
            totalRows,
            createdEntries.Count,
            skippedCount,
            groupsCreatedCount,
            emailsSentCount,
            resultItems,
            errors
        );
    }

    private AcademicGroup CreateGroupWithChat(
        string groupName,
        int? course,
        string faculty,
        Guid creatorUserId,
        out Chat createdChat)
    {
        var group = new AcademicGroup
        {
            Id = Guid.NewGuid(),
            Name = groupName,
            Course = course is >= 1 and <= 6 ? course.Value : InferCourse(groupName),
            Faculty = faculty
        };

        _db.AcademicGroups.Add(group);
        createdChat = CreateOfficialGroupChatWithTemplates(group, creatorUserId);
        return group;
    }

    private static int InferCourse(string groupName)
    {
        foreach (var ch in groupName)
        {
            if (char.IsDigit(ch))
            {
                var digit = ch - '0';
                return digit is >= 1 and <= 6 ? digit : 1;
            }
        }

        return 1;
    }
}
