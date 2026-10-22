using Microsoft.Extensions.Options;
using UNVToUNXTransform.Configuration;
using UNVToUNXTransform.Infrastructure;
using UNVToUNXTransform.Models;

namespace UNVToUNXTransform.Services;

public class DocumentService : IDocumentService
{
    private readonly IHttpClientWrapper _httpClient;
    private readonly IAuthenticationService _authService;
    private readonly ILogger _logger;
    private readonly SapBoConfiguration _config;
    private readonly ApplicationConfiguration _appConfig;

    public DocumentService(IHttpClientWrapper httpClient, IAuthenticationService authService, ILogger logger, IOptions<SapBoConfiguration> config, IOptions<ApplicationConfiguration> appConfig)
    {
        _httpClient = httpClient;
        _authService = authService;
        _logger = logger;
        _config = config.Value;
        _appConfig = appConfig.Value;
    }

    /// <summary>
    /// Runs an operation and, if the server rejects the logon token (HTTP 401),
    /// re-authenticates once and retries. The token the operation was sent with
    /// is passed along so that concurrent workers hitting the same expiry
    /// trigger only one re-logon between them.
    /// </summary>
    private async Task<T> ExecuteWithSessionRetryAsync<T>(Func<Task<T>> operation)
    {
        string? tokenBefore = _authService.CurrentToken;
        try
        {
            return await operation();
        }
        catch (SapSessionExpiredException)
        {
            if (!await _authService.ReLogonAsync(tokenBefore))
            {
                throw;
            }
            return await operation();
        }
    }

    public async Task<List<CmsEntry>> ListWebiDocumentsAsync()
    {
        _logger.LogInformation("Fetching list of WebI documents using CMS Query...");

        // SI_INSTANCE = 0: the document itself, not scheduled instances.
        // SI_ANCESTOR: restricts the scan to a folder and everything below it.
        string queryText = "SELECT SI_ID, SI_NAME, SI_CUID FROM CI_INFOOBJECTS WHERE SI_KIND = 'Webi' AND SI_INSTANCE = 0";
        if (_appConfig.SourceFolderId.HasValue)
        {
            queryText += $" AND SI_ANCESTOR = {_appConfig.SourceFolderId.Value}";
            _logger.LogInformation($"Restricting scan to CMS folder SI_ID={_appConfig.SourceFolderId.Value} (and its subfolders).");
        }

        var cmsQuery = new CmsQueryRequest { Query = queryText };

        // The REST API returns 50 results per call by default and silently
        // truncates the rest - page through explicitly.
        int pageSize = _config.CmsQueryPageSize > 0 ? _config.CmsQueryPageSize : 200;
        var entries = new List<CmsEntry>();
        int page = 1;
        while (true)
        {
            int currentPage = page;
            var pageResponse = await ExecuteWithSessionRetryAsync(() =>
                _httpClient.PostAsJsonAsync<CmsQueryResponse>($"v1/cmsquery?page={currentPage}&pagesize={pageSize}", cmsQuery));

            var pageEntries = pageResponse?.Entries;
            if (pageEntries == null || pageEntries.Count == 0)
            {
                break;
            }

            entries.AddRange(pageEntries);

            if (pageEntries.Count < pageSize)
            {
                break; // last page
            }

            page++;
        }

        _logger.LogInformation($"Found {entries.Count} WebI documents.");
        return entries;
    }

    public async Task<List<DataProvider>?> GetDataProvidersAsync(int docId)
    {
        try
        {
            var response = await ExecuteWithSessionRetryAsync(() =>
                _httpClient.GetFromJsonAsync<DataProviderListResponse>($"raylight/v1/documents/{docId}/dataproviders"));

            return response?.DataProviders?.DataProviderList ?? (response == null ? null : new List<DataProvider>());
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Could not read data providers of document {docId}: {ex.Message}");
            return null;
        }
    }

    public async Task<List<string>> ChangeDataSourceAsync(int docId, List<string> dataProviderIds, string targetUnxId)
    {
        if (string.IsNullOrWhiteSpace(targetUnxId))
        {
            _logger.LogError($"Target UNX ID is empty for Document {docId}, Data Providers [{string.Join(",", dataProviderIds)}]. Check the Excel mapping file for a missing 'Target UNX ID' value on the matching Source UNV ID row.");
            return new List<string>(dataProviderIds);
        }

        try
        {
            bool success = await ExecuteWithSessionRetryAsync(() => TryApplyMappingAsync(docId, dataProviderIds, targetUnxId));
            if (success)
            {
                return new List<string>();
            }
            if (dataProviderIds.Count <= 1)
            {
                return new List<string>(dataProviderIds);
            }

            // One unmappable data provider (prompt, free-hand SQL element, ... -
            // see WSR 00101) makes the whole grouped call fail. Retry one by one
            // so the log identifies exactly which data provider is the problem.
            _logger.LogWarning($"Batched mapping for Document {docId} failed for {dataProviderIds.Count} data providers. Retrying each data provider individually to isolate the failure...");

            var failed = new List<string>();
            foreach (var dpId in dataProviderIds)
            {
                bool dpSuccess = await ExecuteWithSessionRetryAsync(() => TryApplyMappingAsync(docId, new List<string> { dpId }, targetUnxId));
                if (!dpSuccess)
                {
                    _logger.LogError($"Data Provider {dpId} on Document {docId} could not be converted to target {targetUnxId}. See the API error above for the SAP-reported reason (often an unmapped object, prompt, or free-hand SQL element).");
                    failed.Add(dpId);
                }
            }

            return failed;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Exception during ChangeDataSourceAsync for Doc {docId}: {ex.Message}", ex);
            return new List<string>(dataProviderIds);
        }
    }

    private async Task<bool> TryApplyMappingAsync(int docId, List<string> dataProviderIds, string targetUnxId)
    {
        string dpIdsStr = string.Join(",", dataProviderIds);
        // CUIDs can contain '+', '/' and '=' which must be escaped in a query string
        // (otherwise observed as "targetDatasourceId must not be null").
        // Parameter name as this server expects it: "targetDatasourceId" (lowercase
        // "s"); "targetDataSourceId" is ignored and fails with WSR 00100
        // "Argument targetDatasourceId must not be null".
        string dpIdsEncoded = string.Join(",", dataProviderIds.Select(Uri.EscapeDataString));
        string targetUnxIdEncoded = Uri.EscapeDataString(targetUnxId);
        string getUrl = $"raylight/v1/documents/{docId}/dataproviders/mappings?originDataProviderIds={dpIdsEncoded}&targetDatasourceId={targetUnxIdEncoded}";

        _logger.LogInformation($"Fetching mapping plan for Doc {docId}, DPs [{dpIdsStr}] -> Target {targetUnxId}...");

        // 1. GET the mapping plan proposed by the server
        var mappingNode = await _httpClient.GetFromJsonAsync<System.Text.Json.Nodes.JsonNode>(getUrl);
        if (mappingNode == null)
        {
            _logger.LogError($"Failed to fetch mapping plan for Document {docId}.");
            return false;
        }

        // 2. POST the same plan back to apply it
        _logger.LogInformation($"Applying mapping plan for Document {docId}...");
        var responseNode = await _httpClient.PostCustomContentAsync<System.Text.Json.Nodes.JsonNode>(
            $"raylight/v1/documents/{docId}/dataproviders/mappings", mappingNode.ToJsonString());

        if (responseNode != null)
        {
            _logger.LogSuccess($"Successfully changed data source for Document {docId} DPs [{dpIdsStr}].");
            return true;
        }

        return false;
    }

    private class SingleDocumentResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("document")]
        public Document? Document { get; set; }
    }

    public async Task<bool> SaveDocumentAsync(int docId)
    {
        try
        {
            _logger.LogInformation($"Saving Document {docId}...");

            var response = await ExecuteWithSessionRetryAsync(() => _httpClient.PutCustomContentAsync<SingleDocumentResponse>(
                $"raylight/v1/documents/{docId}", "{\"document\": {}}"));

            if (response != null)
            {
                _logger.LogSuccess($"Document {docId} saved successfully.");
                return true;
            }

            _logger.LogError($"Document {docId} could not be saved (see API error above).");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to save document {docId}: {ex.Message}", ex);
            return false;
        }
    }

    public async Task<bool> CloseDocumentAsync(int docId, bool documentHasUnsavedChanges = false)
    {
        try
        {
            // state "Unused" is the documented way to release a document; any
            // unsaved changes (e.g. a partly failed conversion) are discarded.
            bool ok = await ExecuteWithSessionRetryAsync(() => TryPutWithRetryAsync(docId, "{\"document\": {\"state\": \"Unused\"}}"));
            if (ok)
            {
                return true;
            }

            if (documentHasUnsavedChanges)
            {
                // The empty-body fallback below SAVES a modified document, so it
                // must not be used when a partial conversion is pending.
                _logger.LogError($"Could not close Document {docId} without saving (see API error above). Its unsaved changes stay in memory until the server's Idle Document Timeout and are NOT saved, but check this document manually.");
                return false;
            }

            // Fallback: PUT with an empty body also releases an unmodified document.
            ok = await ExecuteWithSessionRetryAsync(() => TryPutWithRetryAsync(docId, string.Empty));
            if (!ok)
            {
                _logger.LogWarning($"Server did not accept closing Document {docId} (see API error above).");
            }
            return ok;
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Could not close WebI session for Document {docId}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// PUT on /documents/{id} with one quick retry for transient connection
    /// errors. NOTE: /documents/{id} must NOT be called with DELETE - that can be
    /// routed to the generic InfoStore handler and delete the document.
    /// </summary>
    private async Task<bool> TryPutWithRetryAsync(int docId, string body)
    {
        const int maxAttempts = 2;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await _httpClient.PutForStatusAsync($"raylight/v1/documents/{docId}", body, isBestEffort: true);
            }
            catch (SapSessionExpiredException)
            {
                throw; // handled by ExecuteWithSessionRetryAsync
            }
            catch (Exception) when (attempt < maxAttempts)
            {
                await Task.Delay(300);
            }
        }
    }
}
