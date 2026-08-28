namespace UNVToUNXTransform.Infrastructure;

public interface IHttpClientWrapper
{
    Task<T?> PostAsJsonAsync<T>(string requestUri, object content);
    Task<T?> PostCustomContentAsync<T>(string requestUri, HttpContent content);
    Task<T?> PutCustomContentAsync<T>(string requestUri, HttpContent content);
    Task<T?> GetFromJsonAsync<T>(string requestUri);
    Task<bool> DeleteAsync(string requestUri);
    void SetAuthorizationHeader(string token);
}
