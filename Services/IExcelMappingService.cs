using UNVToUNXTransform.Models;

namespace UNVToUNXTransform.Services;

public interface IExcelMappingService
{
    List<DataSourceMapping> ReadMappings(string filePath);
}
