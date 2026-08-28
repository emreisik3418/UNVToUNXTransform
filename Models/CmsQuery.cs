using System.Text.Json.Serialization;

namespace UNVToUNXTransform.Models;

public class CmsQueryRequest
{
    [JsonPropertyName("query")]
    public string Query { get; set; }
}

public class CmsQueryResponse
{
    // The structure depends on the API response for CMS queries.
    // Usually it returns a generic list of properties for each object found.
    // Let's assume a simplified mapping or dynamic if complex.
    // Often it wraps results in "entries" or similar.
    
    // For specific fields we asked (SI_ID, SI_NAME, SI_CUID) we can try to map them.
    // But CMS query responses can be tricky JSON.
    // Let's use a flexible mapping for the entries.
    
    [JsonPropertyName("entries")]
    public List<CmsEntry> Entries { get; set; }
    
    // Sometimes it's directly a list or wrapped differently.
    // We might need to inspect the response if it fails, but let's try standard structure.
}

public class CmsEntry
{
    [JsonPropertyName("SI_ID")]
    public object SiId { get; set; } // Can be int or string in JSON
    
    [JsonPropertyName("SI_NAME")]
    public string SiName { get; set; }
    
    [JsonPropertyName("SI_CUID")]
    public string SiCuid { get; set; }

    // Helper to get int ID safely
    public int GetId()
    {
        if (SiId is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.Number)
        {
            return je.GetInt32();
        }
        if (int.TryParse(SiId?.ToString(), out int id)) return id;
        return 0;
    }
}
