using System.Text.Json.Serialization;

namespace UNVToUNXTransform.Models;

public class DataProvider
{
    [JsonPropertyName("id")]
    public string Id { get; set; }
    
    [JsonPropertyName("name")]
    public string Name { get; set; }
    
    [JsonPropertyName("dataSourceId")]
    public string DataSourceId { get; set; }

    [JsonPropertyName("updated")]
    public string Updated { get; set; }
    
    // Helper to check if it looks like a UNV
    // Note: The API response might vary, usually source UNV IDs are integers or specific format strings.
    // We will inspect dataSourceId.
}

public class DataProviderListResponse
{
    [JsonPropertyName("dataproviders")]
    public DataProvidersWrapper DataProviders { get; set; }
}

public class DataProvidersWrapper
{
    [JsonPropertyName("dataprovider")]
    public List<DataProvider> DataProviderList { get; set; }
}
