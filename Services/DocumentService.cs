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

    public DocumentService(IHttpClientWrapper httpClient, IAuthenticationService authService, ILogger logger, IOptions<SapBoConfiguration> config)
    {
        _httpClient = httpClient;
        _authService = authService;
        _logger = logger;
        _config = config.Value;
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

    public async Task<List<DataSourceMapping>> GetDocumentsWithUnvDataSourcesAsync()
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
        var cmsQuery = new CmsQueryRequest
        {
            Query = "SELECT SI_ID, SI_NAME, SI_CUID FROM CI_INFOOBJECTS WHERE SI_KIND = 'Webi' AND SI_INSTANCE = 0"
        };

        // Note: The endpoint for CMS query in RESTful WS is typically "/biprws/v1/cmsquery" or "/biprws/raylight/v1/cmsquery"
        // Sometimes it is just "/biprws/cmsquery".
        // Let's assume it is relative to BaseUrl "http://.../biprws/" -> "v1/cmsquery"
        // If it fails, we might need to adjust the path. 
        // Common path: /biprws/v1/cmsquery
        
        var queryResponse = await ExecuteWithSessionRetryAsync(() => _httpClient.PostAsJsonAsync<CmsQueryResponse>("v1/cmsquery", cmsQuery));

        if (queryResponse?.Entries == null || queryResponse.Entries.Count == 0)
        {
            _logger.LogWarning("No documents found using CMS query.");
            return documents;
        }

        var entries = queryResponse.Entries;
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
        // 1. GET Request: Fetch Mapping Plan
        // Endpoint: raylight/v1/documents/{docId}/dataproviders/mappings?originDataProviderIds={ids}&targetDataSourceId={unxId}

        string dpIdsStr = string.Join(",", dataProviderIds);
        string getUrl = $"raylight/v1/documents/{docId}/dataproviders/mappings?originDataProviderIds={dpIdsStr}&targetDataSourceId={targetUnxId}";

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
}
