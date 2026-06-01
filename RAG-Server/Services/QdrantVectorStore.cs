// QdrantVectorStore.cs
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace RAG_Server.Services;

/// <summary>
/// Handles all persistent storage and retrieval interactions with a Qdrant vector database collection.
/// </summary>
/// <remarks>
/// <para><strong>Primary use in this project:</strong> <see cref="RagIngestionEngine"/> uses this class
/// exclusively to upsert embedding vectors and to delete existing chunks by file path before rebuilding.</para>
/// <para><strong>Deduplication strategy:</strong> The engine maintains consistency by using the
/// <c>full_filepath</c> payload field as a unique identifier for all chunks belonging to a single
/// source file. Before upserting new chunks, <see cref="DeleteByFilePathAsync"/> is called to purge
/// all points matching that path. This means every source file has exactly one set of vectors in
/// Qdrant at any given time.</para>
/// <para><strong>DeleteByFilePathAsync implementation detail:</strong> This method MUST use a
/// payload-based <see cref="ScrollAsync"/> query with a <see cref="Filter"/> / <see cref="Condition"/>
/// / <see cref="FieldCondition"/> / <see cref="Match"/> structure targeting the <c>full_filepath</c>
/// field. <em>Never</em> use <see cref="SearchAsync"/> or <see cref="QdrantClient.SearchAsync"/> to
/// find points for deletion — Search requires a valid vector (non-zero dimension) which you cannot
/// provide when performing identification-only deletion. Scroll with a payload filter is the only
/// correct approach.</para>
/// <para><strong>UpsertVectorAsync:</strong> Assigns a random <see cref="Guid"/> as the Qdrant point ID
/// and stores all metadata as string-typed payload values. The <c>full_filepath</c> payload value is
/// resolved via <see cref="Path.GetFullPath"/> — callers must pass the same absolute path here and to
/// <see cref="DeleteByFilePathAsync"/> for deduplication to work correctly.</para>
/// </remarks>
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

    public async Task<int> DeleteByFilePathAsync(string collectionName, string fullFilePath, CancellationToken cancellationToken)
    {
        try
        {
            var filter = new Qdrant.Client.Grpc.Filter();
            filter.Must.Add(new Qdrant.Client.Grpc.Condition
            {
                Field = new Qdrant.Client.Grpc.FieldCondition
                {
                    Key = "full_filepath",
                    Match = new Qdrant.Client.Grpc.Match { Keyword = fullFilePath }
                }
            });

            var allPoints = new List<Qdrant.Client.Grpc.RetrievedPoint>();
            Qdrant.Client.Grpc.PointId? offset = null;

            do
            {
                var result = await _client.ScrollAsync(
                    collectionName,
                    filter: filter,
                    limit: 1000,
                    offset: offset,
                    payloadSelector: new Qdrant.Client.Grpc.WithPayloadSelector { Enable = true },
                    vectorsSelector: new Qdrant.Client.Grpc.WithVectorsSelector { Enable = false },
                    cancellationToken: cancellationToken);

                if (result.Result.Count == 0)
                    break;

                allPoints.AddRange(result.Result);
                offset = result.NextPageOffset;
            } while (offset != null);

            if (allPoints.Count == 0)
            {
                System.Console.WriteLine($"[Qdrant] No points found for '{fullFilePath}'");
                return 0;
            }

            var pointIds = allPoints
                .Where(p => p.Id.HasUuid)
                .Select(p => Guid.Parse(p.Id.Uuid))
                .ToArray();

            await _client.DeleteAsync(collectionName, pointIds, cancellationToken: cancellationToken);

            System.Console.WriteLine($"[Qdrant] Deleted {pointIds.Length} point(s) for '{fullFilePath}'");
            return pointIds.Length;
        }
        catch (System.Exception ex)
        {
            System.Console.WriteLine($"[Qdrant] ERROR during delete by filepath '{fullFilePath}': {ex.Message}");
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
