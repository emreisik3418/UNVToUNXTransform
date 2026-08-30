namespace UNVToUNXTransform.Infrastructure;

public interface IHttpClientWrapper
{
    Task<T?> PostAsJsonAsync<T>(string requestUri, object content);
    Task<T?> PostCustomContentAsync<T>(string requestUri, HttpContent content);
    Task<T?> PutCustomContentAsync<T>(string requestUri, HttpContent content, bool isBestEffort = false);
    Task<T?> GetFromJsonAsync<T>(string requestUri);
    void SetAuthorizationHeader(string token);
    void ClearAuthorizationHeader();
}
