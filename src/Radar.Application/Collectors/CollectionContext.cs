using Radar.Domain.Companies;

namespace Radar.Application.Collectors;

/// <summary>
/// The watch universe Radar hands every collector at collection time. Minimal for now (collectors
/// may ignore it); a company-specific collector uses <see cref="Companies"/> for company-hint
/// resolution. Kept a record so later slices can extend it (e.g. add source feeds) without breaking
/// callers.
/// </summary>
public sealed record CollectionContext(IReadOnlyList<Company> Companies);
