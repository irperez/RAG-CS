// OllamaEmbeddingGenerator.cs
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace RAG_Server.Services;

/// <summary>
/// Generates embeddings by communicating with a local Ollama instance.
/// </summary>
public class OllamaEmbeddingGenerator : IEmbeddingGenerator
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly string _modelName;

    // Models used by Ollama need a structure to send the prompt
    private class EmbedRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("prompt")]
        public string Prompt { get; set; } = string.Empty;
    }

    // Models used by Ollama to receive the response
    private class EmbedResponse
    {
        [JsonPropertyName("embedding")]
        public List<float> Embedding { get; set; } = new List<float>();
    }

    public OllamaEmbeddingGenerator(HttpClient httpClient, string ollamaBaseUrl, string modelName)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _baseUrl = ollamaBaseUrl ?? throw new ArgumentNullException(nameof(ollamaBaseUrl));
        _modelName = modelName ?? throw new ArgumentNullException(nameof(modelName));
    }

    public async Task<List<float>> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken)
    {
        // The embedding endpoint in Ollama is usually /api/embeddings
        var requestUri = $"{_baseUrl}/api/embeddings"; 
        
        // Note: If using an old version or different endpoint, this URL might need adjustment.
        // Assuming the standard Ollama API endpoint structure.
        var request = new EmbedRequest
        {
            Model = _modelName,
            Prompt = text
        };

        try
        {
            // Use SendAsync with proper serialization settings
            var response = await _httpClient.PostAsJsonAsync(requestUri, request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new HttpRequestException($"Failed to generate embedding from Ollama API. Status: {response.StatusCode}. Details: {content}");
            }

            var embedResponse = await response.Content.ReadFromJsonAsync<EmbedResponse>(cancellationToken);

            if (embedResponse?.Embedding == null || embedResponse.Embedding.Count == 0)
            {
                throw new InvalidOperationException("Received an empty or null embedding from Ollama.");
            }

            return embedResponse.Embedding;
        }
        catch (OperationCanceledException)
        {
            throw; // Re-throw cancellation to be caught by the caller
        }
        catch (Exception ex)
        {
            // Reth로우 the exception for the caller to handle, but provide context.
            throw new InvalidOperationException($"Error calling Ollama embedding service at {_baseUrl}: {ex.Message}", ex);
        }
    }
}
