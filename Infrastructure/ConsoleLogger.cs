using Microsoft.Extensions.Options;
using UNVToUNXTransform.Configuration;

namespace UNVToUNXTransform.Infrastructure;

public class ConsoleLogger : ILogger
{
    private readonly string _logFilePath;

    // Documents are processed in parallel; without a lock concurrent writes to
    // the log file fail (file in use) and lines are silently lost, and console
    // colors of different messages get mixed up.
    private readonly object _sync = new object();

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
        lock (_sync)
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine($"[INFO] {DateTime.Now:HH:mm:ss} - {message}");
            Console.ResetColor();
            LogToFile("INFO", message);
        }
    }

    public void LogWarning(string message)
    {
        lock (_sync)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[WARN] {DateTime.Now:HH:mm:ss} - {message}");
            Console.ResetColor();
            LogToFile("WARN", message);
        }
    }

    public void LogError(string message, Exception? ex = null)
    {
        lock (_sync)
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
    }

    public void LogSuccess(string message)
    {
        lock (_sync)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[SUCCESS] {DateTime.Now:HH:mm:ss} - {message}");
            Console.ResetColor();
            LogToFile("SUCCESS", message);
        }
    }
}
