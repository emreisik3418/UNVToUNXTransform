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
    private string? _logonToken;
    private readonly SemaphoreSlim _relogonLock = new SemaphoreSlim(1, 1);

    public AuthenticationService(IHttpClientWrapper httpClient, IOptions<SapBoConfiguration> config, ILogger logger)
    {
        _httpClient = httpClient;
        _config = config.Value;
        _logger = logger;
    }

    public async Task<bool> LogonAsync()
    {
        _logger.LogInformation("Attempting to log on to SAP BO...");

        // A stale X-SAP-LogonToken header from a previous (now-expired) session
        // must not be sent with the logon request itself - SAP BO's REST API
        // rejects logon attempts that already carry a session token, with
        // "Logon may not proceed because a session is already associated with
        // this request", which otherwise looks identical to a plain logon
        // failure and leaves every subsequent request stuck retrying forever.
        _httpClient.ClearAuthorizationHeader();

        var requestUri = "logon/long";
        var authRequest = new AuthenticationRequest
        {
            UserName = _config.UserName,
            Password = _config.Password,
            AuthType = _config.AuthType
        };

        try
        {
            var response = await _httpClient.PostAsJsonAsync<AuthenticationResponse>(requestUri, authRequest);

            if (response != null && !string.IsNullOrEmpty(response.LogonToken))
            {
                _logonToken = response.LogonToken;
                // Important: Set the token in the HTTP client wrapper for future requests
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
        if (string.IsNullOrEmpty(_logonToken)) return;

        _logger.LogInformation("Logging off from SAP BO...");
        try
        {
            // Ensure token is set (though it should be for the session)
            _httpClient.SetAuthorizationHeader(_logonToken);
            
            // Endpoint is usually just "logoff"
            await _httpClient.PostAsJsonAsync<object>("logoff", new { });
            
            _logonToken = null;
            _logger.LogSuccess("Logoff successful.");
        }
        catch (Exception ex)
        {
            // Logoff failure shouldn't stop flow usually, but good to note
            _logger.LogWarning($"Logoff failed (session might verify timeout later): {ex.Message}");
        }
    }

    public async Task<bool> ReLogonAsync()
    {
        await _relogonLock.WaitAsync();
        try
        {
            _logger.LogWarning("SAP BO session appears to have expired. Re-authenticating...");
            _logonToken = null;
            return await LogonAsync();
        }
        finally
        {
            _relogonLock.Release();
        }
    }

    public string GetToken()
    {
        return _logonToken ?? throw new InvalidOperationException("Not logged on.");
    }
}
