using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using FacultyMessenger.Api.Configuration;
using Microsoft.Extensions.Options;

namespace FacultyMessenger.Api.Services.Storage;

public class FileValidationException : Exception
{
    public FileValidationException(string message) : base(message) { }
}

public record StoredFileResult(
    string StoredFilePath,
    string OriginalFileName,
    string ContentType,
    long FileSize
);

public record StoredFileReadResult(
    Stream Stream,
    string ContentType,
    long? ContentLength,
    bool IsSeekable,
    IDisposable? ResourceToDispose
);

public interface IFileStorageService
{
    Task<StoredFileResult> SaveFileAsync(IFormFile file, bool isAudio, CancellationToken ct = default);
    Task<StoredFileReadResult> OpenReadAsync(string storedFileKey, string fallbackContentType, CancellationToken ct = default);
    Task DeleteFileAsync(string storedFileKey, CancellationToken ct = default);
}

/// <summary>Хранилище файлов и голосовых сообщений в Garage (S3-совместимый API).</summary>
public class GarageS3FileStorageService : IFileStorageService, IDisposable
{
    private const long InMemorySeekThresholdBytes = 10L * 1024 * 1024; // до 10 МБ буферизуем для Range-перемотки аудио/PDF

    private readonly FileStorageOptions _options;
    private readonly AmazonS3Client _s3;
    private readonly ILogger<GarageS3FileStorageService> _logger;

    public GarageS3FileStorageService(IOptions<FileStorageOptions> options, ILogger<GarageS3FileStorageService> logger)
    {
        _options = options.Value;
        _logger = logger;

        var credentials = new BasicAWSCredentials(_options.AccessKey, _options.SecretKey);
        var config = new AmazonS3Config
        {
            ServiceURL = _options.Endpoint,
            ForcePathStyle = _options.ForcePathStyle,
            AuthenticationRegion = _options.Region,
            UseHttp = _options.Endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase),
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
        };
        _s3 = new AmazonS3Client(credentials, config);
    }

    public async Task<StoredFileResult> SaveFileAsync(IFormFile file, bool isAudio, CancellationToken ct = default)
    {
        if (file is null || file.Length <= 0)
            throw new FileValidationException("Файл пуст или не передан.");

        var maxBytes = isAudio ? _options.MaxAudioSizeBytes : _options.MaxFileSizeBytes;
        if (file.Length > maxBytes)
        {
            var maxMb = maxBytes / (1024d * 1024d);
            throw new FileValidationException($"Размер файла превышает допустимый лимит ({maxMb:0.#} МБ).");
        }

        var originalFileName = Path.GetFileName(file.FileName);
        if (string.IsNullOrWhiteSpace(originalFileName))
            originalFileName = isAudio ? "voice.ogg" : "file.bin";

        var ext = Path.GetExtension(originalFileName).ToLowerInvariant();
        var allowed = isAudio ? _options.AllowedAudioExtensions : _options.AllowedFileExtensions;

        if (string.IsNullOrEmpty(ext) || !allowed.Contains(ext, StringComparer.OrdinalIgnoreCase))
        {
            throw new FileValidationException(
                $"Расширение '{ext}' не разрешено для {(isAudio ? "голосовых сообщений" : "файлов")}. " +
                $"Допустимые форматы: {string.Join(", ", allowed)}");
        }

        var now = DateTime.UtcNow;
        var prefix = (isAudio ? _options.AudioPrefix : _options.FilesPrefix).Trim('/');
        var objectKey = $"{prefix}/{now:yyyy}/{now:MM}/{Guid.NewGuid():N}{ext}";

        var contentType = string.IsNullOrWhiteSpace(file.ContentType)
            ? (isAudio ? "audio/ogg" : "application/octet-stream")
            : file.ContentType;

        await using var bufferStream = new MemoryStream((int)Math.Min(file.Length, int.MaxValue));
        await file.CopyToAsync(bufferStream, ct);
        bufferStream.Position = 0;

        var putRequest = new PutObjectRequest
        {
            BucketName = _options.BucketName,
            Key = objectKey,
            InputStream = bufferStream,
            ContentType = contentType,
            UseChunkEncoding = false,
            AutoCloseStream = false
        };
        putRequest.Headers.ContentLength = bufferStream.Length;

        await _s3.PutObjectAsync(putRequest, ct);

        return new StoredFileResult(objectKey, originalFileName, contentType, file.Length);
    }

    public async Task<StoredFileReadResult> OpenReadAsync(string storedFileKey, string fallbackContentType, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(storedFileKey))
            throw new FileNotFoundException("Ключ файла в хранилище не указан.");

        GetObjectResponse response;
        try
        {
            response = await _s3.GetObjectAsync(new GetObjectRequest
            {
                BucketName = _options.BucketName,
                Key = storedFileKey
            }, ct);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new FileNotFoundException("Файл не найден в хранилище Garage.", storedFileKey, ex);
        }

        var contentType = string.IsNullOrWhiteSpace(response.Headers.ContentType)
            ? (string.IsNullOrWhiteSpace(fallbackContentType) ? "application/octet-stream" : fallbackContentType)
            : response.Headers.ContentType;
        var contentLength = response.ContentLength >= 0 ? response.ContentLength : (long?)null;

        if (contentLength.HasValue && contentLength.Value <= InMemorySeekThresholdBytes)
        {
            using (response)
            {
                var mem = new MemoryStream((int)contentLength.Value);
                await response.ResponseStream.CopyToAsync(mem, ct);
                mem.Position = 0;
                return new StoredFileReadResult(mem, contentType, mem.Length, true, null);
            }
        }

        return new StoredFileReadResult(response.ResponseStream, contentType, contentLength, false, response);
    }

    public async Task DeleteFileAsync(string storedFileKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(storedFileKey))
            return;

        try
        {
            await _s3.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = _options.BucketName,
                Key = storedFileKey
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Не удалось удалить объект {Key} из Garage", storedFileKey);
        }
    }

    public void Dispose() => _s3.Dispose();
}
