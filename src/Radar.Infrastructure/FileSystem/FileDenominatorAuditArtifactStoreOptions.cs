namespace Radar.Infrastructure.FileSystem;

/// <summary>Options for <see cref="FileDenominatorAuditArtifactStore"/> (spec 172).</summary>
public sealed class FileDenominatorAuditArtifactStoreOptions
{
    /// <summary>
    /// Root directory for the audit artifacts (default <c>data/audits</c> via the Worker's
    /// <c>Radar:AuditsDirectory</c>). A NEW root, deliberately separate from the efficacy directory, so no
    /// existing efficacy artifact can be overwritten. NOT created at construction time — the shared graceful
    /// writer creates it only when the audit actually writes, so default-off leaves no directory behind.
    /// </summary>
    public string RootDirectory { get; init; } = "data/audits";
}
