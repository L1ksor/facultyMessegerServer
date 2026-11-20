using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FacultyMessenger.Api.Services.Common;

namespace FacultyMessenger.Api.Services.Auth;

public record EiosProfileResult(
    string ExternalId,
    string FullName,
    string Email,
    string? GroupName,
    string? Faculty
);

public interface IEiosClient
{
    Task<EiosProfileResult> AuthenticateAsync(string username, string password, CancellationToken ct = default);
}

public class KurskSuEiosClient : IEiosClient
{
    private const string BaseApiUrl = "https://my.kursksu.ru/api";
    private static readonly EmailAddressAttribute EmailValidator = new();

    private readonly HttpClient _http;
    private readonly ILogger<KurskSuEiosClient> _logger;

    public KurskSuEiosClient(HttpClient http, ILogger<KurskSuEiosClient> logger)
    {
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(15);
        _logger = logger;
    }

    public async Task<EiosProfileResult> AuthenticateAsync(string username, string password, CancellationToken ct = default)
    {
        var loginPayload = new { username = username.Trim(), password };
        using var loginReq = new HttpRequestMessage(HttpMethod.Post, $"{BaseApiUrl}/auth/login")
        {
            Content = JsonContent.Create(loginPayload)
        };
        loginReq.Headers.TryAddWithoutValidation("Origin", "https://my.kursksu.ru");
        loginReq.Headers.TryAddWithoutValidation("Referer", "https://my.kursksu.ru/");

        using var loginResp = await _http.SendAsync(loginReq, ct);
        if (loginResp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest or HttpStatusCode.Forbidden)
        {
            throw new DomainUnauthorizedException("Неверный логин или пароль ЭИОС КГУ (my.kursksu.ru).");
        }

        if (!loginResp.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Сервер ЭИОС КГУ вернул статус {(int)loginResp.StatusCode}.");
        }

        await using var loginStream = await loginResp.Content.ReadAsStreamAsync(ct);
        using var loginDoc = await JsonDocument.ParseAsync(loginStream, cancellationToken: ct);
        if (!loginDoc.RootElement.TryGetProperty("token", out var tokenProp) || string.IsNullOrWhiteSpace(tokenProp.GetString()))
        {
            throw new DomainUnauthorizedException("Неверный логин или пароль ЭИОС КГУ (my.kursksu.ru).");
        }

        var token = tokenProp.GetString()!;
        var (jwtUserId, groupId) = ParseTokenClaims(token);

        // Запрашиваем профиль /api/people
        string? externalId = jwtUserId;
        string? fullName = null;
        string? email = null;

        using var peopleReq = new HttpRequestMessage(HttpMethod.Get, $"{BaseApiUrl}/people");
        peopleReq.Headers.TryAddWithoutValidation("Authorization", $"JWT {token}");
        using var peopleResp = await _http.SendAsync(peopleReq, ct);
        if (peopleResp.IsSuccessStatusCode)
        {
            await using var peopleStream = await peopleResp.Content.ReadAsStreamAsync(ct);
            using var peopleDoc = await JsonDocument.ParseAsync(peopleStream, cancellationToken: ct);
            var root = peopleDoc.RootElement;

            if (root.TryGetProperty("id", out var idProp))
                externalId = idProp.ToString();

            if (root.TryGetProperty("fullName", out var fnProp) && fnProp.ValueKind == JsonValueKind.String)
                fullName = fnProp.GetString();

            if (root.TryGetProperty("email", out var emProp) && emProp.ValueKind == JsonValueKind.String)
                email = emProp.GetString();
        }

        // Если в токене есть привязка к группе — пробуем получить номер группы и факультет
        string? groupName = null;
        string? faculty = null;
        if (!string.IsNullOrWhiteSpace(groupId))
        {
            try
            {
                using var grpReq = new HttpRequestMessage(HttpMethod.Get, $"{BaseApiUrl}/groups/{groupId}");
                grpReq.Headers.TryAddWithoutValidation("Authorization", $"JWT {token}");
                using var grpResp = await _http.SendAsync(grpReq, ct);
                if (grpResp.IsSuccessStatusCode)
                {
                    await using var grpStream = await grpResp.Content.ReadAsStreamAsync(ct);
                    using var grpDoc = await JsonDocument.ParseAsync(grpStream, cancellationToken: ct);
                    var gRoot = grpDoc.RootElement;
                    if (gRoot.TryGetProperty("number", out var numProp))
                        groupName = numProp.ToString();
                    if (gRoot.TryGetProperty("department", out var depProp)
                        && depProp.TryGetProperty("faculty", out var facProp))
                    {
                        if (facProp.TryGetProperty("abbreviation", out var abbr) && abbr.ValueKind == JsonValueKind.String)
                            faculty = abbr.GetString();
                        else if (facProp.TryGetProperty("name", out var fname) && fname.ValueKind == JsonValueKind.String)
                            faculty = fname.GetString();
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Не удалось получить информацию о группе {GroupId} из ЭИОС КГУ", groupId);
            }
        }

        var cleanUsername = username.Trim();
        var resolvedFullName = string.IsNullOrWhiteSpace(fullName) ? cleanUsername : fullName.Trim();
        var resolvedEmail = !string.IsNullOrWhiteSpace(email) && EmailValidator.IsValid(email.Trim())
            ? email.Trim().ToLowerInvariant()
            : (EmailValidator.IsValid(cleanUsername) ? cleanUsername.ToLowerInvariant() : $"{SanitizeLocalPart(cleanUsername)}@eios.kursksu.ru");

        var resolvedExternalId = string.IsNullOrWhiteSpace(externalId)
            ? $"ksu:{cleanUsername.ToLowerInvariant()}"
            : $"ksu:{externalId}";

        return new EiosProfileResult(resolvedExternalId, resolvedFullName, resolvedEmail, groupName, faculty);
    }

    private static (string? UserId, string? GroupId) ParseTokenClaims(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2) return (null, null);

            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            switch (payload.Length % 4)
            {
                case 2: payload += "=="; break;
                case 3: payload += "="; break;
            }

            var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? userId = root.TryGetProperty("id", out var idEl) ? idEl.ToString() : null;
            string? groupId = null;

            if (root.TryGetProperty("access", out var accessEl))
            {
                JsonElement accessObj = default;
                var hasAccess = false;
                if (accessEl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(accessEl.GetString()))
                {
                    using var accessDoc = JsonDocument.Parse(accessEl.GetString()!);
                    accessObj = accessDoc.RootElement.Clone();
                    hasAccess = true;
                }
                else if (accessEl.ValueKind == JsonValueKind.Object)
                {
                    accessObj = accessEl;
                    hasAccess = true;
                }

                if (hasAccess
                    && accessObj.TryGetProperty("group", out var grpArr)
                    && grpArr.ValueKind == JsonValueKind.Array
                    && grpArr.GetArrayLength() > 0
                    && grpArr[0].TryGetProperty("id", out var gIdEl))
                {
                    groupId = gIdEl.ToString();
                }
            }

            return (userId, groupId);
        }
        catch
        {
            return (null, null);
        }
    }

    private static string SanitizeLocalPart(string username)
    {
        var sb = new StringBuilder(username.Length);
        foreach (var ch in username.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-')
                sb.Append(ch);
        }
        return sb.Length > 0 ? sb.ToString() : "student";
    }
}
