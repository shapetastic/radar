using System.Text.RegularExpressions;
using Radar.Application.Evidence;

namespace Radar.Application.Tests.Evidence;

public class EvidenceNormalizerTests
{
    private static readonly Regex LowerHex64 = new("^[0-9a-f]{64}$");

    private readonly EvidenceNormalizer _normalizer = new();

    [Fact]
    public void Normalize_IdenticalInputs_ProduceIdenticalContentHash()
    {
        var a = _normalizer.Normalize("Title", "Some body text.");
        var b = _normalizer.Normalize("Title", "Some body text.");

        Assert.Equal(a.ContentHash, b.ContentHash);
        Assert.Equal(a.NormalizedText, b.NormalizedText);
    }

    [Fact]
    public void Normalize_TrailingWhitespaceDifferences_HashEqual()
    {
        var clean = _normalizer.Normalize("Title", "line one\nline two");
        var trailing = _normalizer.Normalize("Title", "line one   \nline two\t");

        Assert.Equal(clean.ContentHash, trailing.ContentHash);
    }

    [Fact]
    public void Normalize_LineEndingDifferences_HashEqual()
    {
        var unix = _normalizer.Normalize("Title", "line one\nline two");
        var windows = _normalizer.Normalize("Title", "line one\r\nline two");
        var carriage = _normalizer.Normalize("Title", "line one\rline two");

        Assert.Equal(unix.ContentHash, windows.ContentHash);
        Assert.Equal(unix.ContentHash, carriage.ContentHash);
    }

    [Fact]
    public void Normalize_ExtraBlankLineDifferences_HashEqual()
    {
        // Both have runs of 3+ blank lines, which collapse to a single blank line.
        var threeBlanks = _normalizer.Normalize("Title", "a\n\n\n\nb");
        var manyBlanks = _normalizer.Normalize("Title", "a\n\n\n\n\n\nb");

        Assert.Equal(threeBlanks.ContentHash, manyBlanks.ContentHash);
        Assert.Equal("a\n\nb", threeBlanks.NormalizedText);
    }

    [Fact]
    public void Normalize_DifferentBody_ProducesDifferentHash()
    {
        var a = _normalizer.Normalize("Title", "body one");
        var b = _normalizer.Normalize("Title", "body two");

        Assert.NotEqual(a.ContentHash, b.ContentHash);
    }

    [Fact]
    public void Normalize_DifferentTitleSameBody_ProducesDifferentHash()
    {
        var a = _normalizer.Normalize("Title A", "same body");
        var b = _normalizer.Normalize("Title B", "same body");

        Assert.NotEqual(a.ContentHash, b.ContentHash);
    }

    [Fact]
    public void Normalize_ContentHash_Is64LowercaseHexChars()
    {
        var result = _normalizer.Normalize("Title", "body");

        Assert.Equal(64, result.ContentHash.Length);
        Assert.Matches(LowerHex64, result.ContentHash);
    }

    [Fact]
    public void Normalize_CollapsesBlankGapsAndMultiSpaceRuns()
    {
        var result = _normalizer.Normalize("Title", "a\r\n\r\n\r\n\r\nb");

        // Three+ blank lines collapse to a single blank line.
        Assert.Equal("a\n\nb", result.NormalizedText);

        var spaced = _normalizer.Normalize("Title", "foo     bar\t\tbaz");
        Assert.Equal("foo bar baz", spaced.NormalizedText);
    }

    [Fact]
    public void Normalize_NullTitle_DoesNotThrowAndHashesDeterministically()
    {
        var a = _normalizer.Normalize(null, "body");
        var b = _normalizer.Normalize(null, "body");

        Assert.Equal(a.ContentHash, b.ContentHash);
        Assert.Matches(LowerHex64, a.ContentHash);
    }

    [Fact]
    public void Normalize_NullAndEmptyTitle_HashEqual()
    {
        var nullTitle = _normalizer.Normalize(null, "body");
        var emptyTitle = _normalizer.Normalize(string.Empty, "body");

        Assert.Equal(nullTitle.ContentHash, emptyTitle.ContentHash);
    }

    [Fact]
    public void Normalize_NullRawText_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => _normalizer.Normalize("Title", null!));
    }

    [Fact]
    public void Normalize_StripsTags_PreservesWords()
    {
        var result = _normalizer.Normalize(
            "Title",
            "<p>Acme <b>raises</b> $50M in a credit facility.</p>");

        Assert.Equal("Acme raises $50M in a credit facility.", result.NormalizedText);
    }

    [Fact]
    public void Normalize_BlockTags_DoNotWordJoin()
    {
        var result = _normalizer.Normalize(
            "Title",
            "<p>First sentence.</p><p>Second sentence.</p>");

        Assert.Equal("First sentence. Second sentence.", result.NormalizedText);
    }

    [Fact]
    public void Normalize_DecodesEntities()
    {
        var result = _normalizer.Normalize("Title", "AT&amp;T &#8211; Q1 &nbsp;update");

        // Ampersand decoded, en-dash (U+2013) decoded, &nbsp; collapsed to single space.
        Assert.Equal("AT&T – Q1 update", result.NormalizedText);
    }

    [Fact]
    public void Normalize_StripsTagsBeforeDecodingEntities()
    {
        // Source-escaped literal: tags must be stripped first, so the escaped angle
        // brackets survive to be decoded into literal text rather than deleted.
        var result = _normalizer.Normalize(
            "Title",
            "&lt;script&gt;alert(1)&lt;/script&gt;");

        Assert.Equal("<script>alert(1)</script>", result.NormalizedText);
    }

    [Fact]
    public void Normalize_DropsScriptAndStyleContents()
    {
        var result = _normalizer.Normalize(
            "Title",
            "<style>.a{color:red}</style>Hello<script>x()</script>");

        Assert.Equal("Hello", result.NormalizedText);
    }

    [Fact]
    public void Normalize_DropsScriptBlock_WithWhitespaceBeforeClosingBracket()
    {
        // Closing tags may carry whitespace before '>' (e.g. "</script >"); the block
        // (tag + body) must still be stripped, not leak its contents into the output.
        var result = _normalizer.Normalize(
            "Title",
            "<style >.a{color:red}</style >Hello<script >x()</script >");

        Assert.Equal("Hello", result.NormalizedText);
    }

    [Fact]
    public void Normalize_TitleMarkup_CleanedAndReflectedInHash()
    {
        // Two titles differing only by markup that cleans to identical text -> same hash.
        var plain = _normalizer.Normalize("AT&T News", "same body");
        var markup = _normalizer.Normalize("<b>AT&amp;T</b> News", "same body");

        Assert.Equal(plain.ContentHash, markup.ContentHash);

        // A genuinely different title -> different hash.
        var different = _normalizer.Normalize("<b>Acme</b> News", "same body");
        Assert.NotEqual(markup.ContentHash, different.ContentHash);
    }

    [Fact]
    public void Normalize_MarkupBody_CleansToSameHashAsPlainEquivalent()
    {
        var plain = _normalizer.Normalize("Title", "Acme raises $50M.");
        var markup = _normalizer.Normalize("Title", "<p>Acme <b>raises</b> $50M.</p>");

        Assert.Equal(plain.ContentHash, markup.ContentHash);
        Assert.Equal(plain.NormalizedText, markup.NormalizedText);
    }
}
