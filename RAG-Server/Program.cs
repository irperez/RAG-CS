// Program.cs
using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RAG_Server.Services;

var builder = WebApplication.CreateBuilder(args);

// Configuration
var config = builder.Configuration;
var qdrantUrl = config["Qdrant:Url"] ?? throw new InvalidOperationException("Qdrant:Url must be configured");
var collectionName = config["Qdrant:CollectionName"] ?? throw new InvalidOperationException("Qdrant:CollectionName must be configured");
const string LLM_API_URL = "http://localhost:11434";
const string LLM_MODEL = "gemma:instruct";
const string DOCUMENT_SOURCE_PATH = @"\\HOME-NAS\obsidian\Ivans Vault\";
const int MAX_CONCURRENT_TASKS = 5;

// Services
builder.Services.AddSingleton<IFileReader>(sp => new ObsidianFileReader(DOCUMENT_SOURCE_PATH));
builder.Services.AddSingleton<IEmbeddingGenerator>(sp => new OllamaEmbeddingGenerator(
    sp.GetRequiredService<HttpClient>(), 
    config["Ollama:Url"] ?? "http://localhost:11434", 
    config["Embedding:Model"] ?? "all-MiniLM-L6-v2"));
builder.Services.AddSingleton<IVectorStore>(sp => new QdrantVectorStore(qdrantUrl, collectionName));
builder.Services.AddSingleton(sp => new RAGQueryService(
    sp.GetRequiredService<IEmbeddingGenerator>(), 
    sp.GetRequiredService<IVectorStore>(), 
    sp.GetRequiredService<ILogger<RAGQueryService>>(), 
    sp.GetRequiredService<HttpClient>(), 
    LLM_API_URL, 
    LLM_MODEL));
builder.Services.AddScoped<IRagIngestionEngine, RagIngestionEngine>();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddAuthentication("Scheme").AddCookie("Scheme");
builder.Services.AddAuthorization();
builder.Services.AddHostedService<DocumentMonitorService>();

// Build app
var app = builder.Build();

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

using (var scope = app.Services.CreateScope())
{
    var vectorStore = scope.ServiceProvider.GetRequiredService<IVectorStore>();
    try
    {
        var collectionExists = await vectorStore.InitializeCollectionAsync(collectionName, 384, CancellationToken.None);
        if (!collectionExists)
        {
            app.Logger.LogError("=== FATAL RAG ERROR ===");
            app.Logger.LogError("Failed to connect and initialize Qdrant collection.");
        }
        else
        {
            app.Logger.LogInformation("=== RAG System Online ===");
            app.Logger.LogInformation("Qdrant collection '{CollectionName}' initialized successfully.", collectionName);
        }
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Failed to initialize Qdrant collection.");
    }
}

// Blazor routing
app.MapRazorComponents<RAG_Server.Components.App>().AddInteractiveServerRenderMode();

// API Endpoints
app.MapPost("/api/ingest", async (IRagIngestionEngine ingestionEngine, 
    ILogger<Program> logger, 
    CancellationToken cancellationToken) =>
{
    var totalChunks = await ingestionEngine.IngestDocumentsAsync(
        DOCUMENT_SOURCE_PATH, 
        collectionName, 
        384, 
        MAX_CONCURRENT_TASKS, 
        new List<DocumentMetadata>(), 
        cancellationToken
    );
    return Results.Ok(new { Success = true, Message = "Ingestion started.", ChunksProcessed = totalChunks });
});

app.MapGet("/api/search", async ([FromServices] IRagQueryService ragService, 
    ILogger<Program> logger, 
    [FromQuery] string query, 
    CancellationToken cancellationToken) =>
{
    logger.LogInformation($"Search query received: {query}");
    
    if (string.IsNullOrWhiteSpace(query))
    {
        return Results.BadRequest("A search query is required.");
    }

    try
    {
        var response = await ragService.QueryAsync(query, cancellationToken);
        return Results.Ok(new { Answer = response.GeneratedAnswer, Sources = response.Sources });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error during RAG query.");
        return Results.StatusCode(StatusCodes.Status500InternalServerError);
    }
});

app.MapGet("/api", () => Results.Content(
    @"<h1>RAG Server API Operational</h1>
<p>Start Ingestion: <a href='/api/ingest'>/api/ingest</a></p>
<p>Query RAG System: <a href='/api/search?query=hello'>/api/search?query=hello</a></p>",
    "text/html; charset=utf-8"
));

app.Run();
