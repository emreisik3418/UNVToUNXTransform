using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
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

    private void PrepareHttpClient()
    {
        try
        {
            var token = _authService.GetToken();
            _httpClient.SetAuthorizationHeader(token);
        }
        catch (InvalidOperationException)
        {
            _logger.LogError("Cannot prepare HttpClient: User is not logged on.");
            throw;
        }
    }

    /// <summary>
    /// Runs an operation and, if the SAP BO session has expired (HTTP 401/403),
    /// transparently re-authenticates once and retries the operation instead of
    /// failing outright or forcing a blind, scheduled re-logon.
    /// </summary>
    private async Task<T> ExecuteWithSessionRetryAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation();
        }
        catch (SapSessionExpiredException)
        {
            bool reLoggedOn = await _authService.ReLogonAsync();
            if (!reLoggedOn)
            {
                throw;
            }
            PrepareHttpClient();
            return await operation();
        }
    }

    public async Task<List<DataSourceMapping>> GetDocumentsWithUnvDataSourcesAsync(ISet<string> targetSourceUnvIds)
    {
        try 
        {
            PrepareHttpClient();
        }
        catch (InvalidOperationException)
        {
            return new List<DataSourceMapping>();
        }

        _logger.LogInformation("Fetching list of all WebI documents using CMS Query...");
        var documents = new List<DataSourceMapping>();

        // Using CMS Query to fetch all WebI documents efficiently
        // SI_INSTANCE = 0 ensures we get the actual document, not history instances
        // SI_ANCESTOR restricts the scan to a specific CMS folder (and everything under it)
        // when SourceFolderId is configured.
        string queryText = "SELECT SI_ID, SI_NAME, SI_CUID FROM CI_INFOOBJECTS WHERE SI_KIND = 'Webi' AND SI_INSTANCE = 0";
        if (_appConfig.SourceFolderId.HasValue)
        {
            queryText += $" AND SI_ANCESTOR = {_appConfig.SourceFolderId.Value}";
            _logger.LogInformation($"Restricting scan to CMS folder SI_ID={_appConfig.SourceFolderId.Value} (and its subfolders).");
        }

        var cmsQuery = new CmsQueryRequest { Query = queryText };

        // Note: The endpoint for CMS query in RESTful WS is typically "/biprws/v1/cmsquery" or "/biprws/raylight/v1/cmsquery"
        // Sometimes it is just "/biprws/cmsquery".
        // Let's assume it is relative to BaseUrl "http://.../biprws/" -> "v1/cmsquery"
        // If it fails, we might need to adjust the path.
        // Common path: /biprws/v1/cmsquery

        // SAP BO's REST API defaults to 50 results per cmsquery call and silently
        // truncates anything beyond that - page through the results explicitly.
        int pageSize = _config.CmsQueryPageSize > 0 ? _config.CmsQueryPageSize : 200;
        var entries = new List<CmsEntry>();
        int page = 1;
        while (true)
        {
            var pageResponse = await ExecuteWithSessionRetryAsync(() =>
                _httpClient.PostAsJsonAsync<CmsQueryResponse>($"v1/cmsquery?page={page}&pagesize={pageSize}", cmsQuery));

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

        if (entries.Count == 0)
        {
            _logger.LogWarning("No documents found using CMS query.");
            return documents;
        }

        _logger.LogInformation($"Found {entries.Count} documents. Fetching data provider details in batches...");

        int batchSize = _config.BatchSize > 0 ? _config.BatchSize : 100;
        int maxParallelism = _config.MaxParallelism > 0 ? _config.MaxParallelism : 10;

        for (int i = 0; i < entries.Count; i += batchSize)
        {
            var currentBatch = entries.Skip(i).Take(batchSize).ToList();
            int currentBatchNumber = (i / batchSize) + 1;
            int totalBatches = (int)Math.Ceiling((double)entries.Count / batchSize);

            _logger.LogInformation($"Processing Batch {currentBatchNumber}/{totalBatches} (Documents {i + 1} to {Math.Min(i + batchSize, entries.Count)})...");

            // First attempt, then (if the session expired mid-batch) exactly one retry
            // pass over the entries that failed because of it. We deliberately do NOT
            // re-logon on a fixed schedule - it's expensive and usually unnecessary;
            // we only pay for it when the server actually rejects the token.
            var pending = currentBatch;
            for (int attempt = 0; attempt < 2 && pending.Count > 0; attempt++)
            {
                var sessionExpired = 0;
                var failedEntries = new System.Collections.Concurrent.ConcurrentBag<CmsEntry>();

                using (var semaphore = new SemaphoreSlim(maxParallelism))
                {
                    var tasks = pending.Select(async entry =>
                    {
                        await semaphore.WaitAsync();
                        try
                        {
                            int docId = entry.GetId();
                            string docName = entry.SiName;

                            var dpResponse = await _httpClient.GetFromJsonAsync<DataProviderListResponse>($"raylight/v1/documents/{docId}/dataproviders");

                            bool willBeConverted = false;
                            if (dpResponse?.DataProviders?.DataProviderList != null)
                            {
                                lock (documents)
                                {
                                    foreach (var dp in dpResponse.DataProviders.DataProviderList)
                                    {
                                        documents.Add(new DataSourceMapping
                                        {
                                            DocumentId = docId,
                                            DocumentName = docName,
                                            DataProviderId = dp.Id,
                                            DataProviderName = dp.Name,
                                            SourceUnvId = dp.DataSourceId,
                                            TargetUnxId = "" // To be filled by matching logic
                                        });
                                    }
                                }

                                willBeConverted = dpResponse.DataProviders.DataProviderList
                                    .Any(dp => dp.DataSourceId != null && targetSourceUnvIds.Contains(dp.DataSourceId));
                            }

                            // Reading a document's data providers opens a WebI Processing
                            // Server session for it. Release it immediately UNLESS this
                            // document matches a loaded mapping and will be reopened for
                            // conversion later in Program.cs - closing it here would leave
                            // that later raylight/v1/documents/{docId} call with an ID whose
                            // session no longer exists (SAP WSR 00400 "does not exist").
                            // Across a full repository scan, leaving thousands of *unmatched*
                            // sessions open is what exhausts the server's connection limit
                            // (SAP error RWI 00239 / "The maximum connection limit has been
                            // reached"), so still close those.
                            if (!willBeConverted)
                            {
                                try
                                {
                                    await TryCloseDocumentSessionAsync(docId);
                                }
                                catch (Exception closeEx)
                                {
                                    _logger.LogWarning($"[Non-critical] Could not close WebI session for Document {docId} after listing - it will be released automatically by the server's own idle timeout, no action needed. Detail: {closeEx.Message}");
                                }
                            }
                        }
                        catch (SapSessionExpiredException)
                        {
                            Interlocked.Exchange(ref sessionExpired, 1);
                            failedEntries.Add(entry);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning($"Skipping document {entry.SiName} (ID: {entry.GetId()}): failed to fetch data providers - {ex.Message}");
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    });

                    await Task.WhenAll(tasks);
                }

                if (sessionExpired == 0)
                {
                    break;
                }

                _logger.LogWarning($"Session expired while fetching {failedEntries.Count} document(s) in this batch. Re-authenticating and retrying just those...");
                bool reLoggedOn = await _authService.ReLogonAsync();
                if (!reLoggedOn)
                {
                    _logger.LogError("Re-logon failed! Aborting document details fetch.");
                    return documents;
                }
                PrepareHttpClient();
                pending = failedEntries.ToList();
            }
        }

        _logger.LogSuccess($"Identified {documents.Count} data providers across checked documents.");
        return documents;
    }

    public async Task<int?> CopyDocumentAsync(int sourceDocId, string newName)
    {
        try
        {
            PrepareHttpClient();
            _logger.LogInformation($"Creating backup copy of document {sourceDocId} named '{newName}'...");
            
            // Manual JSON string construction as requested to ensure payload format is exact
            // Using System.Text.Json for value escaping to be safe, but structure is manual
            string safeName = System.Text.Json.JsonSerializer.Serialize(newName);
            string jsonPayload = $"{{\"document\": {{\"name\": {safeName}}}}}";
            
            var content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json");

            var response = await ExecuteWithSessionRetryAsync(() => _httpClient.PostCustomContentAsync<SingleDocumentResponse>(
                $"raylight/v1/documents?sourceId={sourceDocId}",
                content));

            if (response?.Document != null)
            {
                 _logger.LogSuccess($"Successfully copied document {sourceDocId} to new document ID {response.Document.Id}.");
                 return response.Document.Id;
            }
            
            _logger.LogWarning("Copy operation returned no document details.");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to copy document {sourceDocId}", ex);
            return null;
        }
    }

    private class SingleDocumentResponse 
    {
        [System.Text.Json.Serialization.JsonPropertyName("document")]
        public Document Document { get; set; }
    }

    public async Task<bool> ChangeDataSourceAsync(int docId, List<string> dataProviderIds, string targetUnxId)
    {
        if (string.IsNullOrWhiteSpace(targetUnxId))
        {
            _logger.LogError($"Target UNX ID is empty for Document {docId}, Data Providers [{string.Join(",", dataProviderIds)}]. Check the Excel mapping file for a missing 'Target UNX ID' value on the matching Source UNV ID row.");
            return false;
        }

        try
        {
            PrepareHttpClient();

            bool success = await ExecuteWithSessionRetryAsync(() => TryApplyMappingAsync(docId, dataProviderIds, targetUnxId));
            if (success || dataProviderIds.Count <= 1)
            {
                return success;
            }

            // The grouped mapping call failed. A single bad/unmappable data provider
            // (e.g. a prompt or free-hand-SQL element SAP can't auto-map - see WSR 00101)
            // is enough to make the whole batch fail even though the others are fine.
            // Retry data providers one at a time so the healthy ones still get converted
            // and the log clearly identifies which specific data provider is the problem.
            _logger.LogWarning($"Batched mapping for Document {docId} failed for {dataProviderIds.Count} data providers. Retrying each data provider individually to isolate the failure...");

            bool allSucceeded = true;
            foreach (var dpId in dataProviderIds)
            {
                bool dpSuccess = await ExecuteWithSessionRetryAsync(() => TryApplyMappingAsync(docId, new List<string> { dpId }, targetUnxId));
                if (!dpSuccess)
                {
                    _logger.LogError($"Data Provider {dpId} on Document {docId} could not be converted to target {targetUnxId}. See the API error above for the SAP-reported reason (often an unmapped object, prompt, or free-hand SQL element).");
                    allSucceeded = false;
                }
            }

            return allSucceeded;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Exception during ChangeDataSourceAsync for Doc {docId}: {ex.Message}", ex);
            return false;
        }
    }

    private async Task<bool> TryApplyMappingAsync(int docId, List<string> dataProviderIds, string targetUnxId)
    {
        if (string.IsNullOrWhiteSpace(targetUnxId))
        {
            _logger.LogError($"Target UNX ID is empty for Document {docId}, Data Providers [{string.Join(",", dataProviderIds)}]. Check the Excel mapping file for a missing 'Target UNX ID' value on the matching Source UNV ID row.");
            return false;
        }

        // 1. GET Request: Fetch Mapping Plan
        // Endpoint: raylight/v1/documents/{docId}/dataproviders/mappings?originDataProviderIds={ids}&targetDataSourceId={unxId}

        string dpIdsStr = string.Join(",", dataProviderIds);
        // SAP CUIDs commonly contain characters such as '+', '/' and '=' that are
        // not safe to place in a query string unescaped - doing so can truncate or
        // mangle the value server-side (observed as "targetDatasourceId must not be null").
        string dpIdsEncoded = string.Join(",", dataProviderIds.Select(Uri.EscapeDataString));
        string targetUnxIdEncoded = Uri.EscapeDataString(targetUnxId);
        string getUrl = $"raylight/v1/documents/{docId}/dataproviders/mappings?originDataProviderIds={dpIdsEncoded}&targetDataSourceId={targetUnxIdEncoded}";

        _logger.LogInformation($"Fetching mapping plan for Doc {docId}, DPs [{dpIdsStr}] -> Target {targetUnxId}...");

        // We use JsonNode/JsonObject to handle the dynamic structure of the mapping plan
        var mappingNode = await _httpClient.GetFromJsonAsync<System.Text.Json.Nodes.JsonNode>(getUrl);

        if (mappingNode == null)
        {
            _logger.LogError($"Failed to fetch mapping plan for Document {docId}.");
            return false;
        }

        // 2. POST Request: Apply Mapping Plan
        // Endpoint: raylight/v1/documents/{docId}/dataproviders/mappings
        // Body: The JSON response from the GET request

        string postUrl = $"raylight/v1/documents/{docId}/dataproviders/mappings";

        // Serialize the node back to string
        string jsonBody = mappingNode.ToJsonString();
        var content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");

        _logger.LogInformation($"Applying mapping plan for Document {docId}...");

        // The response of POST is usually the result status or updated details.
        // If it succeeds (200 OK), we assume success.
        var responseNode = await _httpClient.PostCustomContentAsync<System.Text.Json.Nodes.JsonNode>(postUrl, content);

        // If we got a response (not null) and no exception was thrown by wrapper, valid.
        // Wrapper logs error on non-success status code.
        if (responseNode != null)
        {
            _logger.LogSuccess($"Successfully changed data source for Document {docId} DPs [{dpIdsStr}].");
            return true;
        }

        return false;
    }
    public async Task<bool> SaveDocumentAsync(int docId)
    {
        try
        {
            PrepareHttpClient();
            _logger.LogInformation($"Saving/Refreshing Document {docId} to persist changes...");
            
            // PUT to documents/{id} basically saves the state. 
            // We send a minimal valid payload.
            string jsonBody = "{\"document\": {}}";
            var content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");

            var response = await ExecuteWithSessionRetryAsync(() => _httpClient.PutCustomContentAsync<SingleDocumentResponse>(
                $"raylight/v1/documents/{docId}",
                content));

            if (response != null)
            {
                _logger.LogSuccess($"Document {docId} saved successfully.");
                return true;
            }
            
            return false;
        }
        catch (Exception ex)
        {
             _logger.LogError($"Failed to save document {docId}: {ex.Message}", ex);
             return false;
        }
    }

    public async Task<bool> CloseDocumentAsync(int docId)
    {
        try
        {
            PrepareHttpClient();
            var result = await ExecuteWithSessionRetryAsync(() => TryCloseDocumentSessionAsync(docId));
            return result != null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"[Non-critical] Could not close WebI session for Document {docId} - it will be released automatically by the server's own idle timeout, no action needed. Detail: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Releases a document's WebI Processing Server session ("Life Cycle
    /// Management" in the WebI RESTful SDK) without re-preparing the shared
    /// HttpClient auth header. Safe to call from within an already
    /// token-prepared batch (e.g. the parallel scan in
    /// GetDocumentsWithUnvDataSourcesAsync) without risking a concurrent header
    /// mutation race on the singleton HttpClient.
    ///
    /// IMPORTANT: /documents/{documentId} does NOT support DELETE - that is only
    /// documented for schedules and, if sent here, can be routed to the generic
    /// InfoStore handler and actually delete the CMS document. Per the SDK, the
    /// correct way to close a document (and free its Raylight session) is a PUT
    /// with a genuinely empty body: this closes an "Original" (unmodified)
    /// document, or saves-then-leaves-open a "Modified" one - never deletes it.
    /// </summary>
    private async Task<System.Text.Json.Nodes.JsonNode?> TryCloseDocumentSessionAsync(int docId)
    {
        const int maxAttempts = 2;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                // A fresh StringContent per attempt - HttpContent should not be resent
                // if a previous send already started consuming it.
                var emptyContent = new StringContent(string.Empty, System.Text.Encoding.UTF8, "application/json");
                return await _httpClient.PutCustomContentAsync<System.Text.Json.Nodes.JsonNode>(
                    $"raylight/v1/documents/{docId}", emptyContent, isBestEffort: true);
            }
            catch (SapSessionExpiredException)
            {
                throw; // let ExecuteWithSessionRetryAsync handle re-logon
            }
            catch (Exception) when (attempt < maxAttempts)
            {
                // Most failures here are a transient connection error from a burst of
                // near-simultaneous requests all opening new connections at once
                // (common right at the start of a parallel batch) - a short pause
                // and a single retry clears almost all of them.
                await Task.Delay(300);
            }
        }

        return null;
    }
}
