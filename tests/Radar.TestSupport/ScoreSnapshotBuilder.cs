using Radar.Domain.Scoring;

namespace Radar.TestSupport;

public sealed class ScoreSnapshotBuilder
{
    private Guid _id = Guid.NewGuid();
    private Guid _companyId = Guid.NewGuid();
    private string _scoringVersion = "radar-formula-v1";
    private int _trajectoryScore = 50;
    private int _opportunityScore = 50;
    private int _attentionScore = 50;
    private int _evidenceConfidenceScore = 50;
    private int _signalVelocityScore = 50;
    private string _explanation = "test";
    private string _componentJson = "{}";
    private DateTimeOffset _windowStartUtc = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private DateTimeOffset _windowEndUtc = new(2026, 1, 31, 0, 0, 0, TimeSpan.Zero);
    private DateTimeOffset _createdAtUtc = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

    public ScoreSnapshotBuilder WithId(Guid v) { _id = v; return this; }
    public ScoreSnapshotBuilder WithCompanyId(Guid v) { _companyId = v; return this; }
    public ScoreSnapshotBuilder WithScoringVersion(string v) { _scoringVersion = v; return this; }
    public ScoreSnapshotBuilder WithTrajectoryScore(int v) { _trajectoryScore = v; return this; }
    public ScoreSnapshotBuilder WithOpportunityScore(int v) { _opportunityScore = v; return this; }
    public ScoreSnapshotBuilder WithAttentionScore(int v) { _attentionScore = v; return this; }
    public ScoreSnapshotBuilder WithEvidenceConfidenceScore(int v) { _evidenceConfidenceScore = v; return this; }
    public ScoreSnapshotBuilder WithSignalVelocityScore(int v) { _signalVelocityScore = v; return this; }
    public ScoreSnapshotBuilder WithWindow(DateTimeOffset start, DateTimeOffset end)
    {
        _windowStartUtc = start;
        _windowEndUtc = end;
        return this;
    }
    public ScoreSnapshotBuilder WithCreatedAtUtc(DateTimeOffset v) { _createdAtUtc = v; return this; }

    public CompanyScoreSnapshot Build() => new(
        Id: _id,
        CompanyId: _companyId,
        ScoringVersion: _scoringVersion,
        TrajectoryScore: _trajectoryScore,
        OpportunityScore: _opportunityScore,
        AttentionScore: _attentionScore,
        EvidenceConfidenceScore: _evidenceConfidenceScore,
        SignalVelocityScore: _signalVelocityScore,
        Explanation: _explanation,
        ComponentJson: _componentJson,
        WindowStartUtc: _windowStartUtc,
        WindowEndUtc: _windowEndUtc,
        CreatedAtUtc: _createdAtUtc);
}
