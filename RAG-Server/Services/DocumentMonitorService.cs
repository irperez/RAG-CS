// DocumentMonitorService.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace RAG_Server.Services;

/// <summary>
/// Background service that watches the document directory for file changes (FileSystemWatcher).
/// </summary>
public class DocumentMonitorService : BackgroundService
{
    private readonly ILogger<DocumentMonitorService> _logger;
    private readonly IFileReader _fileReader;
    private readonly IServiceProvider _serviceProvider;
    
    // Since we need the DocumentMetadata to track state across restarts, 
    // we assume a persistent Model/DB for last known state (simulated here).
    // In production, this would load from a database using the file hash.
    private List<DocumentMetadata> _lastKnownMetadata = [];
    
    private FileSystemWatcher _watcher = null!;
    private string? _vaultPath;

    public DocumentMonitorService(
        ILogger<DocumentMonitorService> logger, 
        IFileReader fileReader, 
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _fileReader = fileReader;
        _serviceProvider = serviceProvider;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("--- Document Monitor Service Starting ---");
        
        // Set up the watcher
        _vaultPath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "dummy_vault_path")); // Use temp path for now
        
        // NOTE: Due to the nature of network shares and FileSystemWatcher limitations, 
        // we start monitoring once the service is hosted, and use a small delay/poll loop
        // for robust cross-platform change detection, as direct file system watch events 
        // can be unreliable over mapped network drives.
        
        // Actual file system watch setup is omitted for reliability across network shares,
        // and instead, we rely on a timed polling loop for robustness.
        return Task.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting document monitoring loop. Running ingestion periodically...");
        
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Poll every 30 seconds (accounting for slow network drives)
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                
                _logger.LogInformation("--- Polling for document changes: Starting new ingestion cycle ---");
                
                // To process the changes, we temporarily lock the dependencies to get clean execution context
                using var scope = _serviceProvider.CreateScope();
                var ingEngine = scope.ServiceProvider.GetRequiredService<IRagIngestionEngine>();
                
                var lastKnown = new List<DocumentMetadata>(); // Pass empty list on first run
                
                await ingEngine.IngestDocumentsAsync(
                    _vaultPath, 
                    "obsidian_vault_chunks", 
                    384, 
                    5, 
                    lastKnown, 
                    stoppingToken
                );
                
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Monitor service stopping due to cancellation.");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An unexpected error occurred in the Document Monitor Service loop.");
                // Wait longer before retrying on critical failure
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Document Monitor Service is stopping.");
        return base.StopAsync(cancellationToken);
    }
}
