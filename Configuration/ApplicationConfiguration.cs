namespace UNVToUNXTransform.Configuration;

public class ApplicationConfiguration
{
    public const string SectionName = "Application";
    
    public string OutputDirectory { get; set; }
    public string MappingFileName { get; set; } = "DataSourceMapping.xlsx";
}
