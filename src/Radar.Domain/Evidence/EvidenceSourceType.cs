namespace Radar.Domain.Evidence;

/// <summary>
/// The canonical provenance source-type for a piece of evidence: which kind of public source it came
/// from. Persisted by name. Treat this enum as <b>append-only</b> — add new members at the end and never
/// reorder or remove existing ones, so persisted values stay stable as collectors fan out. Each collector
/// declares exactly one <see cref="EvidenceSourceType"/> for the evidence it produces.
/// </summary>
public enum EvidenceSourceType
{
    Manual,
    LocalFile,
    RssFeed,
    PressRelease,
    NewsArticle,
    CompanyBlog,
    Filing,
    EarningsTranscript,
    GovernmentContract,
    JobPosting,
    Patent,
    SocialMedia,
    RegulatoryAnnouncement,   // UK RNS / regulatory news service announcements
    InsiderTransaction,       // director / insider buy-sell filings
    ConferenceMention,        // conference / event agenda appearances
    RegulatoryApproval        // FDA 510(k)/PMA device clearance/approval (first-party regulatory gate; spec 129)
}
