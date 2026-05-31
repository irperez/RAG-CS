// IFileReader.cs
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;

namespace RAG_Server.Services;

/// <summary>
/// Represents the metadata recorded for a processed source document chunk.
/// </summary>
public record DocumentMetadata(
    string SourceFilePath,
    DateTime LastModified,
    string ContentHash // SHA256 hash of the content for fast change detection
);

public interface IFileReader
{
    /// <summary>Reads raw text content from a source document path.</summary>
    /// <param name="filePath">The absolute path to the file.</param>
    /// <param name="cancellationToken">Token for cancellation.</param>
    /// <returns">A string containing the full text or null if reading fails.</returns>
    Task<string?> ReadFileAsync(string filePath, CancellationToken cancellationToken);

    /// <summary>
    /// Scans a directory (like the Obsidian Vault) and collects all eligible document file paths.
    /// </summary>
    /// <param name="directoryPath">The root directory to scan.</param>
    /// <param name="fileExtensions">A list of file extensions (e.g., { ".md", ".txt" }).</param>
    /// <param name="cancellationToken">Token for cancellation.</param>
    /// <returns">A list of full paths to all files found.</returns>
    Task<IEnumerable<string>> FindAllFilePathsAsync(string directoryPath, IEnumerable<string> fileExtensions, CancellationToken cancellationToken);

    /// <summary>
    /// Checks for changes (new, modified, deleted) since the last known state.
    /// </summary>
    /// <param name="directoryPath">The root directory to scan.</param>
    /// <param name="lastKnownMetadata">A list of previously indexed files and their metadata.</param>
    /// <param name="cancellationToken">Token for cancellation.</param>
    /// <returns>A dictionary of file paths that have changed or are new.</returns>
    Task<Dictionary<string, DocumentMetadata>> CheckForChangesAsync(string directoryPath, List<DocumentMetadata> lastKnownMetadata, CancellationToken cancellationToken);
}
