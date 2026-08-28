using UNVToUNXTransform.Models;

namespace UNVToUNXTransform.Services;

public interface IAuthenticationService
{
    Task<bool> LogonAsync();
    Task LogoffAsync();

    /// <summary>
    /// Re-establishes the session after it has expired (HTTP 401/403 from the server).
    /// Safe to call from multiple concurrent callers; concurrent calls are serialized
    /// so the session is only refreshed once.
    /// </summary>
    Task<bool> ReLogonAsync();
    string GetToken();
}
