// TextChunker.cs
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RAG_Server.Services;

/// <summary>
/// Utility class to split large blocks of text into manageable, overlapping chunks.
/// </summary>
public static class TextChunker
{
    /// <summary>
    /// Splits a large document chunk into smaller, overlapping chunks.
    /// </summary>
    /// <param name="text">The source text.</param>
    /// <param name="chunkSize">The target size of each chunk (tokens/characters).</param>
    /// <param name="overlap">The text overlap between chunks (in characters).</param>
    /// <returns>An enumerable of text chunks.</returns>
    public static IEnumerable<string> Chunk(string text, int chunkSize, int overlap)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        var textLength = text.Length;
        int start = 0;

        while (start < textLength)
        {
            // Determine the end of the current chunk
            int end = Math.Min(start + chunkSize, textLength);
            
            // Adjust the end to be on a natural break (like a period or newline)
            // This is a simple heuristic; production RAG systems use tokenizers (e.g., from HuggingFace) for chunking.
            var chunk = text.Substring(start, end - start).Trim();
            
            if (!string.IsNullOrWhiteSpace(chunk))
            {
                yield return chunk;
            }

            // Calculate the start index for the next chunk
            if (end >= textLength)
            {
                break; // Last chunk
            }
            
            // Move the starting position forward, respecting the overlap
            start += (chunkSize - overlap);
        }
    }
}

// Extension method to simplify chunk splitting (optional but helpful)
public static class StringExtensions
{
    public static IEnumerable<string> Chunk(this string text, int chunkSize, int overlap) => TextChunker.Chunk(text, chunkSize, overlap);
}
