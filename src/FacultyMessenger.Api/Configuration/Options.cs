using System.ComponentModel.DataAnnotations;

namespace FacultyMessenger.Api.Configuration;

public class JwtOptions
{
    public const string SectionName = "Jwt";

    [Required] public string Issuer { get; set; } = null!;
    [Required] public string Audience { get; set; } = null!;

    /// <summary>Симметричный ключ подписи HMAC-SHA256 (не короче 32 символов).</summary>
    [Required, MinLength(32)] public string SigningKey { get; set; } = null!;

    [Range(1, 1440)] public int AccessTokenLifetimeMinutes { get; set; } = 120;
}

public class SmtpOptions
{
    public const string SectionName = "Smtp";

    [Required] public string Host { get; set; } = null!;
    [Range(1, 65535)] public int Port { get; set; } = 587;
    public bool UseStartTls { get; set; } = true;
    public string? UserName { get; set; }
    public string? Password { get; set; }
    [Required, EmailAddress] public string FromAddress { get; set; } = null!;
    public string FromName { get; set; } = "Деканат факультета";
}

/// <summary>Настройки S3-совместимого объектного хранилища Garage.</summary>
public class FileStorageOptions
{
    public const string SectionName = "FileStorage";

    [Required] public string Endpoint { get; set; } = "http://localhost:3900";
    [Required] public string Region { get; set; } = "garage";
    [Required] public string BucketName { get; set; } = "faculty-messenger";
    [Required] public string AccessKey { get; set; } = null!;
    [Required] public string SecretKey { get; set; } = null!;
    public bool ForcePathStyle { get; set; } = true;

    public string FilesPrefix { get; set; } = "files";
    public string AudioPrefix { get; set; } = "audio";

    [Range(1, long.MaxValue)] public long MaxFileSizeBytes { get; set; } = 50L * 1024 * 1024;
    [Range(1, long.MaxValue)] public long MaxAudioSizeBytes { get; set; } = 10L * 1024 * 1024;

    public string[] AllowedFileExtensions { get; set; } =
        [".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".zip", ".rar", ".7z", ".png", ".jpg", ".jpeg"];

    public string[] AllowedAudioExtensions { get; set; } = [".ogg", ".oga", ".opus", ".mp3", ".m4a", ".webm", ".wav"];
}

public class SeedAdminOptions
{
    public const string SectionName = "SeedAdmin";

    public string Email { get; set; } = "admin@faculty.local";
    public string FullName { get; set; } = "Администратор деканата";

    /// <summary>Начальный пароль. При первом входе потребуется сменить.</summary>
    public string Password { get; set; } = "ChangeMe!2026";
}
