namespace VisionStudio.Api.Infrastructure;

public abstract class ApiProblemException : Exception
{
    protected ApiProblemException(int statusCode, string title, string detail, string code, Exception? inner = null)
        : base(detail, inner)
    {
        StatusCode = statusCode;
        Title = title;
        Code = code;
    }

    public int StatusCode { get; }
    public string Title { get; }
    public string Code { get; }
}

public sealed class ApiValidationException(string detail, Exception? inner = null)
    : ApiProblemException(StatusCodes.Status400BadRequest, "Request validation failed", detail, "validation_error", inner);

public sealed class ApiUnauthorizedException(string detail, Exception? inner = null)
    : ApiProblemException(StatusCodes.Status401Unauthorized, "Authentication failed", detail, "unauthorized", inner);

public sealed class ApiNotFoundException(string detail, Exception? inner = null)
    : ApiProblemException(StatusCodes.Status404NotFound, "Resource not found", detail, "not_found", inner);

public sealed class ApiConflictException(string detail, Exception? inner = null)
    : ApiProblemException(StatusCodes.Status409Conflict, "Operation conflicts with current state", detail, "conflict", inner);

public sealed class ApiUnavailableException(string detail, Exception? inner = null)
    : ApiProblemException(StatusCodes.Status503ServiceUnavailable, "Dependency unavailable", detail, "dependency_unavailable", inner);

/// <summary>
/// 账号因连续登录失败被临时锁定（2026-11-12）。用 429 而不是 401：现场需要区分
/// "口令输错"和"别再试了，等窗口过去或找管理员解锁"，否则只会继续重试把窗口越推越长。
/// </summary>
public sealed class ApiTooManyRequestsException(string detail, Exception? inner = null)
    : ApiProblemException(StatusCodes.Status429TooManyRequests, "Too many attempts", detail, "account_locked", inner);
