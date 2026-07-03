using Microsoft.Extensions.AI;

namespace Radar.Infrastructure.Tests.Filings;

/// <summary>
/// A fully offline test double <see cref="IChatClient"/>: no network, no key, no Ollama. It returns a
/// scripted JSON response body (which the <c>GetResponseAsync&lt;T&gt;</c> extension deserializes into a
/// <c>FilingSentiment</c>), captures the messages it was handed so tests can assert on the prompt/length,
/// and can be told to throw a provider-style exception or honour cancellation. The analyzer only ever calls
/// the non-generic <see cref="GetResponseAsync"/>; streaming and <see cref="GetService"/> throw.
/// </summary>
internal sealed class FakeChatClient : IChatClient
{
    private readonly string _jsonResponse;
    private readonly Exception? _throw;

    public FakeChatClient(string jsonResponse, Exception? throwOnCall = null)
    {
        _jsonResponse = jsonResponse;
        _throw = throwOnCall;
    }

    /// <summary>How many times <see cref="GetResponseAsync"/> was invoked.</summary>
    public int CallCount { get; private set; }

    /// <summary>The messages captured from the last (only) call, for prompt/length assertions.</summary>
    public IReadOnlyList<ChatMessage> CapturedMessages { get; private set; } = [];

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        CapturedMessages = messages.ToList();

        cancellationToken.ThrowIfCancellationRequested();

        if (_throw is not null)
        {
            throw _throw;
        }

        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, _jsonResponse));
        return Task.FromResult(response);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null)
        => throw new NotSupportedException();

    public void Dispose()
    {
    }
}
