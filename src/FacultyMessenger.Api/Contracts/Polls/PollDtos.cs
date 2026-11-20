using System.ComponentModel.DataAnnotations;

namespace FacultyMessenger.Api.Contracts.Polls;

public record PollVoterDto(
    Guid UserId,
    string FullName,
    string? GroupName,
    DateTime VotedAt
);

public record PollOptionDto(
    Guid Id,
    string OptionText,
    int? MaxVotes,
    int SortOrder,
    int VotesCount,
    bool IsVotedByMe,
    bool IsFull,
    List<PollVoterDto> Voters
);

public record PollDto(
    Guid Id,
    Guid MessageId,
    Guid ChatId,
    string Question,
    bool IsClosed,
    bool AllowMultipleChoice,
    int TotalVotes,
    List<PollOptionDto> Options
);

public record CreatePollOptionRequest(
    [param: Required, MaxLength(300)] string OptionText,
    [param: Range(1, 10000)] int? MaxVotes = null
);

public record CreatePollRequest(
    [param: Required] Guid ChatId,
    [param: Required, MaxLength(500)] string Question,
    [param: Required, MinLength(2)] List<CreatePollOptionRequest> Options,
    bool AllowMultipleChoice = false,
    bool RequiresAcknowledgement = false
);

public record ClosePollRequest(bool IsClosed = true);
