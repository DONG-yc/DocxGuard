using System.Xml;
using DocxGuard.Core.Packaging;

namespace DocxGuard.Core.Indexing;

internal sealed partial class DocumentIndexBuildState
{
    public DocumentIndexParseResult? HandleText(XmlReader reader)
    {
        if (!bodyOpen || capturedTextDepth is null)
        {
            return null;
        }

        return AppendVisible(reader, reader.Value);
    }

    public DocumentIndexParseResult? HandleEnd(XmlReader reader)
    {
        if (reader.NamespaceURI != wordNamespace)
        {
            return null;
        }

        DocumentIndexParseResult? failure = reader.LocalName switch
        {
            "t" => EndText(reader),
            "r" => EndRun(reader),
            "p" => EndParagraph(reader),
            "tc" => EndCell(reader),
            "tr" => EndRow(reader),
            "tbl" => EndTable(reader),
            "body" when reader.Depth == 1 => EndBody(reader),
            _ => null,
        };
        if (failure is not null)
        {
            return failure;
        }

        if (IsSuppressionElement(reader))
        {
            if (suppressionDepth == 0)
            {
                return StructureFailure(
                    reader,
                    "A revision-suppression container closed without opening.");
            }

            suppressionDepth--;
        }

        return null;
    }

    public DocumentIndexParseResult Build()
    {
        if (!bodySeen
            || bodyOpen
            || paragraph is not null
            || run is not null
            || capturedTextDepth is not null
            || tableStack.Count != 0
            || suppressionDepth != 0)
        {
            return Failure(
                DocumentIndexFindingCodes.StructureViolation,
                "The document index ended with incomplete WordprocessingML structures.");
        }

        return new DocumentIndexParseResult(
            new WordDocumentIndex(
                paragraphs,
                tables.Select(table => table.Build())),
            []);
    }

    public DocumentIndexParseResult FailureAt(
        XmlReader reader,
        string code,
        string message)
    {
        IXmlLineInfo lineInfo = (IXmlLineInfo)reader;
        return Failure(
            code,
            message,
            lineInfo.HasLineInfo() ? lineInfo.LineNumber : null,
            lineInfo.HasLineInfo() ? lineInfo.LinePosition : null);
    }

    public DocumentIndexParseResult Failure(
        string code,
        string message,
        int? lineNumber = null,
        int? linePosition = null) =>
        new(
            null,
            [
                new DocumentIndexFinding(
                    code,
                    FindingSeverity.Error,
                    message,
                    partName.Value,
                    NormalizeLineNumber(lineNumber),
                    NormalizeLineNumber(linePosition)),
            ]);

    private DocumentIndexParseResult? EndText(XmlReader reader)
    {
        if (capturedTextDepth != reader.Depth)
        {
            return StructureFailure(
                reader,
                "A text element closed outside the active run text.");
        }

        capturedTextDepth = null;
        return null;
    }

    private DocumentIndexParseResult? EndRun(XmlReader reader)
    {
        if (run is null || capturedTextDepth is not null)
        {
            return StructureFailure(reader, "A run closed without a complete open run.");
        }

        FinishRun();
        return null;
    }

    private DocumentIndexParseResult? EndParagraph(XmlReader reader)
    {
        if (paragraph is null || run is not null)
        {
            return StructureFailure(
                reader,
                "A paragraph closed without a complete open paragraph.");
        }

        FinishParagraph();
        return null;
    }

    private DocumentIndexParseResult? EndCell(XmlReader reader)
    {
        if (paragraph is not null
            || tableStack.Count == 0
            || !tableStack.Peek().TryEndCell())
        {
            return StructureFailure(reader, "A table cell closed out of sequence.");
        }

        return null;
    }

    private DocumentIndexParseResult? EndRow(XmlReader reader)
    {
        if (tableStack.Count == 0 || !tableStack.Peek().TryEndRow())
        {
            return StructureFailure(reader, "A table row closed out of sequence.");
        }

        return null;
    }

    private DocumentIndexParseResult? EndTable(XmlReader reader)
    {
        if (paragraph is not null
            || tableStack.Count == 0
            || !tableStack.Peek().CanClose)
        {
            return StructureFailure(reader, "A table closed out of sequence.");
        }

        tableStack.Pop();
        return null;
    }

    private DocumentIndexParseResult? EndBody(XmlReader reader)
    {
        if (paragraph is not null
            || run is not null
            || tableStack.Count != 0
            || suppressionDepth != 0)
        {
            return StructureFailure(
                reader,
                "The body closed while indexed structures were still open.");
        }

        bodyOpen = false;
        return null;
    }

    private void FinishRun()
    {
        paragraph!.AddRun(run!);
        run = null;
    }

    private void FinishParagraph()
    {
        paragraphs.Add(paragraph!.Build());
        paragraph = null;
    }

    private DocumentIndexParseResult ResourceFailure(
        XmlReader reader,
        string message) =>
        FailureAt(
            reader,
            DocumentIndexFindingCodes.ResourceLimitExceeded,
            message);

    private DocumentIndexParseResult StructureFailure(
        XmlReader reader,
        string message) =>
        FailureAt(
            reader,
            DocumentIndexFindingCodes.StructureViolation,
            message);

    private static int? NormalizeLineNumber(int? value) =>
        value > 0 ? value : null;
}
