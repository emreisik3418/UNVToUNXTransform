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

    /// <summary>
    /// When true, documents are only analysed and the matches are reported;
    /// nothing is converted or saved. Use it to check the mapping file first.
    /// </summary>
    public bool DryRun { get; set; }
}
