namespace UNVToUNXTransform.Configuration;

public class ApplicationConfiguration
{
    public const string SectionName = "Application";
    
    public string OutputDirectory { get; set; }
    public string MappingFileName { get; set; } = "DataSourceMapping.xlsx";

    /// <summary>
    /// When set, restricts the document scan to WebI documents under this CMS
    /// folder (SI_ID of the folder), recursively, via SI_ANCESTOR. Leave null
    /// to scan the entire repository.
    /// </summary>
    public int? SourceFolderId { get; set; }
}
