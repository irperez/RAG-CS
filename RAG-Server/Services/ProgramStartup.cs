// ProgramStartup.cs
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;

namespace RAG_Server.Services;

public class ProgramStartup : BackgroundService
{
    private readonly ILogger<ProgramStartup> _logger;
    private readonly IDocumentMonitorService _monitorService;

    public ProgramStartup(ILogger<ProgramStartup> logger, IDocumentMonitorService monitorService)
    {
        _logger = logger;
        _monitorService = monitorService;
    }
    
    // Run this service instead of the primary DocumentMonitorService
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting RAG Background Services...");
        try
        {
             // Start the document monitor and keep the service alive until cancellation.
            await _monitorService.StartMonitoringAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Monitor service gracefully stopped.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Critical failure in the primary RAG background service monitor.");
        }
    }
}