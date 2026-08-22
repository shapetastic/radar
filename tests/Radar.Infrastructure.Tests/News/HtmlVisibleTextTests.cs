using Radar.Infrastructure.News;

namespace Radar.Infrastructure.Tests.News;

public sealed class HtmlVisibleTextTests
{
    [Fact]
    public void ToPlainText_StripsTags_RemovesScriptStyleNavContent_DecodesEntities_CollapsesWhitespace()
    {
        const string html =
            "<html><nav>Home | About</nav><script>var hidden = \"secret\";</script>"
            + "<style>.a { color: red; }</style>"
            + "<body>  <p>Rocket&nbsp;Lab &amp; partners\n\n   announced   a <b>new</b> launch.</p></body></html>";

        var text = HtmlVisibleText.ToPlainText(html);

        Assert.Equal("Rocket Lab & partners announced a new launch.", text);
        Assert.DoesNotContain("secret", text);
        Assert.DoesNotContain("color", text);
        Assert.DoesNotContain("Home", text);
    }

    [Fact]
    public void ToPlainText_EncodedMarkupInText_DecodesToLiteralText_NeverReinterpretedAsMarkup()
    {
        // Entity decoding runs AFTER tag stripping (the versioned order), so "&lt;script&gt;" in TEXT
        // becomes the literal string rather than being removed as an element.
        Assert.Equal("<script>", HtmlVisibleText.ToPlainText("<p>&lt;script&gt;</p>"));
    }

    [Fact]
    public void ToPlainText_IsDeterministic()
    {
        const string html = "<p>a  b\tc</p><script>x</script>";
        Assert.Equal(HtmlVisibleText.ToPlainText(html), HtmlVisibleText.ToPlainText(html));
    }

    [Fact]
    public void ToPlainText_NullOrBlank_IsEmpty()
    {
        Assert.Equal(string.Empty, HtmlVisibleText.ToPlainText(null));
        Assert.Equal(string.Empty, HtmlVisibleText.ToPlainText("   "));
    }

    [Fact]
    public void Extract_AppliesTheDeclaredCharCap_WithExplicitTruncation()
    {
        var text = HtmlVisibleText.Extract("<p>" + new string('a', 100) + "</p>", maxChars: 10, out var truncated);

        Assert.True(truncated);
        Assert.Equal(new string('a', 10), text);

        var whole = HtmlVisibleText.Extract("<p>short</p>", maxChars: 10, out var notTruncated);
        Assert.False(notTruncated);
        Assert.Equal("short", whole);
    }

    [Fact]
    public void Extract_NeverSplitsASurrogatePair()
    {
        // "🚀" is a surrogate pair (2 UTF-16 code units); a cap landing mid-pair backs off by one.
        var text = HtmlVisibleText.Extract("🚀🚀", maxChars: 3, out var truncated);

        Assert.True(truncated);
        Assert.Equal("🚀", text);
    }

    [Fact]
    public void Version_IsThePinnedPersistedFormatConstant()
    {
        // Recorded on every content-fetch result and folded into the retrieval-policy identity: changing
        // the extraction rules must bump this, never silently reuse it.
        Assert.Equal("news-text-v1", HtmlVisibleText.Version);
    }
}
