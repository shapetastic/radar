using Radar.Infrastructure.Sec;

namespace Radar.Infrastructure.Tests.Sec;

/// <summary>
/// SPEC 228 §1 — the shared filing-index parser resolves inline-XBRL viewer links and every row's Type column, and
/// the primary document is chosen from what SEC says (declared, then form-typed), never by position. Pinned on
/// four REAL index pages (<see cref="RealSecFilingIndexPages"/>), including the SHOO page where the pre-228 code
/// took the EX-10.1 credit agreement as "the primary document".
/// </summary>
public sealed class SecFilingIndexTableTests
{
    [Fact]
    public void Parse_RealMyrgIndex_ExtractsTheInlineViewerPrimary_WhichThePre228ParserDropped()
    {
        var rows = SecFilingIndexTable.Parse(RealSecFilingIndexPages.MyrgNoExhibits);

        var row = Assert.Single(rows);
        Assert.Equal("myrg-20260908.htm", row.FileName);
        Assert.Equal("8-K", row.DocumentType);
        Assert.Null(row.Ex99Type);
        Assert.True(row.LinkedThroughInlineViewer);

        // The pre-228 row set: nothing at all — "no parseable document table".
        Assert.Empty(SecFilingIndexTable.Parse(RealSecFilingIndexPages.MyrgNoExhibits, InlineViewerLinks.IgnoreAsBeforeSpec228));

        var selection = SecFilingIndexTable.SelectPrimaryDocument(rows, declaredPrimaryDocument: null);
        Assert.Equal("myrg-20260908.htm", selection.Row?.FileName);
        Assert.Equal(PrimaryDocumentAuthority.FormTyped, selection.Authority);
        Assert.Null(SecFilingIndexTable.SelectEarningsExhibit(rows));
    }

    [Fact]
    public void Parse_RealHzoIndex_ResolvesPrimaryAndEx991_WhereThePre228ParserSawOnlyTheExhibit()
    {
        var rows = SecFilingIndexTable.Parse(RealSecFilingIndexPages.HzoEx991Only);

        Assert.Equal(["hzo-20260629.htm", "hzo-ex99_1.htm"], rows.Select(r => r.FileName));
        Assert.Equal(["8-K", "EX-99.1"], rows.Select(r => r.DocumentType));

        var legacy = SecFilingIndexTable.Parse(RealSecFilingIndexPages.HzoEx991Only, InlineViewerLinks.IgnoreAsBeforeSpec228);
        Assert.Equal("hzo-ex99_1.htm", Assert.Single(legacy).FileName);

        var selection = SecFilingIndexTable.SelectPrimaryDocument(rows, "hzo-20260629.htm");
        Assert.Equal("hzo-20260629.htm", selection.Row?.FileName);
        Assert.Equal(PrimaryDocumentAuthority.Declared, selection.Authority);
        Assert.Equal("hzo-ex99_1.htm", SecFilingIndexTable.SelectEarningsExhibit(rows)?.FileName);
    }

    [Fact]
    public void SelectPrimaryDocument_RealShooIndex_IsThe8K_NotTheCreditAgreementThePre228CodeChose()
    {
        var rows = SecFilingIndexTable.Parse(RealSecFilingIndexPages.ShooCreditAgreementAndResults);

        Assert.Equal(["form8-k.htm", "ex10-1.htm", "ex99-1.htm"], rows.Select(r => r.FileName));
        Assert.Equal(["8-K", "EX-10.1", "EX-99.1"], rows.Select(r => r.DocumentType));

        // With the declared primary (what the collector records from SEC's submissions feed) and without it.
        foreach (var declared in new[] { "form8-k.htm", null })
        {
            var selection = SecFilingIndexTable.SelectPrimaryDocument(rows, declared);
            Assert.Equal("form8-k.htm", selection.Row?.FileName);
            Assert.Null(selection.FailureDetail);
        }

        // The defect this slice closes, reproduced on the same page: the pre-228 row set has no primary row, and
        // its first untyped row — the fallback spec 228 deletes — was the EX-10.1 credit agreement.
        var legacy = SecFilingIndexTable.Parse(RealSecFilingIndexPages.ShooCreditAgreementAndResults, InlineViewerLinks.IgnoreAsBeforeSpec228);
        Assert.Equal(["ex10-1.htm", "ex99-1.htm"], legacy.Select(r => r.FileName));
        Assert.Equal("ex10-1.htm", legacy.First(r => r.Ex99Type is null).FileName);
        Assert.Null(SecFilingIndexTable.SelectPrimaryDocument(legacy, "form8-k.htm").Row);

        Assert.Equal("ex99-1.htm", SecFilingIndexTable.SelectEarningsExhibit(rows)?.FileName);
    }

    [Fact]
    public void SelectMergerAgreementExhibit_RealHzoMergerIndex_IsTheEx21_WhichThePre228CodeTookAsPrimary()
    {
        var rows = SecFilingIndexTable.Parse(RealSecFilingIndexPages.HzoMergerAgreement);

        Assert.Equal(["d135056d8k.htm", "d135056dex21.htm", "d135056dex991.htm"], rows.Select(r => r.FileName));
        Assert.Equal("d135056d8k.htm", SecFilingIndexTable.SelectPrimaryDocument(rows, null).Row?.FileName);
        Assert.Equal("d135056dex991.htm", SecFilingIndexTable.SelectEarningsExhibit(rows)?.FileName);
        Assert.Equal("d135056dex21.htm", SecFilingIndexTable.SelectMergerAgreementExhibit(rows)?.FileName);

        var legacy = SecFilingIndexTable.Parse(RealSecFilingIndexPages.HzoMergerAgreement, InlineViewerLinks.IgnoreAsBeforeSpec228);
        Assert.Equal("d135056dex21.htm", legacy.First(r => r.Ex99Type is null).FileName);
    }

    [Fact]
    public void SelectMergerAgreementExhibit_NeverAnEx10MaterialContract()
    {
        Assert.Null(SecFilingIndexTable.SelectMergerAgreementExhibit(
            SecFilingIndexTable.Parse(RealSecFilingIndexPages.ShooCreditAgreementAndResults)));
    }

    [Fact]
    public void SelectPrimaryDocument_NoDeclaredAndNoFormTypedRow_IsANamedFailure_NeverAnUntypedGuess()
    {
        var rows = SecFilingIndexTable.Parse(Index(
            ("EX-10.1", "/Archives/edgar/data/1/000000000126000001/ex10-1.htm", "EX-10.1"),
            ("EX-99.1", "/Archives/edgar/data/1/000000000126000001/ex99-1.htm", "EX-99.1")));

        var selection = SecFilingIndexTable.SelectPrimaryDocument(rows, "not-in-index.htm");

        Assert.Null(selection.Row);
        Assert.Null(selection.Authority);
        Assert.Equal(SecFilingIndexTable.NoPrimaryDocumentRow, selection.FailureDetail);
    }

    [Fact]
    public void SelectPrimaryDocument_TwoFormTypedRows_IsAmbiguous_NeverResolvedByPosition()
    {
        var rows = SecFilingIndexTable.Parse(Index(
            ("8-K", "/ix?doc=/Archives/edgar/data/1/000000000126000001/a.htm", "8-K"),
            ("8-K", "/ix?doc=/Archives/edgar/data/1/000000000126000001/b.htm", "8-K")));

        var selection = SecFilingIndexTable.SelectPrimaryDocument(rows, declaredPrimaryDocument: null);

        Assert.Null(selection.Row);
        Assert.StartsWith(SecFilingIndexTable.AmbiguousPrimaryDocumentRows, selection.FailureDetail);

        // The declared document still resolves it — it is the higher authority.
        Assert.Equal("b.htm", SecFilingIndexTable.SelectPrimaryDocument(rows, "b.htm").Row?.FileName);
    }

    [Fact]
    public void SelectPrimaryDocument_AmendedForm_IsFormTyped()
    {
        var rows = SecFilingIndexTable.Parse(Index(
            ("8-K/A", "/ix?doc=/Archives/edgar/data/1/000000000126000001/amend.htm", "8-K/A"),
            ("EX-10.1", "/Archives/edgar/data/1/000000000126000001/ex10-1.htm", "EX-10.1")));

        Assert.Equal("amend.htm", SecFilingIndexTable.SelectPrimaryDocument(rows, null).Row?.FileName);
    }

    [Fact]
    public void Parse_TypeComesFromTheTypeColumn_NotFromADescriptionThatSays8K()
    {
        // Description cell "8-K" (free text) on an exhibit whose Type column is EX-10.1: not a primary.
        var rows = SecFilingIndexTable.Parse(Index(
            ("8-K", "/Archives/edgar/data/1/000000000126000001/ex10-1.htm", "EX-10.1")));

        Assert.Equal("EX-10.1", Assert.Single(rows).DocumentType);
        Assert.Null(SecFilingIndexTable.SelectPrimaryDocument(rows, null).Row);
    }

    [Fact]
    public void Parse_TableWithoutATypeHeader_LeavesDocumentTypeUnrecorded()
    {
        const string html = """
            <table>
              <tr><td>1</td><td>8-K</td><td><a href="/Archives/edgar/data/1/000000000126000001/p.htm">p.htm</a></td><td>8-K</td></tr>
            </table>
            """;

        var row = Assert.Single(SecFilingIndexTable.Parse(html));
        Assert.Null(row.DocumentType);
        Assert.Null(SecFilingIndexTable.SelectPrimaryDocument([row], null).Row);
    }

    [Theory]
    [InlineData("/ix?doc=/Archives/edgar/data/1/000000000126000001/doc.htm")]
    [InlineData("https://www.sec.gov/ix?doc=/Archives/edgar/data/1/000000000126000001/doc.htm")]
    [InlineData("/ix?doc=/Archives/edgar/data/1/000000000126000001/doc.htm&amp;action=view")]
    [InlineData("/ix?action=view&amp;doc=%2FArchives%2Fedgar%2Fdata%2F1%2F000000000126000001%2Fdoc.htm")]
    public void Parse_InlineViewerHref_YieldsTheDocParametersLastPathSegment(string href)
    {
        var rows = SecFilingIndexTable.Parse(Index(("8-K", href, "8-K")));

        var row = Assert.Single(rows);
        Assert.Equal("doc.htm", row.FileName);
        Assert.True(row.LinkedThroughInlineViewer);
        Assert.Empty(SecFilingIndexTable.Parse(Index(("8-K", href, "8-K")), InlineViewerLinks.IgnoreAsBeforeSpec228));
    }

    [Fact]
    public void Parse_InlineViewerHrefToANonHtmlDocument_IsNotACandidate()
    {
        Assert.Empty(SecFilingIndexTable.Parse(Index(("XML", "/ix?doc=/Archives/edgar/data/1/2/x.xml", "XML"))));
    }

    private static string Index(params (string Description, string Href, string Type)[] rows)
    {
        var body = string.Concat(rows.Select((r, i) =>
            $"""
              <tr>
                <td scope="row">{i + 1}</td>
                <td scope="row">{r.Description}</td>
                <td scope="row"><a href="{r.Href}">doc</a></td>
                <td scope="row">{r.Type}</td>
                <td scope="row">1000</td>
              </tr>
            """));
        return $"""
            <table class="tableFile" summary="Document Format Files">
              <tr>
                <th scope="col">Seq</th><th scope="col">Description</th><th scope="col">Document</th>
                <th scope="col">Type</th><th scope="col">Size</th>
              </tr>
            {body}
            </table>
            """;
    }
}
