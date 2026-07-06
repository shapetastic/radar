namespace Radar.Infrastructure.Sec;

/// <summary>
/// The beneficial-ownership category a SEC Schedule 13D/13G filing maps to, derived purely from its EDGAR
/// form string. Only the four beneficial-ownership forms are in scope; anything else is
/// <see cref="NotApplicable"/> (filtered out upstream by the reader's form predicate).
/// </summary>
internal enum Sec13DGCategory
{
    /// <summary>Original <c>SC 13D</c> — an activist beneficial-ownership stake with declared intent.</summary>
    Activist13D,

    /// <summary>Original <c>SC 13G</c> — a passive &gt; 5% beneficial-ownership stake.</summary>
    Passive13G,

    /// <summary>Any <c>SC 13D/A</c> or <c>SC 13G/A</c> — an amendment (routine in v1: metadata cannot tell an increase from an exit).</summary>
    Amendment,

    /// <summary>Not a beneficial-ownership form (any non-13D/13G form) — not collected.</summary>
    NotApplicable,
}

/// <summary>
/// Deterministic classification of a single SEC Schedule 13D/13G filing, keyed on the EDGAR form string
/// (case-insensitive, trimmed). This is the single owner of the form-string → category mapping (mirrors
/// <see cref="SecForm4TransactionCode"/>): a <c>/A</c> suffix (any 13D or 13G amendment) → <see cref="Sec13DGCategory.Amendment"/>;
/// otherwise <c>SC 13D</c> → <see cref="Sec13DGCategory.Activist13D"/>, <c>SC 13G</c> → <see cref="Sec13DGCategory.Passive13G"/>;
/// anything else → <see cref="Sec13DGCategory.NotApplicable"/>. The amendment test is applied FIRST so an
/// <c>SC 13D/A</c> classifies as an amendment, not an activist stake.
/// </summary>
internal static class Sec13DGFormType
{
    /// <summary>
    /// Classifies a raw EDGAR form string. Detects a <c>/A</c> amendment by a trimmed, case-insensitive
    /// <c>EndsWith("/A")</c> test, and 13D vs 13G by a <c>StartsWith("SC 13D")</c> / <c>StartsWith("SC 13G")</c>
    /// test. A blank/unknown form → <see cref="Sec13DGCategory.NotApplicable"/>.
    /// </summary>
    public static Sec13DGCategory Classify(string? form)
    {
        if (string.IsNullOrWhiteSpace(form))
        {
            return Sec13DGCategory.NotApplicable;
        }

        var trimmed = form.Trim();
        var is13D = trimmed.StartsWith("SC 13D", StringComparison.OrdinalIgnoreCase);
        var is13G = trimmed.StartsWith("SC 13G", StringComparison.OrdinalIgnoreCase);

        if (!is13D && !is13G)
        {
            return Sec13DGCategory.NotApplicable;
        }

        // A /A amendment (of either 13D or 13G) is routine in v1 — submissions metadata alone cannot tell an
        // increase from a reduction or an exit, so this classifies BEFORE the original-form branch.
        if (trimmed.EndsWith("/A", StringComparison.OrdinalIgnoreCase))
        {
            return Sec13DGCategory.Amendment;
        }

        return is13D ? Sec13DGCategory.Activist13D : Sec13DGCategory.Passive13G;
    }

    /// <summary>
    /// Convenience predicate for the shared <see cref="SecRecentFilings.Flatten"/> form hook: true for any of
    /// the four in-scope beneficial-ownership forms (a form that classifies to anything but
    /// <see cref="Sec13DGCategory.NotApplicable"/>).
    /// </summary>
    public static bool IsBeneficialOwnershipForm(string form) =>
        Classify(form) != Sec13DGCategory.NotApplicable;
}
