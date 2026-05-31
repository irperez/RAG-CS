namespace RAG_Server.Services;

public interface IRagIngestionEngine
{
    Task<int> IngestDocumentsAsync(
        string directoryPath, 
        string collectionName, 
        int embeddingDimension, 
        int maxConcurrentTasks, 
        List<DocumentMetadata> lastKnownMetadata, 
        CancellationToken cancellationToken);
}
