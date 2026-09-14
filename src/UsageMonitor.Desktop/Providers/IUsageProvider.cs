using UsageMonitor.Models;

namespace UsageMonitor.Providers;

public interface IUsageProvider
{
    string Name { get; }
    Task<UsageSnapshot> FetchAsync(HttpClient http, CancellationToken ct);
}
