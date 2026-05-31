// IVectorStore.cs
using System.Collections.Generic;
using System.Threading.Tasks;

namespace RAG_Server.Services;

public interface IVectorStore
{
    /// <summary>
    /// Initializes the vector collection in Qdrant if it doesn't exist.
    /// </summary>
    /// <param name="collectionName">The name of the collection.</param>
    /// <param name="vectorDimension">The expected dimension of the embedded vectors.</param>
    /// <param name="cancellationToken">Token for cancellation.</param>
    /// <returns>True if initialized successfully, false otherwise.</returns>
    Task<bool> InitializeCollectionAsync(string collectionName, int vectorDimension, CancellationToken cancellationToken);

    /// <summary>
    /// Stores a chunk/document chunk and its associated vector in the database.
    /// </summary>
    /// <param name="collectionName">The target collection name.</param>
    /// <param name="vector">The embedding vector.</param>
    /// <param name="metadata">Contextual data about the chunk (source, page number, etc.).</param>
    /// <param name="cancellationToken">Token for cancellation.</param>
    /// <returns>Number of points upserted.</returns>
    Task<int> UpsertVectorAsync(string collectionName, List<float> vector, Dictionary<string, object> metadata, CancellationToken cancellationToken);

    /// <summary>
    /// Queries the vector store to find the top-K most semantically similar chunks.
    /// </summary>
    /// <param name="collectionName">The target collection name.</param>
    /// <param name="queryVector">The vector representing the user's query.</param>
    /// <param name="topK">The number of results to retrieve.</param>
    /// <param name="cancellationToken">Token for cancellation.</param>
    /// <returns">A list of source documents (metadata) and their associated scores.</returns>
    Task<List<(Dictionary<string, object> Metadata, float Score)>> SearchAsync(
        string collectionName,
        List<float> queryVector,
        uint topK,
        CancellationToken cancellationToken);
}
