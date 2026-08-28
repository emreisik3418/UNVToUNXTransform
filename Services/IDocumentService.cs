using UNVToUNXTransform.Models;

namespace UNVToUNXTransform.Services;

public interface IDocumentService
{
    Task<List<DataSourceMapping>> GetDocumentsWithUnvDataSourcesAsync();
    Task<int?> CopyDocumentAsync(int sourceDocId, string newName);
    Task<bool> ChangeDataSourceAsync(int docId, List<string> dataProviderIds, string targetUnxId);
    Task<bool> SaveDocumentAsync(int docId);
}
