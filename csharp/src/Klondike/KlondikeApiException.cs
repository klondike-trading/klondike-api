namespace Klondike;

/// <summary>An error answer of the API.</summary>
public sealed class KlondikeApiException(int status, string code, string message, TimeSpan? retryAfter = null)
    : Exception($"klondike: {status} {code}: {message}")
{
    /// <summary>HTTP status.</summary>
    public int Status { get; } = status;

    /// <summary><c>bad_request</c>, <c>unauthorized</c>, <c>not_found</c>, <c>rate_limited</c>, <c>unavailable</c>...</summary>
    public string Code { get; } = code;

    /// <summary>The message of the server, without the status and the code.</summary>
    public string ErrorMessage { get; } = message;

    /// <summary>How long to wait, on a 429.</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;

    /// <summary>The symbol has no data: no venue streams it.</summary>
    public bool IsNotFound => Status == 404;

    /// <summary>The API key is missing, unknown or revoked.</summary>
    public bool IsUnauthorized => Status == 401;
}
