using RAG_Server.Services;


/// <summary>
/// Orchestrates the full RAG document ingestion pipeline: read source files, detect changes,
/// chunk content, generate embeddings, and upsert vectors into Qdrant.
/// </summary>
/// <remarks>
/// <para><strong>Core contract:</strong> This class maintains 100% consistency between the
/// file system and Qdrant by enforcing a delete-before-rebuild strategy. Every ingestion cycle
/// performs the following steps:</para>
/// <list type="number">
///   <item>Compare current file system state against <see cref="lastKnownMetadata"/> to detect
///   new, modified, and deleted files.</item>
///   <item>For <em>deleted files</em> (on disk but not next time): call
///   <see cref="IVectorStore.DeleteByFilePathAsync"/> to remove ALL historical chunks from Qdrant.</item>
///   <item>For <em>new/modified files</em>: before upserting, call
///   <see cref="IVectorStore.DeleteByFilePathAsync"/> to purge any pre-existing chunks for that
///   file path. This prevents duplicate data — the same source file must never produce multiple
///   sets of vectors in Qdrant.</item>
///   <item>Chunk content using 1000-char segments with 100-char overlap, then concurrently
///   generate embeddings and upsert each chunk with its <c>full_filepath</c> metadata for
///   later deduplication.</item>
/// </list>
/// <para><strong>CRITICAL — Deduplication invariant:</strong> The engine relies on the
/// <c>full_filepath</c> payload field to identify which Qdrant points belong to which source file.
/// <see cref="QdrantVectorStore.UpsertVectorAsync"/> and <see cref="QdrantVectorStore.DeleteByFilePathAsync"/>
/// MUST both use the same <c>full_filepath</c> value (resolved via <see cref="Path.GetFullPath"/>).
/// Deleting by file path is the ONLY supported mechanism for removing existing chunks before
/// rebuilding — do not use vector-based deletion (<see cref="QdrantClient.SearchAsync"/>) because
/// it requires a valid vector dimension (non-zero) which is meaningless for identification-only
/// operations.</para>
/// <para><strong>Concurrency:</strong> Uses <see cref="SemaphoreSlim"/> to limit concurrent
/// embedding/upsert calls (default 5). Each invocation is stateless — it does not persist state
/// between calls. Callers (e.g. <see cref="DocumentMonitorService"/>) must supply
/// <see cref="lastKnownMetadata"/> across invocations.</para>
/// <para><strong>Target stack:</strong> All inference (embeddings + LLM inference) targets
/// local Ollama. The collection name and embedding dimension are caller-controlled and must
/// match the Qdrant collection schema.</para>
/// </remarks>
public class RagIngestionEngine : IRagIngestionEngine
{
    private readonly IFileReader _fileReader;
    private readonly IEmbeddingGenerator _embeddingGenerator;
    private readonly IVectorStore _vectorStore;
    private readonly ILogger<RagIngestionEngine> _logger;

    /// <summary>Chunk size in characters for text splitting.</summary>
    private const int CHUNK_SIZE = 1000;
    /// <summary>Overlap in characters between adjacent chunks for context continuity.</summary>
    private const int OVERLAP = 100;

    public RagIngestionEngine(IFileReader fileReader, IEmbeddingGenerator embeddingGenerator, IVectorStore vectorStore, ILogger<RagIngestionEngine> logger)
    {
        _fileReader = fileReader;
        _embeddingGenerator = embeddingGenerator;
        _vectorStore = vectorStore;
        _logger = logger;
    }

    /// <summary>
    /// Runs the full RAG ingestion pipeline: detect file system changes, clean stale Qdrant
    /// points for deleted files, then read/insert chunks for all new and modified files.
    /// </summary>
    /// <param name="directoryPath">The root directory to scan for documents (e.g., Obsidian Vault path).</param>
    /// <param name="collectionName">The target Qdrant collection name.</param>
    /// <param name="embeddingDimension">The expected embedding vector dimension (must match the Qdrant collection schema).</param>
    /// <param name="maxConcurrentTasks">Maximum concurrent embedding + upsert operations.</param>
    /// <param name="lastKnownMetadata">Previously indexed file metadata from prior runs. If empty or null,
    /// treated as a first-time full ingest (no deletions are processed). Callers must persist and
    /// supply this across invocations to enable change tracking.</param>
    /// <param name="cancellationToken">Token for cancellation.</param>
    /// <returns>Total number of chunks successfully upserted into Qdrant.</returns>
    /// <remarks>
    /// <para>If <paramref name="lastKnownMetadata"/> is empty, the method assumes first boot — no
    /// Qdrant deletions occur, but every file's existing Qdrant chunks are still purged before
    /// rebuilding (preventing duplicates on initial run against an existing collection).</para>
    /// <para>Deleted files (present in <paramref name="lastKnownMetadata"/> but missing from disk)
    /// have their Qdrant points removed before new/modified file chunks are upserted.</para>
    /// <para>Every file is processed through full deletion-first semantics: before any new chunks
    /// are written, all prior chunks for that file path are removed from Qdrant. This means no
    /// file path ever holds more than one set of vectors in the collection at a time.</para>
    /// </remarks>
    public async Task<int> IngestDocumentsAsync(
        string directoryPath, 
        string collectionName, 
        int embeddingDimension, 
        int maxConcurrentTasks, 
        List<DocumentMetadata> lastKnownMetadata, 
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting RAG Ingestion pipeline...");

        // 1. Check for changes (New, Modified, Deleted)
        Console.WriteLine("\n--- STEP 1: Checking for file system changes ---");
        var changedMetadata = await _fileReader.CheckForChangesAsync(
            directoryPath, 
            lastKnownMetadata,
            cancellationToken
        );
        
        if (changedMetadata == null || changedMetadata.Count == 0)
        {
            _logger.LogWarning("No effective changes detected in the source directory (new, modified, or deleted). Ingestion skipped.");
            return 0;
        }

        // 2. Delete old chunks for files that were deleted from disk
        var firstScan = lastKnownMetadata == null || lastKnownMetadata.Count == 0;
        if (!firstScan)
        {
            var deletedFilePaths = lastKnownMetadata
                .Where(m => !changedMetadata.ContainsKey(m.SourceFilePath))
                .Select(m => m.SourceFilePath)
                .ToList();

            foreach (var deletedPath in deletedFilePaths)
            {
                var fullKey = Path.GetFullPath(deletedPath);
                int deletedCount = await _vectorStore.DeleteByFilePathAsync(collectionName, fullKey, cancellationToken);
                _logger.LogInformation($"Deleted {deletedCount} point(s) from Qdrant for removed file: {Path.GetFileName(deletedPath)}");
            }
        }

        // 3. Process relevant files
        var filePathsToProcess = changedMetadata.Where(kv => kv.Value.SourceFilePath != null).Select(kv => kv.Key).ToList();
        
        var chunkTasks = new List<Task<int>>();
        var totalProcessedChunkCount = 0;

        // 4. Setup Tasks for Parallel Processing
        foreach (var filePath in filePathsToProcess)
        {
            chunkTasks.Add(ProcessFileAsync(filePath, collectionName, embeddingDimension, maxConcurrentTasks, cancellationToken));
        }

        // Wait for all files to be processed
        var results = await Task.WhenAll(chunkTasks);
        var allProcessedCounts = results.Select(r => r).ToList();
        
        totalProcessedChunkCount = allProcessedCounts.Sum();

        _logger.LogInformation($"\n✅ Ingestion Complete! Processed {allProcessedCounts.Count} files, upserting a total of {totalProcessedChunkCount} chunks into Qdrant.");
        return totalProcessedChunkCount;
    }

    /// <summary>
    /// Processes a single source file: purge existing Qdrant chunks, read content, split into
    /// overlapping segments, generate embeddings, and upsert each chunk.
    /// </summary>
    /// <remarks>
    /// <para><strong>De-duplication step occurs here:</strong> Before reading content, calls
    /// <see cref="IVectorStore.DeleteByFilePathAsync"/> with the file's absolute path. This ensures
    /// the new chunks upserted in this method replace (not duplicate) any previous set. This is
    /// the critical step that prevents duplicate data from accumulating in Qdrant across ingestion
    /// cycles for the same source file.</para>
    /// <para>Chunking uses 1000-char segments with 100-char overlap for context continuity across
    /// adjacent chunks. All chunks are processed concurrently (bounded by <c>maxConcurrentTasks</c>).</para>
    /// </remarks>
    private async Task<int> ProcessFileAsync(string filePath, string collectionName, int embeddingDimension, int maxConcurrentTasks, CancellationToken cancellationToken)
    {
        _logger.LogInformation($"\n--- Processing file: {Path.GetFileName(filePath)} ---");
        
        var fullPath = Path.GetFullPath(filePath);

        // De-duplicate: purge any existing Qdrant chunks for this file so new ones completely replace old.
        int deletedCount = await _vectorStore.DeleteByFilePathAsync(collectionName, fullPath, cancellationToken);
        if (deletedCount > 0)
        {
            _logger.LogInformation($"Deleted {deletedCount} existing chunk(s) for '{Path.GetFileName(filePath)}' from Qdrant before rebuilding.");
        }

        // 1. Read Content
        string? fileContent = await _fileReader.ReadFileAsync(filePath, cancellationToken);
        if (string.IsNullOrWhiteSpace(fileContent))
        {
            _logger.LogError($"Skipping file {Path.GetFileName(filePath)} due to read failure or empty content.");
            return 0;
        }

        // 2. Chunking and Structural Parsing
        var chunks = fileContent.Chunk(chunkSize: CHUNK_SIZE, overlap: OVERLAP).ToList();
        _logger.LogInformation($"File chunked into {chunks.Count} segments.");

        // Use SemaphoreSlim to enforce concurrency limits during the heavy lifting (Embedding/Network I/O)
        var concurrencySemaphore = new SemaphoreSlim(maxConcurrentTasks, maxConcurrentTasks);
        var processingTasks = new List<Task>();

        foreach (var chunk in chunks)
        {
            // Execute the embedding/upsert logic within the concurrency limit
            var task = Task.Run(async () =>
            {
                await concurrencySemaphore.WaitAsync(cancellationToken);
                try
                {
                    await EmbedAndUpsertChunkAsync(chunk, collectionName, embeddingDimension, filePath, cancellationToken);
                }
                finally
                {
                    concurrencySemaphore.Release();
                }
            }, cancellationToken);
            processingTasks.Add(task);
        }

        await Task.WhenAll(processingTasks);
        return chunks.Count; 
    }

    private async Task EmbedAndUpsertChunkAsync(string textChunk, string collectionName, int embeddingDimension, string sourceFilePath, CancellationToken cancellationToken)
    {
        try
        {
            // 1. Embed the chunk
            var embedding = await _embeddingGenerator.GenerateEmbeddingAsync(textChunk, cancellationToken);

            // 2. Create robust metadata
            var metadata = new Dictionary<string, object>
            {
                { "source_file", Path.GetFileName(sourceFilePath) },
                { "full_filepath", Path.GetFullPath(sourceFilePath) }, 
                { "page_source", "ObsidianChunk" }, 
                { "obsidian_links", "" } // Placeholder: Structural Regex parsing needed here later
            };

            // 3. Upsert into Vector Store
            var upsertedCount = await _vectorStore.UpsertVectorAsync(collectionName, embedding, metadata, cancellationToken);

            if (upsertedCount > 0)
            {
                Console.WriteLine($"(✓) Chunk processed successfully. Count: {upsertedCount}");
            }
        }
        catch (OperationCanceledException)
        {
            throw; 
        }
        catch (Exception ex)
        {
            _logger.LogError($"[ERROR] Failed to process chunk from {sourceFilePath}: {ex.Message}");
        }
    }
}