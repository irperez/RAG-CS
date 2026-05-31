// ObsidianFileReader.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;

namespace RAG_Server.Services;

public class ObsidianFileReader : IFileReader
{
    private readonly string _defaultVaultPath;

    public ObsidianFileReader(string defaultVaultPath)
    {
        _defaultVaultPath = defaultVaultPath ?? throw new ArgumentNullException(nameof(defaultVaultPath));
    }

    public async Task<string?> ReadFileAsync(string filePath, CancellationToken cancellationToken)
    {
        // For next steps: Real-world path handling must account for UNC paths and network idiosyncrasies.
        var fullPath = Path.GetFullPath(filePath);

        try
        {
            if (!File.Exists(fullPath))
            {
                return null;
            }
            return await File.ReadAllTextAsync(fullPath, Encoding.UTF8, cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Error] Failed to read file {filePath}: {ex.Message}");
            return null;
        }
    }

    public async Task<IEnumerable<string>> FindAllFilePathsAsync(string directoryPath, IEnumerable<string> fileExtensions, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directoryPath))
        {
            Console.WriteLine($"[Error] Directory not found at {directoryPath}");
            return [];
        }

        var filePaths = Directory.EnumerateFiles(
            Path.GetFullPath(directoryPath), // Use absolute path for reliability
            "*",
            SearchOption.AllDirectories
        )
        .Where(path => fileExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
        .ToList();

        return filePaths;
    }

    /// <summary>
    /// Calculates SHA256 hash for the given file content.
    /// </summary>
    private static string CalculateHash(string content)
    {
        using var sha = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(content);
        var hashBytes = sha.ComputeHash(bytes);
        return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
    }

    public async Task<Dictionary<string, DocumentMetadata>> CheckForChangesAsync(
        string directoryPath, 
        List<DocumentMetadata> lastKnownMetadata, 
        CancellationToken cancellationToken)
    {
        var currentMetadata = new Dictionary<string, DocumentMetadata>();
        var changedPaths = new Dictionary<string, DocumentMetadata>();
        var deletedPaths = new List<string>();

        // 1. Scan current directory (Find ALL current files)
        var currentFilePaths = await FindAllFilePathsAsync(directoryPath, new[] { ".md", ".txt" }, cancellationToken);

        // 2. Comparison logic
        foreach (var currentPath in currentFilePaths)
        {
            // Calculate current hash (must read the file content)
            string? content = await ReadFileAsync(currentPath, cancellationToken);
            if (string.IsNullOrEmpty(content)) continue;

            string currentHash = CalculateHash(content);
            var metadata = new DocumentMetadata(currentPath, DateTime.Now, currentHash);
            currentMetadata[currentPath] = metadata;
        }

        // 3. Detect Deletions and Populate the new state
        foreach(var lastKnown in lastKnownMetadata)
        {
            if (!currentMetadata.ContainsKey(lastKnown.SourceFilePath))
            {
                deletedPaths.Add(lastKnown.SourceFilePath);
            }
        }
        
        // Return the new state tracking for the next run
        return currentMetadata;
    }
}
