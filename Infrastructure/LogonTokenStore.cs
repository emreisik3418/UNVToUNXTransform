namespace UNVToUNXTransform.Infrastructure;

/// <summary>
/// Holds the current SAP BO logon token. Registered as a singleton so every
/// HttpClientWrapper instance sends the same token: AddHttpClient registers the
/// wrapper as a transient typed client, i.e. AuthenticationService and
/// DocumentService each get their own wrapper instance.
/// </summary>
public class LogonTokenStore
{
    private volatile string? _token;

    public string? Token
    {
        get => _token;
        set => _token = value;
    }
}
