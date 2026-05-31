# RAG Server Development - Project Scope Summary

This document details the architecture, services, and workflow established for the RAG (Retrieval-Augmented Generation) server. The goal is to create a highly scalable, low-latency, and maintainable system for answering questions based on internal, proprietary documentation (the Obsidian Vault).

## 🎯 Project Goals & Constraints

*   **Functionality:** Ingest documents from a local network share (Obsidian Vault) $\rightarrow$ Embed $\rightarrow$ Store in Qdrant $\rightarrow$ Retrieve context $\rightarrow$ Generate answer using local LLM.
*   **Scalability:** Must support continuous monitoring for changes (Add/Update/Delete).
*   **Privacy/Locality:** 100% local stack (Ollama for both Embeddings and LLM).
*   **Performance:** Requires efficient, concurrent background processing.
*   **Design:** Must adhere to modern C# standards, SOLID principles, and leverage dependency injection heavily.

## 📚 Key Components & Services

| Service | Role | Core Responsibility | Dependencies |
| :--- | :--- | :--- | :--- |
| **`DocumentMonitorService`** | **The Watchdog/Worker** | The background hosted service that watches the network share. It detects file change events and triggers the ingestion process. | `IFileReader`, `RagIngestionEngine` |
| **`IFileReader` / `ObsidianFileReader`** | **Source Reader** | Loads content from the specified network directory, handles file path management, and calculates content hashes for change detection. | `System.IO`, `System.Security.Cryptography` |
| **`RagIngestionEngine`** | **Orchestrator** | The core workflow engine. Manages the sequence: `CheckChanges` $\rightarrow$ `Chunk` $\rightarrow$ `EmbedBatch` $\rightarrow$ `Upsert`. | `IFileReader`, `IEmbeddingGenerator`, `IVectorStore` |
| **`IEmbeddingGenerator` / `OllamaEmbeddingGenerator`** | **Vectorization** | Communicates with the local `all-MiniLM-L6-v2` endpoint on Ollama to convert text chunks into high-dimensional vectors. | `HttpClient` |
| **`IVectorStore` / `QdrantVectorStore`** | **Repository** | Manages all connections and CRUD operations with the Qdrant database. | `Qdrant.Client` |
| **`RAGQueryService`** | **Query Executor** | The final user-facing service. Executes: 1. Embedding Query $\rightarrow$ 2. Qdrant Search $\rightarrow$ 3. LLM Prompting $\rightarrow$ 4. Answer Formatting. | `IEmbeddingGenerator`, `IVectorStore`, `HttpClient` |
| **`Program.cs`** | **Bootstrap/Endpoint Mesh** | Initializes and wires all services via DI, starting `DocumentMonitorService` as a background task, and exposing the `/api/search` endpoint. | All services. |

## ⚙️ Workflow Diagrams

### 1. Ingestion Flow (Offline / Manual Trigger)
`RAG_Server.csproj` $\rightarrow$ (Triggers `/api/ingest`) $\rightarrow$ `DocumentMonitorService` $\rightarrow$ `ObsidianFileReader`:
1.  `CheckForChangesAsync` $\rightarrow$ Detects changed files/new files.
2.  `ObsidianFileReader`: Reads content.
3.  `RagIngestionEngine` $\rightarrow$ `TextChunker`: Splits content into overlapping chunks.
4.  `RagIngestionEngine` $\rightarrow$ `OllamaEmbeddingGenerator`: Sends chunk texts for vectorization.
5.  `RagIngestionEngine` $\rightarrow$ `QdrantVectorStore`: Upserts vector + metadata (`source_file`, `obsidian_links`).

### 2. Query Flow (Runtime)
`Client` $\rightarrow$ (Calls `/api/search`) $\rightarrow$ `Program.cs` $\rightarrow$ `RAGQueryService`:
1.  `IEmbeddingGenerator`: Converts `userQuery` to a query vector.
2.  `IVectorStore`: Queries Qdrant using the query vector to find the top-K context chunks.
3.  `RAGQueryService`: Assembles a comprehensive prompt (Context + Query) and calls the local LLM endpoint (`gemma:instruct`).
4.  `RAGResponse`: Returns the final generated answer and source citations.

## 🚧 Known Limitations & Future Improvements

1.  **Source Link Parsing:** The `ObsidianFileReader` currently only reads raw text. The structural parsing of Markdown features (like `[[Wikilinks]]`) is conceptual and needs a dedicated parser to fully support citation tracing.
2.  **Monitoring State Persistence:** The `DocumentMonitorService` currently uses an in-memory `Dictionary` for `lastKnownMetadata`. For a persistent service, this state must be saved to Redis or a persistent database after every successful ingestion run to survive restarts.
3.  **Resource Management:** Error handling for network outages (Qdrant/Ollama being offline) is present but could be enhanced with circuit breakers.

## ✨ Next Steps (To achieve V2.0)

1.  **Full Monitoring:** Implement persistent state tracking for `DocumentMonitorService`.
2.  **Structural Parsing:** Upgrade `IFileReader` to correctly extract and persist citation links (e.g., `obsidian_links` metadata field).
3.  **Testable Endpoints:** Create a dedicated test suite to simulate the full flow.
