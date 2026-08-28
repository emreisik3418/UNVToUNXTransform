using System.Net.Http.Json;
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

    public async Task<T?> PostAsJsonAsync<T>(string requestUri, object content)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync(requestUri, content);

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
            var response = await _httpClient.PostAsync(requestUri, content);

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

    public async Task<T?> PutCustomContentAsync<T>(string requestUri, HttpContent content)
    {
        try
        {
            var response = await _httpClient.PutAsync(requestUri, content);

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
        catch (Exception ex)
        {
            _logger.LogError($"HTTP Put Exception: {ex.Message}", ex);
            throw;
        }
    }

    public async Task<T?> GetFromJsonAsync<T>(string requestUri)
    {
        try
        {
            var response = await _httpClient.GetAsync(requestUri);

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

    public async Task<bool> DeleteAsync(string requestUri)
    {
        try
        {
            var response = await _httpClient.DeleteAsync(requestUri);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError($"API Delete Error: {response.StatusCode} - {errorContent}");
                ThrowIfSessionExpired(response, errorContent);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError($"HTTP Delete Exception: {ex.Message}", ex);
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
}
