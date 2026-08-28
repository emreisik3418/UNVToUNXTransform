namespace UNVToUNXTransform.Infrastructure;

public interface ILogger
{
    void LogInformation(string message);
    void LogWarning(string message);
    void LogError(string message, Exception? ex = null);
    void LogSuccess(string message);
}
