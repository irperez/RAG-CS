// RAGQueryService.cs
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace RAG_Server.Services;

/// <summary>
/// Executes the full Retrieval-Augmented Generation (RAG) query cycle.
/// 1. Embeds the user query.
/// 2. Retrieves relevant context chunks from Qdrant using the vector.
/// 3. Calls the local LLM (Gemma 4 endpoint) to generate an answer based on the context.
/// </summary>
public class RAGQueryService : IRagQueryService
{
    private readonly IEmbeddingGenerator _embeddingGenerator;
    private readonly IVectorStore _vectorStore;
    private readonly ILogger<RAGQueryService> _logger;
    private readonly HttpClient _llmHttpClient;
    private readonly string _llmApiBaseUrl;
    private readonly string _llmModel;

    public RAGQueryService(IEmbeddingGenerator embeddingGenerator, IVectorStore vectorStore, ILogger<RAGQueryService> logger, HttpClient llmHttpClient, string llmApiBaseUrl, string llmModel)
    {
        _embeddingGenerator = embeddingGenerator ?? throw new ArgumentNullException(nameof(embeddingGenerator));
        _vectorStore = vectorStore ?? throw new ArgumentNullException(nameof(vectorStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _llmHttpClient = llmHttpClient ?? throw new ArgumentNullException(nameof(llmHttpClient));
        _llmApiBaseUrl = llmApiBaseUrl;
        _llmModel = llmModel;
    }

    public async Task<RAGResponse> QueryAsync(string userQuery, CancellationToken cancellationToken)
    {
        _logger.LogInformation("--- Starting RAG Query Process ---");

        // 1. EMBEDDING: Convert the user query into a vector
        _logger.LogInformation("1/3. Generating query embedding...");
        var queryEmbedding = await _embeddingGenerator.GenerateEmbeddingAsync(userQuery, cancellationToken);

        // 2. RETRIEVAL: Query Qdrant to get the top K relevant context chunks
        _logger.LogInformation("2/3. Searching Qdrant for relevant context...");
        var topK = 5u;
        var searchResults = await _vectorStore.SearchAsync(
            "obsidian_vault_chunks", // Hardcoded collection name for now
            queryEmbedding, 
            topK, 
            cancellationToken);

        if (searchResults == null || searchResults.Count == 0)
        {
            return new RAGResponse(
                "I could not find any relevant context documents for your question. Please check the source material or try phrasing it differently.",
                new List<(string SourceFile, string Citation)>());
        }

        // 3. ASSEMBLY: Prepare the prompt for the LLM
        _logger.LogInformation("3/3. Formatting prompt and calling remote LLM...");
        
        // Transform search results into a readable context block
        var contextChunks = string.Join("\n\n---\n\n", searchResults.Select(r => 
            $"**Source: {r.Metadata["source_file"] ?? "Unknown"}**\nContext: {r.Metadata["content_chunk"] ?? "[Empty Chunk]"}"));

        // 4. LLM API CALL: Send the prompt to the local LLM
        var finalAnswer = await CallLocalLLMAsync(userQuery, contextChunks, cancellationToken);

        // 5. Source Tracing: Build the citation list from the retrieved metadata
        var sources = searchResults.Select(r => 
            (
                SourceFile: r.Metadata.ContainsKey("source_file") ? r.Metadata["source_file"].ToString() : "Unknown Source",
                Citation: $"This claim is based on the document chunk from: {r.Metadata["source_file"]}"
            )
        ).ToList();

        _logger.LogInformation("--- RAG Query Process Complete ---");

        return new RAGResponse(finalAnswer, sources);
    }

    /// <summary>
    /// Constructs and sends the prompt to the local Gemma 4 LLM endpoint over HTTP.
    /// </summary>
    private async Task<string> CallLocalLLMAsync(string userQuery, string context, CancellationToken cancellationToken)
    {
        // --- Prompt Engineering for RAG ---
        const string systemPrompt = @"You are an expert AI assistant that answers questions using ONLY the provided context. 
        You must cite the source document whenever you make a factual claim. 
        If the context does not contain the necessary information to answer the query, you MUST state that clearly: 'I am sorry, but the provided source documents do not contain enough information to answer this question.' Never guess.";

        var systemMessage = new { role = "system", content = systemPrompt };
        
        var userMessage = $@"
        **📚 AVAILABLE CONTEXT:**
        ---
        {context}
        ---
        
        **❓ USER QUESTION:**
        {userQuery}

        **INSTRUCTIONS:** Please answer the question. Use Markdown formatting, and cite the source document name immediately after key claims (e.g., [Source: Document Name]).";
        
        // Assuming the LLM API expects an array of messages
        var messages = new List<object> { 
            new { role = "system", content = systemPrompt },
            new { role = "user", content = userMessage }
        };

        // Note: The specific model structure (e.g., v1, v2) will depend on the Ollama API version.
        // We assume a simple /generate endpoint for maximum compatibility.
        var requestBody = new 
        {
            model = _llmModel,
            messages = messages,
            stream = false // We want the full response at once
        };


        // --- API Call ---
        try
        {
            var response = await _llmHttpClient.PostAsJsonAsync(_llmApiBaseUrl, requestBody, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new HttpRequestException($"Failed to call LLM at {_llmApiBaseUrl}. Status: {response.StatusCode}. Details: {content}");
            }

            var llmResponse = await response.Content.ReadFromJsonAsync<object>(cancellationToken);

            // The exact JSON structure changes based on the LLM wrapper. 
            // This assumes a generic response structure where the text is in a 'content' field.
            if (llmResponse is JsonElement element && element.TryGetProperty("content", out var contentProp))
            {
                return contentProp.GetString() ?? "Error: LLM response was empty.";
            }
            
            // Fallback for models that return text directly
            if (llmResponse is string finalText)
            {
                 return finalText;
            }

            throw new InvalidOperationException("Could not parse the structured response from the LLM API.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to communicate with the local LLM service.");
            return $"[SYSTEM ERROR: LLM ACCESS FAILED] Cannot generate an answer. Please ensure the local LLM service is running at {_llmApiBaseUrl} and the model '{_llmModel}' is pulled.";
        }
    }
}