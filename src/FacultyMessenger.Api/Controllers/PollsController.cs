using FacultyMessenger.Api.Auth;
using FacultyMessenger.Api.Contracts.Messages;
using FacultyMessenger.Api.Contracts.Polls;
using FacultyMessenger.Api.Services.Common;
using FacultyMessenger.Api.Services.Polls;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FacultyMessenger.Api.Controllers;

[ApiController]
[Route("api/polls")]
[Authorize]
public class PollsController : ControllerBase
{
    private readonly IPollService _polls;

    public PollsController(IPollService polls) => _polls = polls;

    /// <summary>Создание опроса в чате (доступно участникам согласно настройкам беседы и ролям чата).</summary>
    [HttpPost]
    [ProducesResponseType<MessageDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MessageDto>> CreatePoll(
        [FromBody] CreatePollRequest request,
        CancellationToken ct)
    {
        try
        {
            var message = await _polls.CreatePollAsync(request, User.GetUserId(), ct);
            return CreatedAtAction(nameof(GetPoll), new { pollId = message.Poll!.Id }, message);
        }
        catch (DomainException ex)
        {
            return Problem(statusCode: ex.StatusCode, detail: ex.Message);
        }
    }

    /// <summary>Получение состояния опроса и списка вариантов.</summary>
    [HttpGet("{pollId:guid}")]
    [ProducesResponseType<PollDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PollDto>> GetPoll(Guid pollId, CancellationToken ct)
    {
        try
        {
            return Ok(await _polls.GetPollAsync(pollId, User.GetUserId(), ct));
        }
        catch (DomainException ex)
        {
            return Problem(statusCode: ex.StatusCode, detail: ex.Message);
        }
    }

    /// <summary>Голосование за вариант с транзакционной проверкой лимита мест MaxVotes.</summary>
    [HttpPost("{optionId:guid}/vote")]
    [ProducesResponseType<PollDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PollDto>> Vote(Guid optionId, CancellationToken ct)
    {
        try
        {
            return Ok(await _polls.VoteAsync(optionId, User.GetUserId(), ct));
        }
        catch (DomainException ex)
        {
            return Problem(statusCode: ex.StatusCode, detail: ex.Message);
        }
    }

    /// <summary>Отмена своего голоса за выбранный вариант.</summary>
    [HttpDelete("{optionId:guid}/vote")]
    [ProducesResponseType<PollDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PollDto>> Unvote(Guid optionId, CancellationToken ct)
    {
        try
        {
            return Ok(await _polls.UnvoteAsync(optionId, User.GetUserId(), ct));
        }
        catch (DomainException ex)
        {
            return Problem(statusCode: ex.StatusCode, detail: ex.Message);
        }
    }

    /// <summary>Закрытие или повторное открытие опроса (автор опроса или менеджер чата).</summary>
    [HttpPatch("{pollId:guid}/close")]
    [ProducesResponseType<PollDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PollDto>> SetClosed(
        Guid pollId,
        [FromBody] ClosePollRequest? request,
        CancellationToken ct)
    {
        try
        {
            return Ok(await _polls.SetClosedAsync(pollId, request?.IsClosed ?? true, User.GetUserId(), ct));
        }
        catch (DomainException ex)
        {
            return Problem(statusCode: ex.StatusCode, detail: ex.Message);
        }
    }
}
