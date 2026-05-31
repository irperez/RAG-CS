// RagIngestionEngine.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace RAG_Server.Services;

/// <summary>
/// Orchestrates the full RAG ingestion pipeline: Read -> Check Changes -> Chunk -> Embed -> Store.
/// NOTE: This class now functions as the central Monitor/Worker service.
/// </summary>
public class RagIngestionEngine : IRagIngestionEngine
{
    private readonly IFileReader _fileReader;
    private readonly IEmbeddingGenerator _embeddingGenerator;
    private readonly IVectorStore _vectorStore;
    private readonly ILogger<RagIngestionEngine> _logger;

    // Constants for chunking
    private const int CHUNK_SIZE = 1000;
    private const int OVERLAP = 100;

    public RagIngestionEngine(IFileReader fileReader, IEmbeddingGenerator embeddingGenerator, IVectorStore vectorStore, ILogger<RagIngestionEngine> logger)
    {
        _fileReader = fileReader;
        _embeddingGenerator = embeddingGenerator;
        _vectorStore = vectorStore;
        _logger = logger;
    }

    /// <summary>
    /// Runs the full pipeline to ingest/update documents based on file changes. 
    /// This method is triggered on explicit API calls or by a background monitor service.
    /// </summary>
    /// <param name="directoryPath">Local path to the source documents (e.g., Obsidian Vault).</param>
    /// <param name="collectionName">The name of the Qdrant collection.</param>
    /// <param name="embeddingDimension">The expected dimension size.</param>
    /// <param name="maxConcurrentTasks">Concurrency limit.</param>
    /// <param name="lastKnownMetadata">The state from the previous successful run.</param>
    /// <param name="cancellationToken">Token for cancellation.</param>
    /// <returns>Total number of chunks processed and upserted.</returns>
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

        // 2. Process relevant files
        var filePathsToProcess = changedMetadata.Where(kv => kv.Value.SourceFilePath != null).Select(kv => kv.Key).ToList();
        
        var chunkTasks = new List<Task<int>>();
        var totalProcessedChunkCount = 0;

        // 3. Setup Tasks for Parallel Processing
        foreach (var filePath in filePathsToProcess)
        {
            // We queue up tasks for all changed/new files
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
    /// Processes a single file: Reads -> Chunks -> Embed -> Store.
    /// </summary>
    private async Task<int> ProcessFileAsync(string filePath, string collectionName, int embeddingDimension, int maxConcurrentTasks, CancellationToken cancellationToken)
    {
        _logger.LogInformation($"\n--- Processing file: {Path.GetFileName(filePath)} ---");
        
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