using DocxGuard.Core.Indexing;
using DocxGuard.Core.Packaging;
using DocxGuard.Core.Planning;

namespace DocxGuard.Tests;

public sealed class TextLocatorTests
{
    private readonly TextLocator locator = new();

    [Fact]
    public void Locate_MatchAcrossRuns_MapsEveryRunSlice()
    {
        WordDocumentIndex index = CreateIndex("""
            <w:p>
              <w:r><w:t>ab</w:t></w:r>
              <w:r><w:t>cde</w:t></w:r>
              <w:r><w:t>fg</w:t></w:r>
            </w:p>
            """);

        TextLocationResult result = locator.Locate(index, "bcdef");

        Assert.Equal(TextLocationStatus.Unique, result.Status);
        TextMatch match = Assert.Single(result.Matches);
        Assert.Equal("m000001", match.Id);
        Assert.Equal("p000001", match.ParagraphId);
        Assert.Equal(1, match.StartOffset);
        Assert.Equal(6, match.EndOffset);
        Assert.Equal("bcdef", match.Text);
        Assert.Collection(
            match.RunSlices,
            first => AssertSlice(first, "p000001/r0001", 1, 1, 1),
            second => AssertSlice(second, "p000001/r0002", 2, 0, 3),
            third => AssertSlice(third, "p000001/r0003", 5, 0, 1));
    }

    [Fact]
    public void Locate_OverlappingMatches_AreAllReportedAsAmbiguous()
    {
        WordDocumentIndex index = CreateIndex(
            "<w:p><w:r><w:t>aaa</w:t></w:r></w:p>");

        TextLocationResult result = locator.Locate(index, "aa");

        Assert.Equal(TextLocationStatus.Ambiguous, result.Status);
        Assert.Equal([0, 1], result.Matches.Select(match => match.StartOffset));
        Assert.Equal(["m000001", "m000002"],
            result.Matches.Select(match => match.Id));
        Assert.Contains(
            result.Findings,
            finding => finding.Code == TextLocationFindingCodes.AmbiguousMatch
                && finding.Severity == FindingSeverity.Warning);
    }

    [Fact]
    public void Locate_ParagraphScope_ResolvesCrossParagraphAmbiguity()
    {
        WordDocumentIndex index = CreateIndex("""
            <w:p><w:r><w:t>target</w:t></w:r></w:p>
            <w:p><w:r><w:t>target</w:t></w:r></w:p>
            """);

        TextLocationResult unscoped = locator.Locate(index, "target");
        TextLocationResult scoped = locator.Locate(
            index,
            "target",
            paragraphScopeId: "p000002");

        Assert.Equal(TextLocationStatus.Ambiguous, unscoped.Status);
        Assert.Equal(TextLocationStatus.Unique, scoped.Status);
        Assert.Equal("p000002", Assert.Single(scoped.Matches).ParagraphId);
    }

    [Fact]
    public void Locate_DoesNotMatchAcrossParagraphBoundaries()
    {
        WordDocumentIndex index = CreateIndex("""
            <w:p><w:r><w:t>first</w:t></w:r></w:p>
            <w:p><w:r><w:t>second</w:t></w:r></w:p>
            """);

        TextLocationResult result = locator.Locate(index, "first\nsecond");

        Assert.Equal(TextLocationStatus.NoMatch, result.Status);
        Assert.Empty(result.Matches);
        Assert.Contains(
            result.Findings,
            finding => finding.Code == TextLocationFindingCodes.NoMatch);
    }

    [Fact]
    public void Locate_IsOrdinalAndCaseSensitive()
    {
        WordDocumentIndex index = CreateIndex(
            "<w:p><w:r><w:t>Target target</w:t></w:r></w:p>");

        TextLocationResult result = locator.Locate(index, "target");

        TextMatch match = Assert.Single(result.Matches);
        Assert.Equal(7, match.StartOffset);
    }

    [Fact]
    public void Locate_TableCandidate_IncludesCellIdentity()
    {
        WordDocumentIndex index = CreateIndex("""
            <w:tbl><w:tr><w:tc>
              <w:p><w:r><w:t>cell target</w:t></w:r></w:p>
            </w:tc></w:tr></w:tbl>
            """);

        TextMatch match = Assert.Single(locator.Locate(index, "target").Matches);

        Assert.Equal("t000001/r0001/c0001", match.TableCellId);
    }

    [Fact]
    public void Locate_MissingParagraphScope_IsRejected()
    {
        WordDocumentIndex index = CreateIndex("<w:p/>");

        TextLocationResult result = locator.Locate(
            index,
            "target",
            paragraphScopeId: "p999999");

        Assert.Equal(TextLocationStatus.Rejected, result.Status);
        Assert.Empty(result.Matches);
        Assert.Contains(
            result.Findings,
            finding => finding.Code
                == TextLocationFindingCodes.ParagraphScopeNotFound
                && finding.Severity == FindingSeverity.Error);
    }

    [Fact]
    public void Locate_QueryAndMatchLimits_FailWithoutPartialCandidates()
    {
        WordDocumentIndex index = CreateIndex(
            "<w:p><w:r><w:t>aaaa</w:t></w:r></w:p>");

        TextLocationResult queryLimit = locator.Locate(
            index,
            "aa",
            limits: TextLocationLimits.Default with
            {
                MaxQueryCharacters = 1,
            });
        TextLocationResult matchLimit = locator.Locate(
            index,
            "a",
            limits: TextLocationLimits.Default with
            {
                MaxMatches = 2,
            });

        AssertRejectedByLimit(queryLimit);
        AssertRejectedByLimit(matchLimit);
    }

    [Fact]
    public void Locate_InvalidArgumentsLimitsAndCancellation_Throw()
    {
        WordDocumentIndex index = CreateIndex("<w:p/>");
        using CancellationTokenSource source = new();
        source.Cancel();

        Assert.Throws<ArgumentException>(() => locator.Locate(index, ""));
        Assert.Throws<ArgumentException>(
            () => locator.Locate(index, "x", paragraphScopeId: " "));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => locator.Locate(
                index,
                "x",
                limits: TextLocationLimits.Default with { MaxMatches = 0 }));
        Assert.Throws<OperationCanceledException>(
            () => locator.Locate(
                index,
                "x",
                cancellationToken: source.Token));
    }

    private static WordDocumentIndex CreateIndex(string bodyContent)
    {
        using MemoryStream stream = IndexTestPackage.WordStream(bodyContent);
        DocxIndexInspectionResult result = new DocxIndexInspector().Inspect(stream);
        Assert.Equal(PackageInspectionStatus.Passed, result.Status);
        return result.Index!;
    }

    private static void AssertSlice(
        RunTextSlice slice,
        string runId,
        int paragraphStart,
        int runStart,
        int length)
    {
        Assert.Equal(runId, slice.RunId);
        Assert.Equal(paragraphStart, slice.ParagraphStartOffset);
        Assert.Equal(runStart, slice.StartOffsetInRun);
        Assert.Equal(length, slice.Length);
    }

    private static void AssertRejectedByLimit(TextLocationResult result)
    {
        Assert.Equal(TextLocationStatus.Rejected, result.Status);
        Assert.Empty(result.Matches);
        Assert.Contains(
            result.Findings,
            finding => finding.Code
                == TextLocationFindingCodes.ResourceLimitExceeded);
    }
}
