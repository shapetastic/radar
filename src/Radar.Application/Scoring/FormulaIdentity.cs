namespace Radar.Application.Scoring;

/// <summary>
/// THE ONE definition of a formula's composed identity (spec 153): <see cref="IScoreFormula.Version"/> alone
/// when <see cref="IScoreFormula.CompositionRevision"/> is blank, otherwise
/// <c>{Version}@{CompositionRevision}</c>.
/// <para>
/// It exists because <c>ScoringEngine</c> stamps the formula identity in THREE places — the
/// <c>formulaVersion</c> field <see cref="ScoringConfigFingerprint.Compute"/> hashes,
/// <see cref="EffectiveScoringConfig.FormulaVersion"/>, and the snapshot's <c>ScoringVersion</c> — and those
/// three must agree by construction. The scoring-config store's self-verification recomputes the fingerprint
/// FROM the persisted <see cref="EffectiveScoringConfig.FormulaVersion"/>, so persisting the composed value is
/// precisely what keeps that invariant true; persisting the bare version while hashing the composed one would
/// break it silently.
/// </para>
/// <para>
/// <b><c>@</c> is the separator, deliberately.</b> It appears in no shipped
/// <see cref="ScoreFormulaVersions"/> token and in no revision token, so the composed string stays injective
/// (AD-3), and it is filename-safe — the fingerprint (not this string) names the config file, but
/// <c>ScoringVersion</c> is rendered in the weekly report and in run logs, where a separator that reads as a
/// path or a version-range operator would invite confusion.
/// </para>
/// <para>
/// A formula that declares no revision — <c>radar-formula-v8</c> and <c>radar-formula-v9</c>, which do not
/// override the default interface member — composes to its bare token, so every existing stamp, every
/// persisted config record and every pinned fingerprint is byte-identical to before this type existed.
/// </para>
/// </summary>
public static class FormulaIdentity
{
    /// <summary>
    /// The separator between a formula's structure token and its composition revision. Never appears inside
    /// either part.
    /// </summary>
    public const char RevisionSeparator = '@';

    /// <summary>
    /// The composed identity of <paramref name="formula"/>. Blank/whitespace revisions are treated as absent,
    /// so "no revision" has exactly one representation and a formula cannot accidentally stamp
    /// <c>radar-formula-vN@</c>.
    /// </summary>
    public static string Of(IScoreFormula formula)
    {
        ArgumentNullException.ThrowIfNull(formula);

        var revision = formula.CompositionRevision;
        return string.IsNullOrWhiteSpace(revision)
            ? formula.Version
            : $"{formula.Version}{RevisionSeparator}{revision.Trim()}";
    }
}
