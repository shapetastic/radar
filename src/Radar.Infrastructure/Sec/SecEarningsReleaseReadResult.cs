namespace Radar.Infrastructure.Sec;

/// <summary>
/// Why an EX-99.1 earnings-release read ended: a fetched-and-stripped exhibit is <see cref="Success"/>;
/// every distinct failure mode is its own value so the analyzer can tell "no earnings exhibit on this
/// filing" from "dead endpoint". A distinct <see cref="Forbidden"/> (HTTP 403) exists because that is
/// almost always a missing/invalid User-Agent, which SEC requires; a distinct <see cref="NoEarningsExhibit"/>
/// exists because a parsed index with no <c>EX-99.*</c> row is a valid (non-error) outcome the analyzer skips.
/// </summary>
internal enum SecEarningsReleaseReadOutcome
{
    Success,            // EX-99.1 (or EX-99.* fallback) fetched and stripped to plain text
    NoEarningsExhibit,  // index parsed OK but no EX-99.* document row present
    Unreachable,        // transport error (HttpRequestException — DNS, connection refused, TLS, etc.)
    HttpError,          // a non-success HTTP status (other than 403) on the index or the exhibit
    Forbidden,          // HTTP 403 on index or exhibit — almost always a missing/invalid User-Agent
    Timeout,            // the request's own HTTP deadline elapsed (TaskCanceledException, ct NOT requested)
    Malformed,          // the index page could not be parsed / had no usable document table
}

/// <summary>
/// Outcome of a single EX-99.1 earnings-release read: a success carrying the exhibit's plain text plus the
/// resolved <see cref="DocumentType"/> and <see cref="DocumentFileName"/>, or a failure carrying a short
/// advice-free <see cref="Detail"/> reason used only for logging. <see cref="PlainText"/> is non-null on
/// <see cref="SecEarningsReleaseReadOutcome.Success"/> and empty otherwise.
/// </summary>
internal sealed record SecEarningsReleaseReadResult(
    SecEarningsReleaseReadOutcome Outcome,
    string PlainText,
    string? DocumentType,
    string? DocumentFileName,
    string? Detail)
{
    public bool IsSuccess => Outcome == SecEarningsReleaseReadOutcome.Success;

    public static SecEarningsReleaseReadResult Success(string text, string docType, string fileName) =>
        new(SecEarningsReleaseReadOutcome.Success, text, docType, fileName, Detail: null);

    public static SecEarningsReleaseReadResult Failure(SecEarningsReleaseReadOutcome outcome, string detail)
    {
        if (outcome == SecEarningsReleaseReadOutcome.Success)
            throw new ArgumentException("A failure result cannot carry the Success outcome.", nameof(outcome));

        return new(outcome, string.Empty, DocumentType: null, DocumentFileName: null, detail);
    }
}
