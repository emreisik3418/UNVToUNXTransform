using UNVToUNXTransform.Models;

namespace UNVToUNXTransform.Services;

public interface IDocumentService
{
    /// <summary>
    /// Scans all WebI documents and lists their data providers. A document's
    /// WebI Processing Server session is closed again immediately after being
    /// listed UNLESS one of its data providers' source UNV IDs is in
    /// <paramref name="targetSourceUnvIds"/> - such a document is about to be
    /// reused for conversion (see Program.cs Phase 1/2), and closing its
    /// session here would make the later raylight/v1/documents/{id} calls
    /// fail with "WSR 00400 ... does not exist" since that ID stops being a
    /// valid resource once its session is released.
    /// </summary>
    Task<List<DataSourceMapping>> GetDocumentsWithUnvDataSourcesAsync(ISet<string> targetSourceUnvIds);
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
