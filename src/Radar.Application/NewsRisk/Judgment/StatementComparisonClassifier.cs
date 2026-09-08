using System.Text.RegularExpressions;

using Radar.Application.NewsTyping;

namespace Radar.Application.NewsRisk.Judgment;

/// <summary>
/// SPEC 214 §1 — what a supplied fact's STATEMENT can establish about direction, decided deterministically
/// before the judge sees it. The values are explicit and start at 1 so that a defaulted zero is an
/// UNDEFINED member (the strict file-store enum converter refuses to write or read it) rather than a
/// silently meaningful one.
/// </summary>
public enum NewsFactComparisonBasis
{
    /// <summary>The statement itself states a comparison or trend (a prior value, a change, a record, a beat/miss) — with or without a number.</summary>
    StatedComparison = 1,

    /// <summary>A quantified stock or flow metric stated as a LEVEL with no comparison marker: a balance such as backlog, cash, debt, headcount, or a bare figure on an earnings statement. Establishes no direction by itself.</summary>
    LevelOnly = 2,

    /// <summary>A recognised business EVENT (an order, award, contract, approval, launch, financing, listing, recall, lawsuit) — with or without a number. Carries direction without needing a comparison.</summary>
    Event = 3,

    /// <summary>None of the above: the statement neither compares, nor quantifies a metric, nor names an event.</summary>
    NotQuantified = 4,
}

/// <summary>
/// SPEC 214 §1 — the pure, static, closed-table, PRECEDENCE-ORDERED classifier (<c>comparison-basis-v1</c>)
/// applied at judge-INPUT time to each supplied family's representative statement. It exists because on
/// 2026-09-07 the judge read Argan's "backlog hits $2.5B" as evidence of an IMPROVING trajectory when the
/// backlog had fallen 14% over the year: the statement was a LEVEL, and nothing told the judge that a level
/// establishes no direction. No I/O, no clock, no configuration; the same statement and event types always
/// yield the same basis (AD-3).
/// <para>
/// <b>Precedence — the first matching rule wins:</b>
/// <list type="number">
/// <item><see cref="NewsFactComparisonBasis.StatedComparison"/> — the statement contains a phrase from the
/// closed COMPARISON table (<c>record</c>, <c>all-time</c>, <c>up from</c>, <c>versus</c>, <c>grew</c>,
/// <c>fell</c>, <c>declined</c>, <c>year-over-year</c>, <c>beat</c>, <c>missed</c>, …), a directional
/// <c>up</c>/<c>down</c> immediately followed by a figure, or a <c>from X to Y</c> pattern between two
/// figures. A number is NOT required ("Backlog declined during the quarter" qualifies). A bare percent sign
/// NEVER qualifies on its own ("gross margin was 24%" is a level).</item>
/// <item><see cref="NewsFactComparisonBasis.LevelOnly"/> — a figure ATTACHED to a metric noun from the
/// closed STOCK/FLOW table (within <see cref="AttachmentWindowTokens"/> tokens of it, either order), OR any
/// figure at all on a statement whose event types include <see cref="NewsEventType.EarningsOrGuidance"/>.
/// A level outranks an event term deliberately: "wins $50M contract, backlog now $2.5B" is a level
/// statement that mentions an event, and the level is what the judge is most likely to misread.</item>
/// <item><see cref="NewsFactComparisonBasis.Event"/> — the statement contains a term from the closed EVENT
/// table AND its event types include at least one of the five EVENT-BEARING types
/// (<see cref="EventBearingTypes"/>). <b>The tie-breaker rule, pinned as part of v1's identity:</b> the term
/// names the event and the type confirms the statement is ABOUT such an event. Both are required, so "the
/// company filed its quarterly report" typed <c>EarningsOrGuidance</c> is not an event, while "the company
/// filed a lawsuit" typed <c>RegulatoryOrLegal</c> is.</item>
/// <item><see cref="NewsFactComparisonBasis.NotQuantified"/> — none of the above.</item>
/// </list>
/// </para>
/// <para>
/// <b>Every table matches WHOLE words or phrases only</b> — case-insensitive, bounded on both sides by a
/// character that is neither a word character nor a hyphen: a hyphenated compound is ONE token, so
/// <c>flat</c> never matches "flat-panel", <c>above</c> never matches "above-average" and <c>record</c>
/// never matches "record-breaking" — the hyphenated forms that DO state a comparison (<c>record-breaking</c>,
/// <c>record-setting</c>, <c>record-high</c>, <c>record-low</c>, the six <c>-than-expected</c> forms,
/// <c>above-average</c>, <c>below-average</c>, <c>year-over-year</c>, <c>all-time</c>) are listed explicitly. <c>vs</c> never matches "investors", <c>record</c> never matches "recorded",
/// <c>cut</c> never matches "cutting-edge", <c>rose</c> never matches "Rosetta" — pinned by negative tests.
/// Two entries are FIGURE-SCOPED rather than bare words, because the bare word over-included in the live
/// sample: <c>growth</c> counts only beside a figure ("growth of 12%", "12% growth" — "growth strategy" does
/// not), and <c>lift(s)</c> counts only immediately before a metric noun ("lifts revenue" — "projects lift
/// Argan" does not, which keeps the AGX backlog statement a level).
/// The three tables, the attachment window and the boundary rule ARE the classifier's identity: changing
/// any of them is <c>comparison-basis-v2</c>, because <see cref="Version"/> joins the judgment cohort key
/// and is therefore hashed into <c>ScoringConfigVersion</c> through the <c>news=</c> segment.
/// </para>
/// </summary>
public static class StatementComparisonClassifier
{
    /// <summary>The classifier's version token — joins <see cref="NewsJudgmentContract.CohortKey"/> beside <c>families=</c>.</summary>
    public const string Version = "comparison-basis-v1";

    /// <summary>How many whitespace-separated tokens may separate a metric noun from the figure attached to it (either order).</summary>
    public const int AttachmentWindowTokens = 6;

    /// <summary>
    /// The event types whose statements can carry an EVENT basis (spec 214 §1's tie-breaker set): a
    /// statement typed only as, say, <c>EarningsOrGuidance</c> or <c>MarketReaction</c> is never an event
    /// however its prose reads, because the typing already said what it is about.
    /// </summary>
    public static readonly IReadOnlySet<NewsEventType> EventBearingTypes = new HashSet<NewsEventType>
    {
        NewsEventType.ContractOrCustomerWin,
        NewsEventType.RegulatoryOrLegal,
        NewsEventType.ProductOrTechnology,
        NewsEventType.MergerAcquisitionOrStake,
        NewsEventType.FinancingOrDilution,
    };

    /// <summary>The closed comparison-phrase table (rule 1). Multi-word phrases match across any whitespace.</summary>
    private static readonly string[] ComparisonPhrases =
    [
        "record", "records", "record-breaking", "record-setting", "record-high", "record-low",
        "all-time", "all time", "highest", "lowest", "above-average", "below-average",
        "higher-than-expected", "lower-than-expected", "better-than-expected", "worse-than-expected",
        "wider-than-expected", "narrower-than-expected",
        "up from", "down from", "compared", "comparing", "comparison", "versus", "vs", "vs.",
        "increase", "increased", "increases", "increasing", "decrease", "decreased", "decreases", "decreasing",
        "grew", "grow", "grows", "growing", "rose", "rise", "rises", "rising",
        "fell", "fall", "falls", "falling", "declined", "decline", "declines", "declining",
        "higher", "lower", "wider", "narrower", "widened", "narrowed",
        "year-over-year", "year over year", "yoy", "y/y", "year-on-year", "year on year",
        "quarter-over-quarter", "quarter over quarter", "qoq", "q/q", "sequential", "sequentially",
        "prior year", "prior-year", "year ago", "year-ago", "year earlier", "last year",
        "prior quarter", "previous quarter", "prior period", "flat", "unchanged", "in line with",
        "beat", "beats", "missed", "misses", "miss", "above", "below",
        "exceeded", "exceeds", "topped", "tops", "surpassed", "surpasses", "outperformed", "crushes", "crushed",
        "doubled", "tripled", "halved", "raised", "raises", "cut", "cuts", "lowered", "lowers",
        "boosted", "trimmed", "slashed", "reaffirmed", "reaffirms", "reaffirm", "maintained",
        "improved", "improvement", "improving", "worsened", "deteriorated",
        "surged", "surges", "jumped", "jumps", "climbed", "climbs", "slipped", "slips",
        "dropped", "drops", "plunged", "plunges", "soared", "soars", "tumbled", "tumbles",
        "slumped", "slumps", "slump", "plummeted", "plummets", "sank", "sinks", "shrank", "shrinks",
        "dipped", "dips", "accelerated", "accelerates", "slowed", "slows", "decelerated",
        "reduced", "reduces", "swings to", "swung to", "swing to",
    ];

    /// <summary>The closed STOCK metric-noun table (rule 2): balances, stated at an instant.</summary>
    private static readonly string[] StockMetricNouns =
    [
        "backlog", "order book", "pipeline", "cash", "cash and investments", "cash and equivalents",
        "cash and cash equivalents", "debt", "net debt", "headcount", "employees", "market cap",
        "market capitalization", "market capitalisation", "shares outstanding", "assets", "total assets",
        "book value", "capacity", "fleet", "stores", "subscribers", "users",
    ];

    /// <summary>The closed FLOW metric-noun table (rule 2): amounts over a period.</summary>
    private static readonly string[] FlowMetricNouns =
    [
        "revenue", "revenues", "sales", "net income", "net loss", "loss", "earnings", "eps",
        "earnings per share", "margin", "margins", "gross margin", "operating margin", "operating income",
        "operating profit", "profit", "net profit", "gross profit", "ebitda", "cash flow", "free cash flow",
        "bookings",
    ];

    /// <summary>The closed EVENT term table (rule 3): verbs and nouns that name an order, award, contract, approval, launch, deal, financing, listing, recall or legal event.</summary>
    private static readonly string[] EventTerms =
    [
        "order", "orders", "award", "awarded", "awards", "contract", "contracts", "customer win", "win", "wins", "won",
        "approval", "approvals", "approved", "approves", "clearance", "cleared", "clears", "authorized", "authorised",
        "launch", "launched", "launches", "acquisition", "acquisitions", "acquire", "acquired", "acquires", "merger",
        "financing", "offering", "ipo", "listing", "listed", "uplisting", "uplisted", "delisting", "delisted",
        "recall", "recalled", "lawsuit", "lawsuits", "filed", "files", "settled", "settles", "settlement",
        "agreement", "signed", "partnership", "granted", "investigation", "bankruptcy",
        "divestiture", "divested", "sale of",
    ];

    private static readonly Regex ComparisonRegex = WholeWordAlternation(ComparisonPhrases);

    /// <summary>
    /// A directional <c>up</c>/<c>down</c> immediately followed (optionally via <c>by</c>) by a figure:
    /// "revenue up 12%", "down by $3M". A bare <c>up</c>/<c>down</c> is NOT in the table — "up to $5M" and
    /// "set up" are not comparisons — so the figure is what qualifies it.
    /// </summary>
    private static readonly Regex UpDownFigureRegex = new(
        @"(?<!\w)(?:up|down)\s+(?:by\s+)?(?:about\s+|approximately\s+|roughly\s+|nearly\s+|over\s+|~)?[\$€£]?\d",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// <c>growth</c> beside a figure: "growth of 12%", "revenue growth to $5M", "12% revenue growth". The bare
    /// word is NOT in the table — "growth strategy outlined" states no comparison (live sample, 2026-09-08).
    /// </summary>
    private static readonly Regex GrowthFigureRegex = new(
        @"(?<![\w-])growth\s+(?:of\s+|rate\s+of\s+|to\s+)?(?:about\s+|approximately\s+|roughly\s+|nearly\s+|over\s+|~)?[\$€£]?\d"
            + @"|\d\s*(?:%|percent)\s+(?:\w+\s+){0,2}growth(?![\w-])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// <c>lift</c>/<c>lifts</c>/<c>lifted</c> before a metric noun with at most TWO qualifiers between
    /// ("its", "Q2", "quarterly", "full-year", "fiscal 2026"): "lifts revenue and profit", "lifted Q2
    /// sales", "lifts its full-year outlook". Deliberately NOT the bare
    /// verb — "Power projects lift Argan … as backlog hits $2.5B" lifts a company, not a metric, and stays
    /// the level spec 214 exists for.
    /// </summary>
    private static readonly Regex LiftsMetricRegex = new(
        @"(?<![\w-])lift(?:s|ed)?\s+(?:(?:its|their|q[1-4]|quarterly|annual|full-year|fiscal\s+\d{4})\s+){0,2}"
            + @"(?:revenue|revenues|sales|profit|profits|net income|earnings|eps|margin|margins|guidance|outlook|forecast|backlog|bookings)(?![\w-])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary><c>from X to Y</c> between two figures ("from $1.2B to $1.5B", "from 24% to 27%").</summary>
    private static readonly Regex FromToRegex = new(
        @"(?<!\w)from\s+[\$€£]?\d[\d,.]*\s*(?:%|percent|million|billion|thousand|[mbk](?!\w)|x(?!\w))?\s+to\s+[\$€£]?\d",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// One figure: an optional currency sign, digits with THOUSANDS GROUPS only as separators (<c>1,200</c>,
    /// never a trailing comma — "In 2026, the company…" must yield the year <c>2026</c>, not <c>2026,</c>,
    /// so the year exclusion below can see it) and optional decimals, then an optional percent/magnitude
    /// unit. Bounded so <c>Q2</c>, <c>1H26</c> and the parts of <c>2026-01-31</c> are not figures. A bare
    /// four-digit YEAR with no currency and no unit, and a DAY-OF-MONTH (a bare figure immediately after a
    /// month name: "August 6", "June 30, 2026"), are excluded afterwards — a date is not a quantity, and
    /// the live sample showed earnings-calendar statements ("to report results on August 6") being read as
    /// levels through exactly that figure.
    /// </summary>
    private const string FigurePattern =
        @"(?<![\w.,\-/])(?<currency>[\$€£])?(?<digits>\d{1,3}(?:,\d{3})+(?:\.\d+)?|\d+(?:\.\d+)?)(?:\s?(?<unit>%|percent|million|billion|thousand|mm|bn|[mbk]|x))?(?![\w\-/])";

    /// <summary>A month name (full or three-letter, optional period) immediately before a figure marks it as a day-of-month.</summary>
    private static readonly Regex DayOfMonthRegex = new(
        @"(?<!\w)(?:jan|feb|mar|apr|may|jun|jul|aug|sep|sept|oct|nov|dec|january|february|march|april|june|july|august|september|october|november|december)\.?\s+$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex FigureRegex = new(
        FigurePattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex MetricNounRegex = WholeWordAlternation([.. StockMetricNouns, .. FlowMetricNouns]);

    private static readonly Regex EventRegex = WholeWordAlternation(EventTerms);

    private static readonly Regex WhitespaceRegex = new(
        @"\s+", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Classifies one supplied statement under the precedence above. Total: every non-null input maps to
    /// exactly one member. A null/blank statement is <see cref="NewsFactComparisonBasis.NotQuantified"/>.
    /// </summary>
    public static NewsFactComparisonBasis Classify(string? statement, IReadOnlyList<NewsEventType> eventTypes)
    {
        ArgumentNullException.ThrowIfNull(eventTypes);

        if (string.IsNullOrWhiteSpace(statement))
        {
            return NewsFactComparisonBasis.NotQuantified;
        }

        // Rule 1 — a stated comparison, with or without a number.
        if (ComparisonRegex.IsMatch(statement)
            || UpDownFigureRegex.IsMatch(statement)
            || FromToRegex.IsMatch(statement)
            || GrowthFigureRegex.IsMatch(statement)
            || LiftsMetricRegex.IsMatch(statement))
        {
            return NewsFactComparisonBasis.StatedComparison;
        }

        // Rule 2 — a level: a figure attached to a metric noun, or any figure on an earnings statement.
        var figures = Figures(statement);
        if (figures.Count > 0)
        {
            if (eventTypes.Contains(NewsEventType.EarningsOrGuidance))
            {
                return NewsFactComparisonBasis.LevelOnly;
            }

            foreach (Match noun in MetricNounRegex.Matches(statement))
            {
                foreach (var figure in figures)
                {
                    if (IsAttached(statement, noun, figure))
                    {
                        return NewsFactComparisonBasis.LevelOnly;
                    }
                }
            }
        }

        // Rule 3 — an event: the term names it AND the typing says the statement is about such an event.
        if (EventRegex.IsMatch(statement) && eventTypes.Any(EventBearingTypes.Contains))
        {
            return NewsFactComparisonBasis.Event;
        }

        return NewsFactComparisonBasis.NotQuantified;
    }

    /// <summary>Every figure in the statement, excluding bare calendar years ("in 2026") and days-of-month ("August 6") that carry no currency and no unit.</summary>
    private static List<Match> Figures(string statement)
    {
        var figures = new List<Match>();
        foreach (Match match in FigureRegex.Matches(statement))
        {
            var digits = match.Groups["digits"].Value;
            var bare = !match.Groups["currency"].Success && !match.Groups["unit"].Success;
            var bareYear = bare
                && digits.Length == 4
                && (digits.StartsWith("19", StringComparison.Ordinal)
                    || digits.StartsWith("20", StringComparison.Ordinal));
            var dayOfMonth = bare
                && !digits.Contains('.')
                && DayOfMonthRegex.IsMatch(statement.AsSpan(0, match.Index));
            if (!bareYear && !dayOfMonth)
            {
                figures.Add(match);
            }
        }

        return figures;
    }

    /// <summary>A noun and a figure are attached when at most <see cref="AttachmentWindowTokens"/> tokens separate them, in either order.</summary>
    private static bool IsAttached(string statement, Match noun, Match figure)
    {
        var (first, second) = noun.Index <= figure.Index ? (noun, figure) : (figure, noun);
        var gapStart = first.Index + first.Length;
        if (gapStart > second.Index)
        {
            return true; // overlapping/adjacent — trivially attached
        }

        var gap = statement.AsSpan(gapStart, second.Index - gapStart).Trim();
        if (gap.IsEmpty)
        {
            return true;
        }

        return WhitespaceRegex.Split(gap.ToString()).Length <= AttachmentWindowTokens;
    }

    /// <summary>
    /// The ONE whole-word/phrase alternation builder for every table: terms are regex-escaped, internal
    /// whitespace matches any whitespace run, and both ends are bounded by a character that is neither a
    /// word character nor a hyphen (or the string edge) — a hyphenated compound is one token, so no term
    /// can hit inside another word or inside a compound ("flat-panel", "above-average", "cutting-edge").
    /// </summary>
    private static Regex WholeWordAlternation(IReadOnlyList<string> terms)
    {
        var alternation = string.Join(
            "|",
            terms
                .OrderByDescending(t => t.Length) // longest first, so "cash and investments" beats "cash"
                .Select(t => Regex.Escape(t).Replace(@"\ ", @"\s+", StringComparison.Ordinal)));
        return new Regex(
            $@"(?<![\w-])(?:{alternation})(?![\w-])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }
}
