using System.Xml;
using DocxGuard.Core.Packaging;

namespace DocxGuard.Core.Indexing;

internal sealed partial class DocumentIndexBuildState
{
    private const string MarkupCompatibilityNamespace =
        "http://schemas.openxmlformats.org/markup-compatibility/2006";

    private readonly OpcPartName partName;
    private readonly string wordNamespace;
    private readonly DocumentIndexLimits limits;
    private readonly List<IndexedParagraph> paragraphs = [];
    private readonly List<IndexTableBuilder> tables = [];
    private readonly Stack<IndexTableBuilder> tableStack = new();
    private IndexParagraphBuilder? paragraph;
    private IndexRunBuilder? run;
    private int? capturedTextDepth;
    private bool bodySeen;
    private bool bodyOpen;
    private int suppressionDepth;
    private int totalRuns;
    private int totalCells;
    private int totalTextCharacters;
    private int currentParagraphRunCount;

    public DocumentIndexBuildState(
        OpcPartName partName,
        string wordNamespace,
        DocumentIndexLimits limits)
    {
        this.partName = partName;
        this.wordNamespace = wordNamespace;
        this.limits = limits;
    }

    public DocumentIndexParseResult? HandleStart(XmlReader reader)
    {
        if (IsWordElement(reader, "body") && reader.Depth == 1)
        {
            bodySeen = true;
            bodyOpen = !reader.IsEmptyElement;
            return null;
        }

        if (!bodyOpen)
        {
            return null;
        }

        if (capturedTextDepth is not null)
        {
            return StructureFailure(
                reader,
                "A Word text element cannot contain child elements.");
        }

        if (reader.NamespaceURI == MarkupCompatibilityNamespace
            && reader.LocalName == "AlternateContent")
        {
            return StructureFailure(
                reader,
                "Markup Compatibility AlternateContent requires branch selection and is outside the V1 index profile.");
        }

        if (reader.NamespaceURI != wordNamespace)
        {
            return null;
        }

        if (IsSuppressionElement(reader) && !reader.IsEmptyElement)
        {
            suppressionDepth++;
        }

        return reader.LocalName switch
        {
            "tbl" => BeginTable(reader),
            "tr" => BeginRow(reader),
            "tc" => BeginCell(reader),
            "p" => BeginParagraph(reader),
            "r" => BeginRun(reader),
            "t" => BeginText(reader),
            "tab" or "ptab" => AppendToken(reader, "\t"),
            "br" or "cr" => AppendToken(reader, "\n"),
            "noBreakHyphen" => AppendToken(reader, "\u2011"),
            "softHyphen" => AppendToken(reader, "\u00AD"),
            "drawing" or "pict" or "object" or "sym" =>
                AppendToken(reader, "\uFFFC"),
            _ => null,
        };
    }

    private DocumentIndexParseResult? BeginTable(XmlReader reader)
    {
        if (paragraph is not null)
        {
            return StructureFailure(
                reader,
                "A table nested inside an open paragraph is outside the V1 index profile.");
        }

        if (tables.Count >= limits.MaxTables)
        {
            return ResourceFailure(
                reader,
                $"The document exceeds the table limit of {limits.MaxTables}.");
        }

        if (tableStack.Count >= limits.MaxTableNestingDepth)
        {
            return ResourceFailure(
                reader,
                $"The document exceeds the table nesting limit of {limits.MaxTableNestingDepth}.");
        }

        IndexTableCellBuilder? parentCell = tableStack.Count == 0
            ? null
            : tableStack.Peek().CurrentCell;
        if (tableStack.Count != 0 && parentCell is null)
        {
            return StructureFailure(
                reader,
                "A nested table must be contained by an open table cell.");
        }

        IndexTableBuilder table = new(
            $"t{tables.Count + 1:D6}",
            tables.Count,
            parentCell?.Id);
        tables.Add(table);
        parentCell?.AddNestedTable(table.Id);
        tableStack.Push(table);
        if (reader.IsEmptyElement)
        {
            tableStack.Pop();
        }

        return null;
    }

    private DocumentIndexParseResult? BeginRow(XmlReader reader)
    {
        if (tableStack.Count == 0 || !tableStack.Peek().TryBeginRow())
        {
            return StructureFailure(
                reader,
                "A table row must start directly within an open table.");
        }

        if (reader.IsEmptyElement)
        {
            tableStack.Peek().TryEndRow();
        }

        return null;
    }

    private DocumentIndexParseResult? BeginCell(XmlReader reader)
    {
        if (tableStack.Count == 0)
        {
            return StructureFailure(reader, "A table cell requires an open table.");
        }

        if (totalCells >= limits.MaxTableCells)
        {
            return ResourceFailure(
                reader,
                $"The document exceeds the table-cell limit of {limits.MaxTableCells}.");
        }

        IndexTableCellBuilder? cell = tableStack.Peek().BeginCell();
        if (cell is null)
        {
            return StructureFailure(
                reader,
                "A table cell must start directly within an open row.");
        }

        totalCells++;
        if (reader.IsEmptyElement)
        {
            tableStack.Peek().TryEndCell();
        }

        return null;
    }

    private DocumentIndexParseResult? BeginParagraph(XmlReader reader)
    {
        if (paragraph is not null)
        {
            return StructureFailure(reader, "Paragraphs cannot be nested.");
        }

        if (paragraphs.Count >= limits.MaxParagraphs)
        {
            return ResourceFailure(
                reader,
                $"The document exceeds the paragraph limit of {limits.MaxParagraphs}.");
        }

        IndexTableCellBuilder? cell = tableStack.Count == 0
            ? null
            : tableStack.Peek().CurrentCell;
        if (tableStack.Count != 0 && cell is null)
        {
            return StructureFailure(
                reader,
                "A paragraph within a table must be contained by an open cell.");
        }

        if (paragraphs.Count != 0 && !TryReserveText(1))
        {
            return ResourceFailure(
                reader,
                $"The index exceeds the text limit of {limits.MaxTextCharacters} characters.");
        }

        paragraph = new IndexParagraphBuilder(
            $"p{paragraphs.Count + 1:D6}",
            paragraphs.Count,
            cell?.Id);
        currentParagraphRunCount = 0;
        cell?.AddParagraph(paragraph.Id);
        if (reader.IsEmptyElement)
        {
            FinishParagraph();
        }

        return null;
    }

    private DocumentIndexParseResult? BeginRun(XmlReader reader)
    {
        if (paragraph is null || run is not null)
        {
            return StructureFailure(
                reader,
                "A run must be contained by one open paragraph and cannot be nested.");
        }

        if (totalRuns >= limits.MaxRuns)
        {
            return ResourceFailure(
                reader,
                $"The document exceeds the run limit of {limits.MaxRuns}.");
        }

        run = new IndexRunBuilder(
            $"{paragraph.Id}/r{currentParagraphRunCount + 1:D4}",
            currentParagraphRunCount,
            paragraph.TextLength,
            suppressionDepth == 0);
        currentParagraphRunCount++;
        totalRuns++;
        if (reader.IsEmptyElement)
        {
            FinishRun();
        }

        return null;
    }

    private DocumentIndexParseResult? BeginText(XmlReader reader)
    {
        if (run is null || capturedTextDepth is not null)
        {
            return StructureFailure(
                reader,
                "A text element must be contained by one open run.");
        }

        if (!reader.IsEmptyElement)
        {
            capturedTextDepth = reader.Depth;
        }

        return null;
    }

    private DocumentIndexParseResult? AppendToken(
        XmlReader reader,
        string value)
    {
        if (run is null)
        {
            return StructureFailure(
                reader,
                "A visible-text token must be contained by an open run.");
        }

        return AppendVisible(reader, value);
    }

    private DocumentIndexParseResult? AppendVisible(
        XmlReader reader,
        string value)
    {
        if (run is null || !run.IsVisible || value.Length == 0)
        {
            return null;
        }

        if (!TryReserveText(value.Length))
        {
            return ResourceFailure(
                reader,
                $"The index exceeds the text limit of {limits.MaxTextCharacters} characters.");
        }

        run.Append(value);
        paragraph!.Append(value);
        return null;
    }

    private bool TryReserveText(int characterCount)
    {
        if (characterCount > limits.MaxTextCharacters - totalTextCharacters)
        {
            return false;
        }

        totalTextCharacters += characterCount;
        return true;
    }

    private bool IsWordElement(XmlReader reader, string localName) =>
        reader.NamespaceURI == wordNamespace && reader.LocalName == localName;

    private bool IsSuppressionElement(XmlReader reader) =>
        reader.LocalName is "del" or "moveFrom";
}
