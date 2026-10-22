namespace UNVToUNXTransform.Services;

public interface IAuthenticationService
{
    Task<bool> LogonAsync();
    Task LogoffAsync();

    /// <summary>
    /// Re-establishes the session after the server rejected a token (HTTP 401).
    /// Pass the token the failed request was sent with: if another caller has
    /// already replaced it, no new logon is made. The old session is logged off
    /// first so it does not linger on the server with open documents.
    /// </summary>
    Task<bool> ReLogonAsync(string? failedToken = null);

    /// <summary>
    /// Logs off and on again. Used between batches, when no request is in flight,
    /// so any server-side resource the session might still hold is released.
    /// </summary>
    Task<bool> RefreshSessionAsync();

    /// <summary>The current logon token, or null when not logged on.</summary>
    string? CurrentToken { get; }
}
