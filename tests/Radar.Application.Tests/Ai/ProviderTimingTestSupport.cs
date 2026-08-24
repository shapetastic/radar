using Microsoft.Extensions.Logging;

namespace Radar.Application.Tests.Ai;

/// <summary>
/// A fully controllable <see cref="TimeProvider"/> for the spec-187 §7 provider-timing tests: it exposes
/// BOTH a settable wall clock (<see cref="GetUtcNow"/>, which the typing/judgment generators stamp on
/// records) and a settable MONOTONIC timestamp (<see cref="GetTimestamp"/>, which the timing measurement
/// brackets provider calls with).
/// <para>
/// <b>Why a monotonic fake at all.</b> The base <see cref="TimeProvider"/> implements
/// <see cref="GetTimestamp"/> over the real high-resolution counter, so a test that did not override it
/// could only observe latency by actually sleeping — the wall-clock dependency spec 187 §7 forbids. Here
/// <see cref="TimestampFrequency"/> is <see cref="TimeSpan.TicksPerSecond"/>, which makes
/// <c>GetElapsedTime</c> an exact tick subtraction, so a scripted 40 ms call measures 40 ms EXACTLY and
/// percentile pins are byte-stable (AD-3).
/// </para>
/// <para>
/// <see cref="Advance"/> moves both clocks (real time passing moves both), while
/// <see cref="AdvanceTimestamp"/> moves only the monotonic one — which is how a fake provider simulates
/// "this call took N ms" from inside its own invocation without disturbing the record timestamps a test is
/// asserting on.
/// </para>
/// </summary>
internal sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;
    private long _timestamp;

    public override DateTimeOffset GetUtcNow() => _now;

    public override long GetTimestamp() => _timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <summary>Advances BOTH the wall clock and the monotonic timestamp by <paramref name="delta"/>.</summary>
    public void Advance(TimeSpan delta)
    {
        _now = _now.Add(delta);
        _timestamp += delta.Ticks;
    }

    /// <summary>
    /// Advances ONLY the monotonic timestamp — the simulated cost of one provider call. Deliberately
    /// separate from <see cref="Advance"/> so a timing test can script latency without moving the
    /// <c>CreatedAtUtc</c> another test pins.
    /// </summary>
    public void AdvanceTimestamp(TimeSpan delta) => _timestamp += delta.Ticks;
}

/// <summary>
/// A log sink that keeps every entry's level, RENDERED message and exception text, so a test can assert
/// what the production logging does and — more importantly for spec 187 §7 — what it must NEVER contain:
/// model request/response text, an API key, or the value of an environment variable.
/// <para>
/// The exception is captured too, because a leak that rides <c>LogWarning(ex, …)</c> would otherwise slip
/// past a message-only assertion.
/// </para>
/// </summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message, string? Exception)> Entries { get; } = [];

    /// <summary>Every captured entry's rendered message plus exception text, for "contains no secret" sweeps.</summary>
    public IEnumerable<string> AllText =>
        Entries.Select(e => e.Message + " " + (e.Exception ?? string.Empty));

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, formatter(state, exception), exception?.ToString()));
}
