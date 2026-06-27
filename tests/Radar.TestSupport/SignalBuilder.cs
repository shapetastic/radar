using Radar.Domain.Signals;

namespace Radar.TestSupport;

public sealed class SignalBuilder
{
    private Guid _id = Guid.NewGuid();
    private Guid _evidenceId = Guid.NewGuid();
    private Guid? _companyId = Guid.NewGuid();
    private string _companyMention = "Acme Corp";
    private SignalType _type = SignalType.CustomerWin;
    private SignalDirection _direction = SignalDirection.Positive;
    private int _strength = 6;
    private int _novelty = 6;
    private decimal _confidence = 0.8m;
    private string _supportingExcerpt = "signed a multi-year deal";
    private string _reason = "Customer win phrase detected.";
    private SignalReviewStatus _reviewStatus = SignalReviewStatus.Pending;
    private DateTimeOffset _observedAtUtc = new(2026, 1, 15, 9, 30, 0, TimeSpan.Zero);
    private DateTimeOffset _createdAtUtc = new(2026, 1, 16, 12, 0, 0, TimeSpan.Zero);

    public SignalBuilder WithId(Guid id) { _id = id; return this; }
    public SignalBuilder WithEvidenceId(Guid v) { _evidenceId = v; return this; }
    public SignalBuilder WithCompanyId(Guid? v) { _companyId = v; return this; }
    public SignalBuilder WithCompanyMention(string v) { _companyMention = v; return this; }
    public SignalBuilder WithType(SignalType v) { _type = v; return this; }
    public SignalBuilder WithDirection(SignalDirection v) { _direction = v; return this; }
    public SignalBuilder WithStrength(int v) { _strength = v; return this; }
    public SignalBuilder WithNovelty(int v) { _novelty = v; return this; }
    public SignalBuilder WithConfidence(decimal v) { _confidence = v; return this; }
    public SignalBuilder WithSupportingExcerpt(string v) { _supportingExcerpt = v; return this; }
    public SignalBuilder WithReason(string v) { _reason = v; return this; }
    public SignalBuilder WithReviewStatus(SignalReviewStatus v) { _reviewStatus = v; return this; }
    public SignalBuilder WithObservedAtUtc(DateTimeOffset v) { _observedAtUtc = v; return this; }
    public SignalBuilder WithCreatedAtUtc(DateTimeOffset v) { _createdAtUtc = v; return this; }

    public Signal Build() => new(
        Id: _id,
        EvidenceId: _evidenceId,
        CompanyId: _companyId,
        CompanyMention: _companyMention,
        Type: _type,
        Direction: _direction,
        Strength: _strength,
        Novelty: _novelty,
        Confidence: _confidence,
        SupportingExcerpt: _supportingExcerpt,
        Reason: _reason,
        ReviewStatus: _reviewStatus,
        ObservedAtUtc: _observedAtUtc,
        CreatedAtUtc: _createdAtUtc);
}
