using Microsoft.Extensions.Options;
using UNVToUNXTransform.Configuration;
using UNVToUNXTransform.Infrastructure;
using UNVToUNXTransform.Models;

namespace UNVToUNXTransform.Services;

public class AuthenticationService : IAuthenticationService
{
    private readonly IHttpClientWrapper _httpClient;
    private readonly SapBoConfiguration _config;
    private readonly ILogger _logger;
    private volatile string? _logonToken;
    private readonly SemaphoreSlim _sessionLock = new SemaphoreSlim(1, 1);

    public AuthenticationService(IHttpClientWrapper httpClient, IOptions<SapBoConfiguration> config, ILogger logger)
    {
        _httpClient = httpClient;
        _config = config.Value;
        _logger = logger;
    }

    public string? CurrentToken => _logonToken;

    public async Task<bool> LogonAsync()
    {
        _logger.LogInformation("Attempting to log on to SAP BO...");

        // A stale X-SAP-LogonToken must not be sent with the logon request itself -
        // SAP BO rejects it with "Logon may not proceed because a session is
        // already associated with this request".
        _httpClient.ClearAuthorizationHeader();

        var authRequest = new AuthenticationRequest
        {
            UserName = _config.UserName,
            Password = _config.Password,
            AuthType = _config.AuthType
        };

        try
        {
            var response = await _httpClient.PostAsJsonAsync<AuthenticationResponse>("logon/long", authRequest);

            if (response != null && !string.IsNullOrEmpty(response.LogonToken))
            {
                _logonToken = response.LogonToken;
                _httpClient.SetAuthorizationHeader(_logonToken);
                _logger.LogSuccess("Logon successful.");
                return true;
            }

            _logger.LogError("Logon failed: No token received.");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError("Logon failed with exception.", ex);
            return false;
        }
    }

    public async Task LogoffAsync()
    {
        var token = _logonToken;
        if (string.IsNullOrEmpty(token)) return;

        _logger.LogInformation("Logging off from SAP BO...");
        try
        {
            _httpClient.SetAuthorizationHeader(token);
            await _httpClient.PostAsJsonAsync<object>("logoff", new { });
            _logger.LogSuccess("Logoff successful.");
        }
        catch (Exception ex)
        {
            // An already expired session cannot be logged off - that is fine.
            _logger.LogWarning($"Logoff failed (the server will release the session on its own timeout): {ex.Message}");
        }
        finally
        {
            _logonToken = null;
            _httpClient.ClearAuthorizationHeader();
        }
    }

    public async Task<bool> ReLogonAsync(string? failedToken = null)
    {
        await _sessionLock.WaitAsync();
        try
        {
            // Another worker already replaced the rejected token while we waited
            // (or the failed request was sent while a re-logon was in progress):
            // just use the new token instead of opening yet another session.
            var current = _logonToken;
            if (current != null && current != failedToken)
            {
                return true;
            }

            _logger.LogWarning("SAP BO session appears to have expired. Re-authenticating...");
            await LogoffAsync();
            return await LogonAsync();
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    public async Task<bool> RefreshSessionAsync()
    {
        await _sessionLock.WaitAsync();
        try
        {
            _logger.LogInformation("Refreshing SAP BO session...");
            await LogoffAsync();
            return await LogonAsync();
        }
        finally
        {
            _sessionLock.Release();
        }
    }
}
