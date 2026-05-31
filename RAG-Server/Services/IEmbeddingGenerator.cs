// IEmbeddingGenerator.cs
using System.Collections.Generic;
using System.Threading.Tasks;

namespace RAG_Server.Services;

public interface IEmbeddingGenerator
{
    /// <summary>
    /// Generates a single embedding vector for the given text using the configured local model.
    /// </summary>
    /// <param name="text">The text content to vectorize.</param>
    /// <param name="cancellationToken">Token for cancellation.</param>
    /// <returns>A list of floating-point numbers representing the vector.</returns>
    Task<List<float>> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken);
}
