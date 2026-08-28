using System.Text.Json.Serialization;

namespace UNVToUNXTransform.Models;

public class Document
{
    [JsonPropertyName("id")]
    public int Id { get; set; }
    
    [JsonPropertyName("cuid")]
    public string Cuid { get; set; }
    
    [JsonPropertyName("name")]
    public string Name { get; set; }
    
    [JsonPropertyName("description")]
    public string Description { get; set; }
    
    [JsonPropertyName("folderId")]
    public int FolderId { get; set; }

    // Not mapped from JSON, populated separately
    public List<DataProvider> DataProviders { get; set; } = new List<DataProvider>();
}

public class DocumentListResponse
{
    [JsonPropertyName("documents")]
    public DocumentsWrapper Documents { get; set; }
}

public class DocumentsWrapper
{
    [JsonPropertyName("document")]
    public List<Document> DocumentList { get; set; }
}
