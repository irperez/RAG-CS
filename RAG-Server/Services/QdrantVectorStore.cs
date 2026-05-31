// QdrantVectorStore.cs
using Qdrant.Client;
using Qdrant.Client.Grpc;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RAG_Server.Services;

/// <summary>
/// Handles all persistent storage interactions with the Qdrant vector database.
/// </summary>
public class QdrantVectorStore : IVectorStore
{
    private readonly QdrantClient _client;
    private readonly string _collectionName;

    public QdrantVectorStore(string qdrantUrl, string collectionName)
    {
        var uri = new System.Uri(qdrantUrl);
        _client = new QdrantClient(uri.Host, uri.Port, uri.Scheme == "https");
        _collectionName = collectionName ?? throw new ArgumentNullException(nameof(collectionName));
    }

    public async Task<bool> InitializeCollectionAsync(string collectionName, int vectorDimension, CancellationToken cancellationToken)
    {
        try
        {
            var exists = await _client.CollectionExistsAsync(collectionName, cancellationToken);
            if (!exists)
            {
                await _client.CreateCollectionAsync(
                    collectionName,
                    new VectorParams
                    {
                        Size = (uint)vectorDimension,
                        Distance = Distance.Cosine
                    },
                    timeout: System.TimeSpan.FromSeconds(30),
                    cancellationToken: cancellationToken);
                System.Console.WriteLine($"[Qdrant] Collection '{collectionName}' created.");
            }
            else
            {
                System.Console.WriteLine($"[Qdrant] Collection '{collectionName}' already exists.");
            }
            return true;
        }
        catch (System.Exception ex)
        {
            System.Console.WriteLine($"[Qdrant] Error initializing collection '{collectionName}': {ex.Message}");
            throw;
        }
    }

    public async Task<int> UpsertVectorAsync(string collectionName, List<float> vector, Dictionary<string, object> metadata, CancellationToken cancellationToken)
    {
        try
        {
            var pointStruct = new PointStruct
            {
                Id = System.Guid.NewGuid(),
                Payload = { },
                Vectors = new Vectors() { Vector = new Vector { Dense = new DenseVector() { Data = { vector } } } }
            };

            foreach (var kvp in metadata)
            {
                pointStruct.Payload[kvp.Key] = new Value { StringValue = kvp.Value?.ToString() ?? "" };
            }

            var upsertPoints = new UpsertPoints
            {
                CollectionName = collectionName,
                Wait = false
            };
            upsertPoints.Points.Add(pointStruct);

            await _client.UpsertAsync(upsertPoints, cancellationToken);
            return 1;
        }
        catch (System.Exception ex)
        {
            System.Console.WriteLine($"[Qdrant] ERROR during vector upsert: {ex.Message}");
            throw;
        }
    }

    public async Task<List<(Dictionary<string, object> Metadata, float Score)>> SearchAsync(
        string collectionName,
        List<float> queryVector,
        uint topK,
        CancellationToken cancellationToken)
    {
        try
        {
            var searchOptions = new SearchParams
            {
                HnswEf = 128,
                Exact = false,
            };

            var searchResult = await _client.SearchAsync(collectionName,
                new ReadOnlyMemory<float>(queryVector.ToArray()),
                limit: topK,
                searchParams: searchOptions,
                cancellationToken: cancellationToken);

            var results = new List<(Dictionary<string, object> Metadata, float Score)>();
            foreach (var result in searchResult)
            {
                var score = result.Score;
                var metadataDict = new Dictionary<string, object>();
                if (result.Payload != null)
                {
                    foreach (var kvp in result.Payload)
                    {
                        metadataDict[kvp.Key] = kvp.Value.StringValue ?? string.Empty;
                    }
                }
                results.Add((metadataDict, score));
            }
            return results;
        }
        catch (System.Exception ex)
        {
            System.Console.WriteLine($"[Qdrant] ERROR during search: {ex.Message}");
            throw;
        }
    }
}
