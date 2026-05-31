// IRagQueryService.cs
using System.Threading.Tasks;
using System.Collections.Generic;

namespace RAG_Server.Services;

/// <summary>
/// Core service responsible for the Retrieval-Augmented Generation (RAG) process.
/// </summary>
public interface IRagQueryService
{
    /// <summary>
    /// Runs the full RAG query lifecycle: Embed Query -> Retrieve Context -> Generate Answer.
    /// </summary>
    /// <param name="userQuery">The question posed by the user.</param>
    /// <param name="cancellationToken">Token for cancellation.</param>
    /// <returns>A summary containing the generated answer and the source documents.</returns>
    Task<RAGResponse> QueryAsync(string userQuery, CancellationToken cancellationToken);
}

/// <summary>
/// Structure to hold the final, structured response from the RAG service.
/// </summary>
public record RAGResponse(
    string GeneratedAnswer,
    List<(string SourceFile, string Citation)> Sources
);