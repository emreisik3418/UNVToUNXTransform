using UNVToUNXTransform.Models;

namespace UNVToUNXTransform.Services;

public interface IMigrationService
{
    /// <summary>Scans the WebI documents and converts the ones that use a mapped UNV.</summary>
    Task RunAsync(IReadOnlyList<DataSourceMapping> mappings);
}
