using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxGuard.Core.Indexing;
using DocxGuard.Core.Packaging;

namespace DocxGuard.Tests;

public sealed class DocxIndexInspectorTests
{
    private readonly DocxIndexInspector inspector = new();

    [Fact]
    public void Inspect_OpenXmlSdkDocument_ReconstructsRunsAndOffsets()
    {
        using MemoryStream stream = new();
        using (WordprocessingDocument document = WordprocessingDocument.Create(
            stream,
            DocumentFormat.OpenXml.WordprocessingDocumentType.Document,
            autoSave: true))
        {
            MainDocumentPart mainPart = document.AddMainDocumentPart();
            mainPart.Document = new Document(
                new Body(
                    new Paragraph(
                        new Run(new Text("Hel")),
                        new Run(new Text("lo")))));
        }

        stream.Position = 0;
        DocxIndexInspectionResult result = inspector.Inspect(stream);

        Assert.Equal(PackageInspectionStatus.Passed, result.Status);
        WordDocumentIndex index = Assert.IsType<WordDocumentIndex>(result.Index);
        IndexedParagraph paragraph = Assert.Single(index.Paragraphs);
        Assert.Equal("p000001", paragraph.Id);
        Assert.Equal(0, paragraph.Ordinal);
        Assert.Null(paragraph.TableCellId);
        Assert.Equal("Hello", paragraph.Text);
        Assert.Equal("Hello", index.PlainText);
        Assert.Collection(
            paragraph.Runs,
            first =>
            {
                Assert.Equal("p000001/r0001", first.Id);
                Assert.Equal(0, first.Ordinal);
                Assert.Equal(0, first.StartOffset);
                Assert.Equal(3, first.EndOffset);
                Assert.Equal("Hel", first.Text);
                Assert.True(first.IsVisible);
            },
            second =>
            {
                Assert.Equal("p000001/r0002", second.Id);
                Assert.Equal(1, second.Ordinal);
                Assert.Equal(3, second.StartOffset);
                Assert.Equal(5, second.EndOffset);
                Assert.Equal("lo", second.Text);
                Assert.True(second.IsVisible);
            });
        Assert.True(index.TryGetParagraph("p000001", out IndexedParagraph? found));
        Assert.Same(paragraph, found);
    }

    [Fact]
    public void Inspect_VisibleTextTokens_AreMappedDeterministically()
    {
        using MemoryStream stream = IndexTestPackage.WordStream("""
            <w:p>
              <w:r>
                <w:t>A</w:t><w:tab/><w:ptab/><w:br/><w:noBreakHyphen/>
                <w:softHyphen/><w:drawing/><w:sym/><w:instrText>ignored</w:instrText>
                <w:t xml:space="preserve"> </w:t>
              </w:r>
            </w:p>
            """);

        DocxIndexInspectionResult result = inspector.Inspect(stream);

        IndexedParagraph paragraph = Assert.Single(result.Index!.Paragraphs);
        Assert.Equal("A\t\t\n\u2011\u00AD\uFFFC\uFFFC ", paragraph.Text);
        Assert.Equal(paragraph.Text, Assert.Single(paragraph.Runs).Text);
    }

    [Fact]
    public void Inspect_DeletedAndMovedFromText_IsNotVisible()
    {
        using MemoryStream stream = IndexTestPackage.WordStream("""
            <w:p>
              <w:del><w:r><w:t>deleted</w:t></w:r></w:del>
              <w:moveFrom><w:r><w:t>moved away</w:t></w:r></w:moveFrom>
              <w:ins><w:r><w:t>current</w:t></w:r></w:ins>
              <w:moveTo><w:r><w:t> here</w:t></w:r></w:moveTo>
            </w:p>
            """);

        DocxIndexInspectionResult result = inspector.Inspect(stream);

        IndexedParagraph paragraph = Assert.Single(result.Index!.Paragraphs);
        Assert.Equal("current here", paragraph.Text);
        Assert.Equal(4, paragraph.Runs.Count);
        Assert.All(paragraph.Runs.Take(2), hidden =>
        {
            Assert.False(hidden.IsVisible);
            Assert.Empty(hidden.Text);
            Assert.Equal(0, hidden.StartOffset);
        });
        Assert.True(paragraph.Runs[2].IsVisible);
        Assert.Equal(0, paragraph.Runs[2].StartOffset);
        Assert.Equal(7, paragraph.Runs[3].StartOffset);
    }

    [Fact]
    public void Inspect_MultipleParagraphs_UseStableIdsAndLfPlainText()
    {
        using MemoryStream stream = IndexTestPackage.WordStream("""
            <w:p><w:r><w:t>first</w:t></w:r></w:p>
            <w:p/>
            <w:p><w:r><w:t>third</w:t></w:r></w:p>
            """);

        DocxIndexInspectionResult result = inspector.Inspect(stream);

        Assert.Equal(
            ["p000001", "p000002", "p000003"],
            result.Index!.Paragraphs.Select(paragraph => paragraph.Id));
        Assert.Equal("first\n\nthird", result.Index.PlainText);
    }

    [Fact]
    public void Inspect_NestedTables_LinkTablesCellsAndParagraphs()
    {
        using MemoryStream stream = IndexTestPackage.WordStream("""
            <w:tbl>
              <w:tr>
                <w:tc>
                  <w:p><w:r><w:t>outer</w:t></w:r></w:p>
                  <w:tbl><w:tr><w:tc>
                    <w:p><w:r><w:t>inner</w:t></w:r></w:p>
                  </w:tc></w:tr></w:tbl>
                </w:tc>
                <w:tc><w:p><w:r><w:t>second</w:t></w:r></w:p></w:tc>
              </w:tr>
            </w:tbl>
            """);

        DocxIndexInspectionResult result = inspector.Inspect(stream);

        WordDocumentIndex index = result.Index!;
        Assert.Equal(["outer", "inner", "second"],
            index.Paragraphs.Select(paragraph => paragraph.Text));
        Assert.Equal(2, index.Tables.Count);

        IndexedTable outer = index.Tables[0];
        Assert.Equal("t000001", outer.Id);
        Assert.Null(outer.ParentCellId);
        Assert.Equal(1, outer.RowCount);
        Assert.Equal(2, outer.Cells.Count);
        Assert.Equal("t000001/r0001/c0001", outer.Cells[0].Id);
        Assert.Equal(["p000001"], outer.Cells[0].ParagraphIds);
        Assert.Equal(["t000002"], outer.Cells[0].NestedTableIds);
        Assert.Equal(["p000003"], outer.Cells[1].ParagraphIds);

        IndexedTable nested = index.Tables[1];
        Assert.Equal("t000002", nested.Id);
        Assert.Equal(outer.Cells[0].Id, nested.ParentCellId);
        Assert.Equal(["p000002"], Assert.Single(nested.Cells).ParagraphIds);
        Assert.Equal(nested.Cells[0].Id, index.Paragraphs[1].TableCellId);
    }

    [Fact]
    public void Inspect_StrictDocument_UsesTheSameIndexContract()
    {
        using MemoryStream stream = IndexTestPackage.WordStream(
            "<w:p><w:r><w:t>strict</w:t></w:r></w:p>",
            strict: true);

        DocxIndexInspectionResult result = inspector.Inspect(stream);

        Assert.Equal(PackageInspectionStatus.Passed, result.Status);
        Assert.Equal("strict", result.Index!.PlainText);
    }

    [Fact]
    public void Inspect_IndexStructureFailure_PreservesPassedDocumentLayer()
    {
        using MemoryStream stream = IndexTestPackage.WordStream(
            "<w:r><w:t>outside paragraph</w:t></w:r>");

        DocxIndexInspectionResult result = inspector.Inspect(stream);

        Assert.Equal(PackageInspectionStatus.Passed, result.DocumentInspection.Status);
        Assert.Equal(PackageInspectionStatus.Failed, result.Status);
        Assert.Null(result.Index);
        Assert.Contains(
            result.IndexFindings,
            finding => finding.Code == DocumentIndexFindingCodes.StructureViolation
                && finding.LineNumber is not null);
    }

    [Fact]
    public void Inspect_DocumentFailure_StopsBeforeIndexLayer()
    {
        using MemoryStream stream = IndexTestPackage.WordStream(
            "<w:sectPr/><w:p/>");

        DocxIndexInspectionResult result = inspector.Inspect(stream);

        Assert.Equal(PackageInspectionStatus.Failed, result.Status);
        Assert.Null(result.Index);
        Assert.Empty(result.IndexFindings);
        Assert.NotEmpty(result.DocumentInspection.WordprocessingFindings);
    }
}
