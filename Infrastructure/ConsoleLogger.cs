using Microsoft.Extensions.Options;
using UNVToUNXTransform.Configuration;

namespace UNVToUNXTransform.Infrastructure;

public class ConsoleLogger : ILogger
{
    private readonly string _logFilePath;

    public ConsoleLogger(IOptions<ApplicationConfiguration> config)
    {
        // Define log directory and file
        var outputDir = config.Value.OutputDirectory ?? AppDomain.CurrentDomain.BaseDirectory;
        var logDir = Path.Combine(outputDir, "Logs");
        
        if (!Directory.Exists(logDir))
        {
            Directory.CreateDirectory(logDir);
        }

        // Daily log file
        _logFilePath = Path.Combine(logDir, $"Log_{DateTime.Now:yyyyMMdd}.txt");
        
        // Ensure the file exists immediately
        if (!File.Exists(_logFilePath))
        {
            File.Create(_logFilePath).Dispose();
        }
    }

    private void LogToFile(string type, string message)
    {
        try
        {
            var logLine = $"[{type}] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}{Environment.NewLine}";
            File.AppendAllText(_logFilePath, logLine);
        }
        catch
        {
            // Fail silently on file IO to not crash app, but maybe print to console
        }
    }

    public void LogInformation(string message)
    {
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine($"[INFO] {DateTime.Now:HH:mm:ss} - {message}");
        Console.ResetColor();
        LogToFile("INFO", message);
    }

    public void LogWarning(string message)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[WARN] {DateTime.Now:HH:mm:ss} - {message}");
        Console.ResetColor();
        LogToFile("WARN", message);
    }

    public void LogError(string message, Exception? ex = null)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[ERROR] {DateTime.Now:HH:mm:ss} - {message}");
        if (ex != null)
        {
            Console.WriteLine(ex.ToString());
        }
        Console.ResetColor();

        var text = message;
        if (ex != null) text += Environment.NewLine + ex.ToString();
        LogToFile("ERROR", text);
    }

    public void LogSuccess(string message)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[SUCCESS] {DateTime.Now:HH:mm:ss} - {message}");
        Console.ResetColor();
        LogToFile("SUCCESS", message);
    }
}
