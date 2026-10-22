namespace UNVToUNXTransform.Configuration;

public class SapBoConfiguration
{
    public const string SectionName = "SapBo";
    
    public string BaseUrl { get; set; }
    public string UserName { get; set; }
    public string Password { get; set; }
    public string AuthType { get; set; } = "secEnterprise";

    /// <summary>
    /// How many documents are processed at the same time. Each one holds an open
    /// WebI document on the server while it is being converted, so keep this low
    /// (2-3) on a small server.
    /// </summary>
    public int MaxParallelism { get; set; } = 2;

    /// <summary>Documents per batch. Between batches the session is refreshed (see RefreshSessionAfterEachBatch).</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>
    /// When true, the tool logs off and on again after each batch, while no
    /// request is in flight, so nothing can accumulate in one long session.
    /// </summary>
    public bool RefreshSessionAfterEachBatch { get; set; } = true;

    /// <summary>
    /// HttpClient timeout, in seconds, for each individual request. Applying a
    /// mapping plan on a large report can take minutes.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Page size used when paging through /v1/cmsquery results. SAP BO defaults
    /// to 50 results per page if not specified, which silently truncates large
    /// result sets unless the client pages through them explicitly.
    /// </summary>
    public int CmsQueryPageSize { get; set; } = 200;
}
