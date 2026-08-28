using ExcelDataReader;
using Microsoft.Extensions.Options;
using System.Data;
using System.Text;
using UNVToUNXTransform.Configuration;
using UNVToUNXTransform.Infrastructure;
using UNVToUNXTransform.Models;

namespace UNVToUNXTransform.Services;

public class ExcelMappingService : IExcelMappingService
{
    private readonly ApplicationConfiguration _config;
    private readonly ILogger _logger;

    public ExcelMappingService(IOptions<ApplicationConfiguration> config, ILogger logger)
    {
        _config = config.Value;
        _logger = logger;
        
        // Register encoding provider required for ExcelDataReader
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
    }

    public List<DataSourceMapping> ReadMappings(string filePath)
    {
        _logger.LogInformation($"Reading Excel mapping file from: {filePath}");
        var mappings = new List<DataSourceMapping>();

        if (!File.Exists(filePath))
        {
            _logger.LogError($"Mapping file not found at {filePath}");
            throw new FileNotFoundException("Mapping file not found", filePath);
        }

        try
        {
            using (var stream = File.Open(filePath, FileMode.Open, FileAccess.Read))
            {
                // Auto-detect format (xls or xlsx)
                using (var reader = ExcelReaderFactory.CreateReader(stream))
                {
                    // Use AsDataSet to get a clean DataTable
                    var result = reader.AsDataSet(new ExcelDataSetConfiguration()
                    {
                        ConfigureDataTable = (_) => new ExcelDataTableConfiguration()
                        {
                            UseHeaderRow = true // Use first row as header info
                        }
                    });

                    // Assume first sheet
                    if (result.Tables.Count == 0) return mappings;
                    var table = result.Tables[0];

                    // Find columns by name (case insensitive)
                    int sourceColIndex = -1;
                    int targetColIndex = -1;

                    // Manual header search if UseHeaderRow didn't match perfectly or to be safe
                    for (int i = 0; i < table.Columns.Count; i++)
                    {
                        var colName = table.Columns[i].ColumnName;
                        if (string.Equals(colName, "Source UNV ID", StringComparison.OrdinalIgnoreCase)) sourceColIndex = i;
                        else if (string.Equals(colName, "Target UNX ID", StringComparison.OrdinalIgnoreCase)) targetColIndex = i;
                    }
                    
                    // Fallback to index if names not found (Column 0 and 1)
                    if (sourceColIndex == -1) sourceColIndex = 0;
                    if (targetColIndex == -1) targetColIndex = 1;

                    foreach (DataRow row in table.Rows)
                    {
                        var sourceId = row[sourceColIndex]?.ToString()?.Trim();
                        var targetId = row[targetColIndex]?.ToString()?.Trim();

                        if (!string.IsNullOrEmpty(sourceId))
                        {
                            mappings.Add(new DataSourceMapping
                            {
                                SourceUnvId = sourceId,
                                TargetUnxId = targetId
                            });
                        }
                    }
                }
            }
            
            _logger.LogSuccess($"Successfully read {mappings.Count} mappings from Excel.");
            return mappings;
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to read Excel mapping file.", ex);
            throw;
        }
    }
}
