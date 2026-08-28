namespace UNVToUNXTransform.Infrastructure;

/// <summary>
/// Thrown when the SAP BO RESTful WS returns HTTP 401/403, indicating the
/// logon token is missing, expired, or otherwise invalid. Callers should
/// re-logon and retry the failed operation once.
/// </summary>
public class SapSessionExpiredException : Exception
{
    public SapSessionExpiredException(string message) : base(message)
    {
    }
}
