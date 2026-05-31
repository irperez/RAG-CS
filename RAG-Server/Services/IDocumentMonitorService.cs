// IDocumentMonitorService.cs
using System.Threading;
using System.Threading.Tasks;

namespace RAG_Server.Services;

/// <summary>
/// Service responsible for monitoring local directories for changes (add, modify, delete).
/// </summary>
public interface IDocumentMonitorService : IHostedService
{
    Task StartMonitoringAsync(CancellationToken cancellationToken);
}
