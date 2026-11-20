namespace FacultyMessenger.Api.Domain.Entities;

public class Poll
{
    public Guid Id { get; set; }

    public Guid MessageId { get; set; }
    public Message Message { get; set; } = null!;

    public string Question { get; set; } = null!;
    public bool IsClosed { get; set; }

    /// <summary>Разрешено ли выбирать несколько вариантов (по умолчанию — один голос на опрос).</summary>
    public bool AllowMultipleChoice { get; set; }

    public ICollection<PollOption> Options { get; set; } = new List<PollOption>();
}

public class PollOption
{
    public Guid Id { get; set; }

    public Guid PollId { get; set; }
    public Poll Poll { get; set; } = null!;

    public string OptionText { get; set; } = null!;

    /// <summary>Лимит мест (запись на сдачу лаб, выбор тем ВКР). Null — без ограничений.</summary>
    public int? MaxVotes { get; set; }

    public int SortOrder { get; set; }

    /// <summary>
    /// Токен параллелизма PostgreSQL (системный столбец xmin).
    /// Используется при голосовании, чтобы не превысить MaxVotes при гонке запросов.
    /// </summary>
    public uint Version { get; set; }

    public ICollection<PollVote> Votes { get; set; } = new List<PollVote>();
}

public class PollVote
{
    public Guid Id { get; set; }

    public Guid PollOptionId { get; set; }
    public PollOption PollOption { get; set; } = null!;

    /// <summary>Денормализация для ограничения «один голос на опрос».</summary>
    public Guid PollId { get; set; }
    public Poll Poll { get; set; } = null!;

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public DateTime VotedAt { get; set; } = DateTime.UtcNow;
}
