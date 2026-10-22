using UNVToUNXTransform.Models;

namespace UNVToUNXTransform.Services;

public interface IDocumentService
{
    /// <summary>
    /// Lists WebI documents via CMS query (optionally restricted to
    /// Application.SourceFolderId). Does not open any document.
    /// </summary>
    Task<List<CmsEntry>> ListWebiDocumentsAsync();

    /// <summary>
    /// Reads a document's data providers. NOTE: this opens the document in a
    /// WebI Processing Server session - the caller must call
    /// <see cref="CloseDocumentAsync"/> once it is done with the document.
    /// Returns null if the data providers could not be read.
    /// </summary>
    Task<List<DataProvider>?> GetDataProvidersAsync(int docId);

    /// <summary>
    /// Applies the mapping plan for the given data providers. Returns the IDs
    /// of the data providers that could NOT be converted (empty = all converted).
    /// </summary>
    Task<List<string>> ChangeDataSourceAsync(int docId, List<string> dataProviderIds, string targetUnxId);

    Task<bool> SaveDocumentAsync(int docId);

    /// <summary>
    /// Discards unsaved changes on an open document (state "Original"), so a
    /// partly failed conversion is not persisted when the document is closed.
    /// </summary>
    Task<bool> RevertDocumentAsync(int docId);

    /// <summary>
    /// Releases the document's WebI Processing Server session. Must be called
    /// for every document that was opened, otherwise sessions accumulate on the
    /// server (memory growth, SAP error RWI 00239).
    /// </summary>
    Task<bool> CloseDocumentAsync(int docId);
}
