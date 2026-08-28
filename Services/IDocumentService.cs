using UNVToUNXTransform.Models;

namespace UNVToUNXTransform.Services;

public interface IDocumentService
{
    Task<List<DataSourceMapping>> GetDocumentsWithUnvDataSourcesAsync();
    Task<int?> CopyDocumentAsync(int sourceDocId, string newName);
    Task<bool> ChangeDataSourceAsync(int docId, List<string> dataProviderIds, string targetUnxId);
    Task<bool> SaveDocumentAsync(int docId);

    /// <summary>
    /// Releases the WebI Processing Server session for a document that was
    /// opened by any raylight/v1/documents/{id}/... call. Must be called once
    /// a document is no longer needed, otherwise sessions accumulate until the
    /// server's connection limit is hit (SAP error RWI 00239).
    /// </summary>
    Task<bool> CloseDocumentAsync(int docId);
}
