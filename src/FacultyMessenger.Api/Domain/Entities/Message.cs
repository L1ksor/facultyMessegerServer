using FacultyMessenger.Api.Domain.Enums;

namespace FacultyMessenger.Api.Domain.Entities;

public class Message
{
    public Guid Id { get; set; }

    public Guid ChatId { get; set; }
    public Chat Chat { get; set; } = null!;

    public Guid SenderId { get; set; }
    public User Sender { get; set; } = null!;

    public string? Text { get; set; }
    public MessageType MessageType { get; set; } = MessageType.Text;

    public bool IsPinned { get; set; }

    /// <summary>Требует нажатия кнопки «Ознакомлен».</summary>
    public bool RequiresAcknowledgement { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Attachment> Attachments { get; set; } = new List<Attachment>();
    public ICollection<MessageAcknowledgement> Acknowledgements { get; set; } = new List<MessageAcknowledgement>();
    public Poll? Poll { get; set; }
}

public class Attachment
{
    public Guid Id { get; set; }

    public Guid MessageId { get; set; }
    public Message Message { get; set; } = null!;

    public string OriginalFileName { get; set; } = null!;

    /// <summary>Ключ объекта в S3-хранилище Garage, например "files/2026/11/abc.pdf".</summary>
    public string StoredFilePath { get; set; } = null!;

    public string ContentType { get; set; } = null!;
    public long FileSize { get; set; }

    /// <summary>Длительность голосового сообщения в секундах.</summary>
    public int? DurationSeconds { get; set; }
}

/// <summary>Подтверждение прочтения важного сообщения.</summary>
public class MessageAcknowledgement
{
    public Guid MessageId { get; set; }
    public Message Message { get; set; } = null!;

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public DateTime ConfirmedAt { get; set; } = DateTime.UtcNow;
}
