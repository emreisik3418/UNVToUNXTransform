namespace UNVToUNXTransform.Infrastructure;

public interface IHttpClientWrapper
{
    Task<T?> PostAsJsonAsync<T>(string requestUri, object content);
    Task<T?> PostCustomContentAsync<T>(string requestUri, string jsonBody);
    Task<T?> PutCustomContentAsync<T>(string requestUri, string jsonBody, bool isBestEffort = false);
    /// <summary>PUT that only reports whether the server accepted it (2xx).</summary>
    Task<bool> PutForStatusAsync(string requestUri, string jsonBody, bool isBestEffort = false);
    Task<T?> GetFromJsonAsync<T>(string requestUri);

    /// <summary>The logon token currently attached to outgoing requests, if any.</summary>
    string? CurrentToken { get; }
    void SetAuthorizationHeader(string token);
    void ClearAuthorizationHeader();
}
