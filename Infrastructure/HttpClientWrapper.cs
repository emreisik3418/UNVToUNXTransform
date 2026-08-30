using System.IO;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

namespace UNVToUNXTransform.Infrastructure;

public class HttpClientWrapper : IHttpClientWrapper
{
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;

    public HttpClientWrapper(HttpClient httpClient, ILogger logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    private static void ThrowIfSessionExpired(System.Net.Http.HttpResponseMessage response, string errorContent)
    {
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized || response.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            throw new SapSessionExpiredException(errorContent);
        }
    }

    /// <summary>
    /// A dropped/stale pooled TCP connection (e.g. silently killed by a
    /// firewall, load balancer, or SAP Web Dispatcher after being idle or
    /// long-lived) surfaces here as a timeout or aborted I/O, not as an HTTP
    /// status code, so it can't be told apart from a genuinely slow or dead
    /// server by inspecting a response. One quiet retry on a fresh connection
    /// resolves it without giving up on the whole document/data provider.
    /// </summary>
    private static bool IsTransientTransportError(Exception ex)
    {
        return ex is IOException
            || ex is SocketException
            || ex is TaskCanceledException
            || (ex is HttpRequestException hre && hre.InnerException is IOException or SocketException);
    }

    private async Task<System.Net.Http.HttpResponseMessage> SendWithTransientRetryAsync(Func<Task<System.Net.Http.HttpResponseMessage>> send, string requestDescription)
    {
        const int maxAttempts = 2;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                return await send();
            }
            catch (Exception ex) when (attempt < maxAttempts && IsTransientTransportError(ex))
            {
                _logger.LogWarning($"Transient connection error on {requestDescription}: {ex.Message}. Retrying once on a fresh connection...");
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }

        // Unreachable: the final attempt above either returns or lets its
        // exception propagate (the `when` guard is false once attempt == maxAttempts).
        throw new InvalidOperationException("Unreachable.");
    }

    public async Task<T?> PostAsJsonAsync<T>(string requestUri, object content)
    {
        try
        {
            var response = await SendWithTransientRetryAsync(() => _httpClient.PostAsJsonAsync(requestUri, content), requestUri);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError($"API Post Error: {response.StatusCode} - {errorContent}");
                ThrowIfSessionExpired(response, errorContent);
                return default;
            }

            var jsonResponse = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(jsonResponse))
            {
                // Typically creates/updates might return empty body for success, but checking expectation
                return default;
            }

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
            return JsonSerializer.Deserialize<T>(jsonResponse, options);
        }
        catch (Exception ex)
        {
            _logger.LogError($"HTTP Post Exception: {ex.Message}", ex);
            throw;
        }
    }

    public async Task<T?> PostCustomContentAsync<T>(string requestUri, HttpContent content)
    {
        try
        {
            var response = await SendWithTransientRetryAsync(() => _httpClient.PostAsync(requestUri, content), requestUri);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError($"API Post Error: {response.StatusCode} - {errorContent}");
                ThrowIfSessionExpired(response, errorContent);
                return default;
            }

            var jsonResponse = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(jsonResponse))
            {
                return default;
            }

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
            return JsonSerializer.Deserialize<T>(jsonResponse, options);
        }
        catch (Exception ex)
        {
            _logger.LogError($"HTTP Post Exception: {ex.Message}", ex);
            throw;
        }
    }

    public async Task<T?> PutCustomContentAsync<T>(string requestUri, HttpContent content, bool isBestEffort = false)
    {
        try
        {
            var response = await SendWithTransientRetryAsync(() => _httpClient.PutAsync(requestUri, content), requestUri);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError($"API Put Error: {response.StatusCode} - {errorContent}");
                ThrowIfSessionExpired(response, errorContent);
                return default;
            }

            var jsonResponse = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(jsonResponse))
            {
                return default;
            }

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
            return JsonSerializer.Deserialize<T>(jsonResponse, options);
        }
        catch (SapSessionExpiredException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A best-effort call (e.g. closing a document session) can hit a
            // transient connection error - most often a burst of near-simultaneous
            // requests all opening new connections at once - and the caller
            // already retries and logs a single, clearly-labeled outcome. Logging
            // it here too as a raw ERROR would just be confusing, alarming noise
            // for something that isn't actually a problem.
            if (!isBestEffort)
            {
                _logger.LogError($"HTTP Put Exception: {ex.Message}", ex);
            }
            throw;
        }
    }

    public async Task<T?> GetFromJsonAsync<T>(string requestUri)
    {
        try
        {
            var response = await SendWithTransientRetryAsync(() => _httpClient.GetAsync(requestUri), requestUri);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError($"API Get Error: {response.StatusCode} - {errorContent}");
                ThrowIfSessionExpired(response, errorContent);
                return default;
            }

            return await response.Content.ReadFromJsonAsync<T>();
        }
        catch (Exception ex)
        {
            _logger.LogError($"HTTP Get Exception: {ex.Message}", ex);
            throw;
        }
    }

    public void SetAuthorizationHeader(string token)
    {
        if (_httpClient.DefaultRequestHeaders.Contains("X-SAP-LogonToken"))
        {
            _httpClient.DefaultRequestHeaders.Remove("X-SAP-LogonToken");
        }
        _httpClient.DefaultRequestHeaders.Add("X-SAP-LogonToken", token);

        // Also simpler "Authorization" just in case, but SAP BO typically uses X-SAP-LogonToken
        // _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    }

    public void ClearAuthorizationHeader()
    {
        if (_httpClient.DefaultRequestHeaders.Contains("X-SAP-LogonToken"))
        {
            _httpClient.DefaultRequestHeaders.Remove("X-SAP-LogonToken");
        }
    }
}
