using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Options;
using UNVToUNXTransform.Configuration;
using UNVToUNXTransform.Infrastructure;
using UNVToUNXTransform.Models;

namespace UNVToUNXTransform.Services;

/// <summary>
/// Runs the migration one document at a time: open -> read data providers ->
/// (if matched) convert -> save -> close. A document is never left open while
/// other documents are processed, so the number of open WebI documents on the
/// server never exceeds MaxParallelism. The previous "scan everything first,
/// convert later" flow kept every matched document open until the conversion
/// phase reached it, which on a few hundred reports exhausted the server's
/// memory after about an hour. Because a document is converted in the same
/// session it was opened in, it is also never closed and reopened in between,
/// which is what caused WSR 00400 earlier.
/// </summary>
public class MigrationService : IMigrationService
{
    private readonly IDocumentService _docService;
    private readonly IAuthenticationService _authService;
    private readonly ILogger _logger;
    private readonly SapBoConfiguration _config;
    private readonly ApplicationConfiguration _appConfig;

    private int _closeFailures;

    public MigrationService(IDocumentService docService, IAuthenticationService authService, ILogger logger,
        IOptions<SapBoConfiguration> config, IOptions<ApplicationConfiguration> appConfig)
    {
        _docService = docService;
        _authService = authService;
        _logger = logger;
        _config = config.Value;
        _appConfig = appConfig.Value;
    }

    public async Task RunAsync(IReadOnlyList<DataSourceMapping> mappings)
    {
        var lookup = BuildLookup(mappings);
        if (lookup.Count == 0)
        {
            _logger.LogError("No usable mappings (Source UNV ID -> Target UNX ID) were loaded.");
            return;
        }

        var entries = await _docService.ListWebiDocumentsAsync();
        if (entries.Count == 0)
        {
            _logger.LogWarning("No WebI documents found.");
            return;
        }

        int batchSize = _config.BatchSize > 0 ? _config.BatchSize : 50;
        int maxParallelism = _config.MaxParallelism > 0 ? _config.MaxParallelism : 2;
        int totalBatches = (int)Math.Ceiling((double)entries.Count / batchSize);

        if (_appConfig.DryRun)
        {
            _logger.LogWarning("DRY RUN: documents will only be analysed, nothing will be changed or saved.");
        }
        _logger.LogInformation($"Processing {entries.Count} documents in {totalBatches} batch(es) of {batchSize}, {maxParallelism} in parallel.");

        var results = new ConcurrentBag<DocumentResult>();
        _closeFailures = 0;

        for (int b = 0; b < totalBatches; b++)
        {
            var batch = entries.Skip(b * batchSize).Take(batchSize).ToList();
            _logger.LogInformation("--------------------------------------------------");
            _logger.LogInformation($"Batch {b + 1}/{totalBatches} (documents {b * batchSize + 1} to {b * batchSize + batch.Count})");

            using (var semaphore = new SemaphoreSlim(maxParallelism))
            {
                var tasks = batch.Select(async entry =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        results.Add(await ProcessDocumentAsync(entry, lookup));
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });
                await Task.WhenAll(tasks);
            }

            // Between batches nothing is in flight, so this is a safe point to
            // start from a clean session - the same thing that made running the
            // migration in chunks of 100 reports work reliably.
            if (_config.RefreshSessionAfterEachBatch && b < totalBatches - 1)
            {
                if (!await _authService.RefreshSessionAsync())
                {
                    _logger.LogError("Could not log on again after the batch. Stopping; re-run the tool to continue (already converted documents no longer match a UNV and are skipped).");
                    break;
                }
            }
        }

        WriteSummary(results.ToList());
    }

    private Dictionary<string, string> BuildLookup(IReadOnlyList<DataSourceMapping> mappings)
    {
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in mappings)
        {
            if (string.IsNullOrWhiteSpace(m.SourceUnvId))
            {
                continue;
            }
            if (string.IsNullOrWhiteSpace(m.TargetUnxId))
            {
                _logger.LogWarning($"Mapping for Source UNV '{m.SourceUnvId}' has no Target UNX ID - documents using it will be skipped.");
                continue;
            }
            if (lookup.TryGetValue(m.SourceUnvId, out var existing))
            {
                if (!string.Equals(existing, m.TargetUnxId, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning($"Source UNV '{m.SourceUnvId}' is mapped more than once ('{existing}' and '{m.TargetUnxId}'). Using the first one: '{existing}'.");
                }
                continue;
            }
            lookup[m.SourceUnvId] = m.TargetUnxId;
        }
        return lookup;
    }

    private async Task<DocumentResult> ProcessDocumentAsync(CmsEntry entry, IReadOnlyDictionary<string, string> lookup)
    {
        int docId = entry.GetId();
        var result = new DocumentResult { DocumentId = docId, DocumentName = entry.SiName ?? string.Empty };
        bool hasUnsavedChanges = false;

        try
        {
            var dataProviders = await _docService.GetDataProvidersAsync(docId);
            if (dataProviders == null)
            {
                result.Status = DocumentStatus.Error;
                result.Details = "Data providers could not be read.";
                return result;
            }

            var matched = dataProviders
                .Where(dp => dp.DataSourceId != null && lookup.ContainsKey(dp.DataSourceId))
                .ToList();

            if (matched.Count == 0)
            {
                result.Status = DocumentStatus.NoMatch;
                return result;
            }

            foreach (var dp in matched)
            {
                _logger.LogSuccess($"MATCH: Document '{result.DocumentName}' (ID: {docId}) DataProvider '{dp.Name}' uses Source UNV '{dp.DataSourceId}' -> Target UNX '{lookup[dp.DataSourceId]}'.");
            }

            if (_appConfig.DryRun)
            {
                result.Status = DocumentStatus.WouldConvert;
                result.Details = $"{matched.Count} data provider(s): {string.Join(", ", matched.Select(dp => dp.Name))}";
                return result;
            }

            var failedDpIds = new List<string>();
            foreach (var targetGroup in matched.GroupBy(dp => lookup[dp.DataSourceId]))
            {
                var dpIds = targetGroup.Select(dp => dp.Id).ToList();
                _logger.LogInformation($"Document {docId}: converting {dpIds.Count} data provider(s) to {targetGroup.Key}...");
                failedDpIds.AddRange(await _docService.ChangeDataSourceAsync(docId, dpIds, targetGroup.Key));
            }

            if (failedDpIds.Count > 0)
            {
                // Do not persist a half-converted document: the changes are discarded
                // when it is closed (state "Unused"), so the report stays on its original UNVs and can be fixed and re-run.
                hasUnsavedChanges = true; // discarded when the document is closed below
                var failedNames = matched.Where(dp => failedDpIds.Contains(dp.Id)).Select(dp => dp.Name);
                result.Status = DocumentStatus.Failed;
                result.Details = $"Not converted: {string.Join(", ", failedNames)}. Document left unchanged.";
                _logger.LogError($"FAILURE: Document {docId} ({result.DocumentName}) - {result.Details}");
                return result;
            }

            if (await _docService.SaveDocumentAsync(docId))
            {
                result.Status = DocumentStatus.Converted;
                result.Details = $"{matched.Count} data provider(s) converted.";
                _logger.LogSuccess($"SUCCESS: Document {docId} ({result.DocumentName}) converted and saved.");
            }
            else
            {
                hasUnsavedChanges = true;
                result.Status = DocumentStatus.Failed;
                result.Details = "Conversion succeeded but saving failed. Document left unchanged.";
                _logger.LogError($"FAILURE: Document {docId} ({result.DocumentName}) - {result.Details}");
            }

            return result;
        }
        catch (Exception ex)
        {
            result.Status = DocumentStatus.Error;
            result.Details = ex.Message;
            _logger.LogError($"Unexpected error on Document {docId} ({result.DocumentName}): {ex.Message}", ex);
            return result;
        }
        finally
        {
            // Always release the document, whatever happened above.
            if (!await _docService.CloseDocumentAsync(docId, hasUnsavedChanges))
            {
                Interlocked.Increment(ref _closeFailures);
            }
        }
    }

    private void WriteSummary(List<DocumentResult> results)
    {
        int Count(DocumentStatus s) => results.Count(r => r.Status == s);

        _logger.LogInformation("==================================================");
        _logger.LogInformation($"Documents scanned:          {results.Count}");
        _logger.LogInformation($"No matching UNV:            {Count(DocumentStatus.NoMatch)}");
        if (_appConfig.DryRun)
        {
            _logger.LogInformation($"Would be converted:         {Count(DocumentStatus.WouldConvert)}");
        }
        else
        {
            _logger.LogSuccess($"Converted and saved:        {Count(DocumentStatus.Converted)}");
            _logger.LogInformation($"Failed (left unchanged):    {Count(DocumentStatus.Failed)}");
        }
        _logger.LogInformation($"Errors (could not process): {Count(DocumentStatus.Error)}");
        if (_closeFailures > 0)
        {
            _logger.LogWarning($"Documents that could not be closed: {_closeFailures} (the server releases them after its Idle Document Timeout).");
        }

        var reportRows = results.Where(r => r.Status != DocumentStatus.NoMatch).OrderBy(r => r.Status).ThenBy(r => r.DocumentName).ToList();
        if (reportRows.Count == 0)
        {
            return;
        }

        try
        {
            var dir = _appConfig.OutputDirectory ?? AppDomain.CurrentDomain.BaseDirectory;
            var path = Path.Combine(dir, $"MigrationReport_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            var sb = new StringBuilder();
            sb.AppendLine("DocumentId;DocumentName;Status;Details");
            foreach (var r in reportRows)
            {
                sb.AppendLine($"{r.DocumentId};{Csv(r.DocumentName)};{r.Status};{Csv(r.Details)}");
            }
            // UTF-8 with BOM so Excel shows Turkish characters correctly.
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            _logger.LogInformation($"Report written to: {path}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Could not write the report file: {ex.Message}");
        }
    }

    private static string Csv(string? value)
    {
        value ??= string.Empty;
        return value.IndexOfAny(new[] { ';', '"', '\n', '\r' }) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }

    private enum DocumentStatus { Converted, Failed, Error, WouldConvert, NoMatch }

    private class DocumentResult
    {
        public int DocumentId { get; set; }
        public string DocumentName { get; set; } = string.Empty;
        public DocumentStatus Status { get; set; }
        public string? Details { get; set; }
    }
}
