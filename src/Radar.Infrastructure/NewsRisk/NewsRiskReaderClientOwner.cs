using Microsoft.Extensions.AI;

using Radar.Application.NewsRisk;

namespace Radar.Infrastructure.NewsRisk;

/// <summary>
/// Owns the per-reader <see cref="IChatClient"/> instances the news-risk shadow registration builds
/// outside the container (one per configured reader — they cannot be plain <c>IChatClient</c>
/// registrations without colliding with the ambient <c>AddRadarAi</c> client). Registered as the
/// factory-produced singleton behind <see cref="NewsRiskReaderSet"/>, so the ServiceProvider disposes
/// the clients on shutdown instead of leaking their handlers for the process lifetime.
/// </summary>
internal sealed class NewsRiskReaderClientOwner(
    NewsRiskReaderSet readers, IReadOnlyList<IChatClient> clients) : IDisposable
{
    public NewsRiskReaderSet Readers { get; } = readers;

    public void Dispose()
    {
        foreach (var client in clients)
        {
            client.Dispose();
        }
    }
}
