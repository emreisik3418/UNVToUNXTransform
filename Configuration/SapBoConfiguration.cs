namespace UNVToUNXTransform.Configuration;

public class SapBoConfiguration
{
    public const string SectionName = "SapBo";
    
    public string BaseUrl { get; set; }
    public string UserName { get; set; }
    public string Password { get; set; }
    public string AuthType { get; set; } = "secEnterprise";

    /// <summary>Max concurrent HTTP requests when fetching data providers for the document list.</summary>
    public int MaxParallelism { get; set; } = 10;

    /// <summary>Documents processed per progress-reporting batch while listing data providers.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>HttpClient timeout, in seconds, for each individual request.</summary>
    public int RequestTimeoutSeconds { get; set; } = 100;

    /// <summary>
    /// Page size used when paging through /v1/cmsquery results. SAP BO defaults
    /// to 50 results per page if not specified, which silently truncates large
    /// result sets unless the client pages through them explicitly.
    /// </summary>
    public int CmsQueryPageSize { get; set; } = 200;
}
