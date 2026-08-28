namespace UNVToUNXTransform.Models;

public class DataSourceMapping
{
    public int DocumentId { get; set; }
    public string DocumentName { get; set; }
    public string DataProviderId { get; set; }
    public string DataProviderName { get; set; }
    public string SourceUnvId { get; set; }
    public string TargetUnxId { get; set; }
}
