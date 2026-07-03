using Microsoft.Extensions.AI;

namespace Radar.Application.Ai;

/// <summary>
/// Yields the config-selected, provider-neutral <see cref="IChatClient"/>. The concrete provider SDKs
/// (Anthropic, Ollama) live in <c>Radar.Infrastructure</c> only (AD-5); Application depends solely on the
/// <c>Microsoft.Extensions.AI</c> abstraction. The provider is fixed at startup by configuration, so
/// <see cref="Create"/> takes no parameters. No consumer of the client exists yet — this is the seam that
/// later AI slices will code against.
/// </summary>
public interface IChatClientFactory
{
    /// <summary>Creates the config-selected, provider-neutral <see cref="IChatClient"/>.</summary>
    IChatClient Create();
}
