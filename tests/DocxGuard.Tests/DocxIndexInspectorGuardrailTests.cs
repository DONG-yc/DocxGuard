using DocxGuard.Core.Indexing;
using DocxGuard.Core.Packaging;

namespace DocxGuard.Tests;

public sealed class DocxIndexInspectorGuardrailTests
{
    private readonly DocxIndexInspector inspector = new();

    [Fact]
    public void Inspect_ParagraphLimit_IsEnforced()
    {
        AssertResourceLimit(
            "<w:p/><w:p/>",
            DocumentIndexLimits.Default with { MaxParagraphs = 1 });
    }

    [Fact]
    public void Inspect_RunLimit_IsEnforced()
    {
        AssertResourceLimit(
            "<w:p><w:r/><w:r/></w:p>",
            DocumentIndexLimits.Default with { MaxRuns = 1 });
    }

    [Fact]
    public void Inspect_TableLimit_IsEnforced()
    {
        AssertResourceLimit(
            "<w:tbl/><w:tbl/>",
            DocumentIndexLimits.Default with { MaxTables = 1 });
    }

    [Fact]
    public void Inspect_TableCellLimit_IsEnforced()
    {
        AssertResourceLimit(
            "<w:tbl><w:tr><w:tc/><w:tc/></w:tr></w:tbl>",
            DocumentIndexLimits.Default with { MaxTableCells = 1 });
    }

    [Fact]
    public void Inspect_TableNestingLimit_IsEnforced()
    {
        AssertResourceLimit(
            "<w:tbl><w:tr><w:tc><w:tbl/></w:tc></w:tr></w:tbl>",
            DocumentIndexLimits.Default with { MaxTableNestingDepth = 1 });
    }

    [Fact]
    public void Inspect_TextLimit_IncludesVisibleTextAndParagraphSeparators()
    {
        AssertResourceLimit(
            "<w:p><w:r><w:t>abcd</w:t></w:r></w:p>",
            DocumentIndexLimits.Default with { MaxTextCharacters = 3 });
        AssertResourceLimit(
            "<w:p/><w:p/><w:p/>",
            DocumentIndexLimits.Default with { MaxTextCharacters = 1 });
    }

    [Fact]
    public void Inspect_TableInsideOpenParagraph_FailsClosed()
    {
        using MemoryStream stream = IndexTestPackage.WordStream("""
            <w:p><w:r><w:drawing><w:tbl/></w:drawing></w:r></w:p>
            """);

        DocxIndexInspectionResult result = inspector.Inspect(stream);

        Assert.Equal(PackageInspectionStatus.Failed, result.Status);
        Assert.Null(result.Index);
        Assert.Contains(
            result.IndexFindings,
            finding => finding.Code == DocumentIndexFindingCodes.StructureViolation);
    }

    [Fact]
    public void Inspect_AlternateContent_FailsInsteadOfIndexingBothBranches()
    {
        using MemoryStream stream = IndexTestPackage.WordStream("""
            <mc:AlternateContent xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006">
              <mc:Choice Requires="w"><w:p><w:r><w:t>choice</w:t></w:r></w:p></mc:Choice>
              <mc:Fallback><w:p><w:r><w:t>fallback</w:t></w:r></w:p></mc:Fallback>
            </mc:AlternateContent>
            """);

        DocxIndexInspectionResult result = inspector.Inspect(stream);

        Assert.Equal(PackageInspectionStatus.Passed, result.DocumentInspection.Status);
        Assert.Equal(PackageInspectionStatus.Failed, result.Status);
        Assert.Null(result.Index);
        Assert.Contains(
            result.IndexFindings,
            finding => finding.Code == DocumentIndexFindingCodes.StructureViolation);
    }

    [Fact]
    public void Inspect_RestoresCallerStreamPosition()
    {
        using MemoryStream stream = IndexTestPackage.WordStream(
            "<w:p><w:r><w:t>position</w:t></w:r></w:p>");
        stream.Position = 7;

        DocxIndexInspectionResult result = inspector.Inspect(stream);

        Assert.Equal(PackageInspectionStatus.Passed, result.Status);
        Assert.Equal(7, stream.Position);
    }

    [Fact]
    public void Inspect_CancellationAndInvalidLimits_Throw()
    {
        using MemoryStream stream = IndexTestPackage.WordStream("<w:p/>");
        using CancellationTokenSource source = new();
        source.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => inspector.Inspect(stream, cancellationToken: source.Token));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => inspector.Inspect(
                stream,
                indexLimits: DocumentIndexLimits.Default with
                {
                    MaxRuns = 0,
                }));
    }

    private void AssertResourceLimit(
        string bodyContent,
        DocumentIndexLimits limits)
    {
        using MemoryStream stream = IndexTestPackage.WordStream(bodyContent);

        DocxIndexInspectionResult result = inspector.Inspect(
            stream,
            indexLimits: limits);

        Assert.Equal(PackageInspectionStatus.Passed, result.DocumentInspection.Status);
        Assert.Equal(PackageInspectionStatus.Failed, result.Status);
        Assert.Null(result.Index);
        Assert.Contains(
            result.IndexFindings,
            finding => finding.Code
                == DocumentIndexFindingCodes.ResourceLimitExceeded);
    }
}
