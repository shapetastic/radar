using Microsoft.Extensions.AI;

using Radar.Application.NewsTyping;

namespace Radar.Infrastructure.NewsTyping;

/// <summary>
/// Owns the per-reader <see cref="IChatClient"/> instances the news-typing registration builds outside the
/// container (the spec-179 <c>NewsRiskReaderClientOwner</c> mechanism: they cannot be plain
/// <c>IChatClient</c> registrations without colliding with the ambient <c>AddRadarAi</c> client). Registered
/// as the factory-produced singleton behind <see cref="NewsTypingReaderSet"/>, so the ServiceProvider
/// disposes the clients on shutdown instead of leaking their handlers for the process lifetime.
/// </summary>
internal sealed class NewsTypingReaderClientOwner(
    NewsTypingReaderSet readers, IReadOnlyList<IChatClient> clients) : IDisposable
{
    public NewsTypingReaderSet Readers { get; } = readers;

    public void Dispose()
    {
        foreach (var client in clients)
        {
            client.Dispose();
        }
    }
}
