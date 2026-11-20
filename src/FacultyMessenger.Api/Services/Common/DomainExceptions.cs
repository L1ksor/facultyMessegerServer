namespace FacultyMessenger.Api.Services.Common;

public abstract class DomainException : Exception
{
    public int StatusCode { get; }

    protected DomainException(string message, int statusCode) : base(message)
        => StatusCode = statusCode;
}

public sealed class DomainNotFoundException : DomainException
{
    public DomainNotFoundException(string message) : base(message, StatusCodes.Status404NotFound) { }
}

public sealed class DomainForbiddenException : DomainException
{
    public DomainForbiddenException(string message) : base(message, StatusCodes.Status403Forbidden) { }
}

public sealed class DomainBadRequestException : DomainException
{
    public DomainBadRequestException(string message) : base(message, StatusCodes.Status400BadRequest) { }
}

public sealed class DomainConflictException : DomainException
{
    public DomainConflictException(string message) : base(message, StatusCodes.Status409Conflict) { }
}

public sealed class DomainUnauthorizedException : DomainException
{
    public DomainUnauthorizedException(string message) : base(message, StatusCodes.Status401Unauthorized) { }
}
