using Radar.Application.Scoring;

namespace Radar.IntegrationTests;

/// <summary>
/// A FROZEN signal-source descriptor for the read-only paired counterfactual harnesses (spec 196 §7, spec
/// 198 §4). Those harnesses compare SCORES between two arms that differ in exactly one input; the descriptor
/// contributes only to the recorded stamp, which is identical on both arms, so freezing it keeps a read-only
/// harness from having to register a collector, an AI seam or a judgment identity it must never use.
/// <para>
/// Extracted from <c>AttentionPolicyCounterfactualTests</c>'s private copy when spec 198 added a second
/// harness (CLAUDE.md reuse-over-copy). Its value is deliberately meaningless and deliberately STABLE: a
/// second copy would let two harnesses drift into stamping different things while claiming to hold the
/// descriptor constant.
/// </para>
/// </summary>
internal sealed class ReadOnlyHarnessSourceDescriptor : ISignalSourceDescriptor
{
    public static readonly ReadOnlyHarnessSourceDescriptor Instance = new();

    public string CanonicalDescriptor() => "counterfactual-src-desc";

    public string CollectionProvenance() => "collectors=;collection=none-this-pass;";

    public IReadOnlyList<string> EnabledCollectors() => [];
}
