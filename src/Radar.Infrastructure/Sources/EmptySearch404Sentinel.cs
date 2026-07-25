namespace Radar.Infrastructure.Sources;

/// <summary>
/// The shared "HTTP 404 means ZERO RESULTS, not an error" plumbing for the search APIs that answer an
/// empty result set with a 404 — openFDA's documented empty-search 404 ("No matches found!") and the USPTO
/// ODP PFW Search API's 404-with-empty-body (live-verified 2026-07-25). <see cref="HttpOutcomeFetch"/>'s
/// <c>onStatus</c> hook returns the caller's own <c>TFailure</c> type, so the only way to say "this status is
/// actually a success" through that seam is to hand back a Success-typed instance and recognize it again by
/// REFERENCE after the fetch. This type owns that sentinel pair — <see cref="OnStatus"/> for the hook and
/// <see cref="Matches"/> for the post-fetch identity check — so the two readers share one implementation
/// rather than each keeping a copy (spec 129 established the mechanism; spec 134 lifted it here for the
/// patents reader). Everything the readers genuinely differ on stays with them: the sentinel's own value, and
/// what they do once they recognize it (openFDA merges a zero-clearance endpoint into the other endpoint's
/// results; the patents reader returns the zero-grant read directly).
/// <para>
/// Every OTHER non-success status still falls through to the caller's <c>onHttpError</c> mapping — this
/// special case is deliberately confined to 404.
/// </para>
/// </summary>
/// <typeparam name="TResult">
/// The reader's own result type (the <c>TFailure</c> of the <see cref="HttpOutcomeFetch"/> ladder). The
/// instance handed to the constructor MUST be the reader's SUCCESS-with-zero-results value.
/// </typeparam>
internal sealed class EmptySearch404Sentinel<TResult>
    where TResult : class
{
    private const int NotFoundStatus = 404;

    private readonly TResult _emptyResult;

    /// <param name="emptyResult">
    /// The reader's success-with-zero-results value. It is shared across every read, so it must be immutable.
    /// </param>
    public EmptySearch404Sentinel(TResult emptyResult)
    {
        ArgumentNullException.ThrowIfNull(emptyResult);

        _emptyResult = emptyResult;
    }

    /// <summary>
    /// The <see cref="HttpOutcomeFetch"/> <c>onStatus</c> hook: intercepts 404 with the sentinel (so it wins
    /// over the generic non-success → HTTP-error mapping) and defers every other status by returning
    /// <see langword="null"/>.
    /// </summary>
    public TResult? OnStatus(int status) => status == NotFoundStatus ? _emptyResult : null;

    /// <summary>
    /// Whether the value the ladder reported as a failure is in fact this sentinel — a reference-identity
    /// check, never structural equality, so a genuine empty response can never be mistaken for it.
    /// </summary>
    public bool Matches(TResult candidate) => ReferenceEquals(candidate, _emptyResult);
}
