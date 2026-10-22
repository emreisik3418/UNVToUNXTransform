using System.IO;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

namespace UNVToUNXTransform.Infrastructure;

public class HttpClientWrapper : IHttpClientWrapper
{
    private const string LogonTokenHeader = "X-SAP-LogonToken";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;

    // The logon token is attached to each request individually instead of being
    // stored in HttpClient.DefaultRequestHeaders. DefaultRequestHeaders is not
    // safe to modify while other requests are in flight, and documents are now
    // processed in parallel - a re-logon on one worker must not corrupt the
    // headers of requests sent concurrently by another. The token itself lives
    // in a shared singleton so all wrapper instances use the same one.
    private readonly LogonTokenStore _tokenStore;

    public HttpClientWrapper(HttpClient httpClient, ILogger logger, LogonTokenStore tokenStore)
    {
        _httpClient = httpClient;
        _logger = logger;
        _tokenStore = tokenStore;
    }

    public string? CurrentToken => _tokenStore.Token;

    public void SetAuthorizationHeader(string token) => _tokenStore.Token = token;

    public void ClearAuthorizationHeader() => _tokenStore.Token = null;

    /// <summary>
    /// Only HTTP 401 means the logon token is no longer valid. HTTP 403 is a
    /// permission error on a specific object (e.g. a document the account may
    /// not edit) while the session itself is still alive - treating it as an
    /// expired session would trigger a needless re-logon and orphan the old
    /// session, together with every WebI document still open in it.
    /// </summary>
    private static void ThrowIfSessionExpired(HttpResponseMessage response, string errorContent)
    {
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            throw new SapSessionExpiredException(errorContent);
        }
    }

    /// <summary>
    /// A dropped/stale pooled TCP connection (e.g. silently killed by a
    /// firewall, load balancer, or SAP Web Dispatcher after being idle or
    /// long-lived) surfaces here as a timeout or aborted I/O, not as an HTTP
    /// status code. One quiet retry on a fresh connection resolves it.
    /// </summary>
    private static bool IsTransientTransportError(Exception ex)
    {
        return ex is IOException
            || ex is SocketException
            || ex is TaskCanceledException
            || (ex is HttpRequestException hre && hre.InnerException is IOException or SocketException);
    }

    /// <param name="createContent">
    /// Factory for the request body. A new HttpContent is created for every
    /// attempt because content that was already (partially) sent cannot be resent.
    /// </param>
    /// <param name="retryOnTimeout">
    /// false for POSTs: a POST that timed out may still be running on the
    /// server (e.g. applying a mapping plan). Sending it again would double the
    /// load on an already struggling server and may apply the change twice.
    /// </param>
    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string requestUri, Func<HttpContent?>? createContent, bool retryOnTimeout)
    {
        const int maxAttempts = 2;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(method, requestUri);
                var token = _tokenStore.Token;
                if (!string.IsNullOrEmpty(token))
                {
                    request.Headers.TryAddWithoutValidation(LogonTokenHeader, token);
                }
                request.Content = createContent?.Invoke();
                return await _httpClient.SendAsync(request);
            }
            catch (Exception ex) when (attempt < maxAttempts
                                       && IsTransientTransportError(ex)
                                       && (retryOnTimeout || ex is not TaskCanceledException))
            {
                _logger.LogWarning($"Transient connection error on {method} {requestUri}: {ex.Message}. Retrying once on a fresh connection...");
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }

        // Unreachable: the final attempt either returns or lets its exception propagate.
        throw new InvalidOperationException("Unreachable.");
    }

    private async Task<T?> ReadResponseAsync<T>(HttpResponseMessage response, string verb)
    {
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError($"API {verb} Error: {response.StatusCode} - {errorContent}");
                ThrowIfSessionExpired(response, errorContent);
                return default;
            }

            var json = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(json))
            {
                return default;
            }

            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
    }

    public async Task<T?> PostAsJsonAsync<T>(string requestUri, object content)
    {
        try
        {
            var response = await SendAsync(HttpMethod.Post, requestUri, () => JsonContent.Create(content), retryOnTimeout: false);
            return await ReadResponseAsync<T>(response, "Post");
        }
        catch (SapSessionExpiredException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError($"HTTP Post Exception ({requestUri}): {ex.Message}", ex);
            throw;
        }
    }

    public async Task<T?> PostCustomContentAsync<T>(string requestUri, string jsonBody)
    {
        try
        {
            var response = await SendAsync(HttpMethod.Post, requestUri, () => CreateJson(jsonBody), retryOnTimeout: false);
            return await ReadResponseAsync<T>(response, "Post");
        }
        catch (SapSessionExpiredException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError($"HTTP Post Exception ({requestUri}): {ex.Message}", ex);
            throw;
        }
    }

    public async Task<T?> PutCustomContentAsync<T>(string requestUri, string jsonBody, bool isBestEffort = false)
    {
        try
        {
            var response = await SendAsync(HttpMethod.Put, requestUri, () => CreateJson(jsonBody), retryOnTimeout: true);
            return await ReadResponseAsync<T>(response, "Put");
        }
        catch (SapSessionExpiredException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Best-effort calls (closing a document) log a single, clearly
            // labeled outcome at the call site instead.
            if (!isBestEffort)
            {
                _logger.LogError($"HTTP Put Exception ({requestUri}): {ex.Message}", ex);
            }
            throw;
        }
    }

    public async Task<bool> PutForStatusAsync(string requestUri, string jsonBody, bool isBestEffort = false)
    {
        try
        {
            using var response = await SendAsync(HttpMethod.Put, requestUri, () => CreateJson(jsonBody), retryOnTimeout: true);
            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogError($"API Put Error ({requestUri}): {response.StatusCode} - {errorContent}");
            ThrowIfSessionExpired(response, errorContent);
            return false;
        }
        catch (SapSessionExpiredException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (!isBestEffort)
            {
                _logger.LogError($"HTTP Put Exception ({requestUri}): {ex.Message}", ex);
            }
            throw;
        }
    }

    public async Task<T?> GetFromJsonAsync<T>(string requestUri)
    {
        try
        {
            var response = await SendAsync(HttpMethod.Get, requestUri, null, retryOnTimeout: true);
            return await ReadResponseAsync<T>(response, "Get");
        }
        catch (SapSessionExpiredException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError($"HTTP Get Exception ({requestUri}): {ex.Message}", ex);
            throw;
        }
    }

    private static StringContent CreateJson(string body) =>
        new StringContent(body, System.Text.Encoding.UTF8, "application/json");
}
