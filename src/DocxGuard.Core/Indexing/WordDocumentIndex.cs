using System.Collections.ObjectModel;

namespace DocxGuard.Core.Indexing;

public sealed record IndexedRun(
    string Id,
    int Ordinal,
    int StartOffset,
    string Text,
    bool IsVisible)
{
    public int Length => Text.Length;

    public int EndOffset => StartOffset + Length;
}

public sealed class IndexedParagraph
{
    internal IndexedParagraph(
        string id,
        int ordinal,
        string? tableCellId,
        string text,
        IEnumerable<IndexedRun> runs)
    {
        Id = id;
        Ordinal = ordinal;
        TableCellId = tableCellId;
        Text = text;
        Runs = new ReadOnlyCollection<IndexedRun>(runs.ToArray());
    }

    public string Id { get; }

    public int Ordinal { get; }

    public string? TableCellId { get; }

    public string Text { get; }

    public IReadOnlyList<IndexedRun> Runs { get; }
}

public sealed class IndexedTableCell
{
    internal IndexedTableCell(
        string id,
        string tableId,
        int rowIndex,
        int columnIndex,
        IEnumerable<string> paragraphIds,
        IEnumerable<string> nestedTableIds)
    {
        Id = id;
        TableId = tableId;
        RowIndex = rowIndex;
        ColumnIndex = columnIndex;
        ParagraphIds = new ReadOnlyCollection<string>(paragraphIds.ToArray());
        NestedTableIds = new ReadOnlyCollection<string>(nestedTableIds.ToArray());
    }

    public string Id { get; }

    public string TableId { get; }

    public int RowIndex { get; }

    public int ColumnIndex { get; }

    public IReadOnlyList<string> ParagraphIds { get; }

    public IReadOnlyList<string> NestedTableIds { get; }
}

public sealed class IndexedTable
{
    internal IndexedTable(
        string id,
        int ordinal,
        string? parentCellId,
        int rowCount,
        IEnumerable<IndexedTableCell> cells)
    {
        Id = id;
        Ordinal = ordinal;
        ParentCellId = parentCellId;
        RowCount = rowCount;
        Cells = new ReadOnlyCollection<IndexedTableCell>(cells.ToArray());
    }

    public string Id { get; }

    public int Ordinal { get; }

    public string? ParentCellId { get; }

    public int RowCount { get; }

    public IReadOnlyList<IndexedTableCell> Cells { get; }
}

public sealed class WordDocumentIndex
{
    internal WordDocumentIndex(
        IEnumerable<IndexedParagraph> paragraphs,
        IEnumerable<IndexedTable> tables)
    {
        Paragraphs = new ReadOnlyCollection<IndexedParagraph>(
            paragraphs.ToArray());
        Tables = new ReadOnlyCollection<IndexedTable>(tables.ToArray());
        PlainText = string.Join(
            "\n",
            Paragraphs.Select(paragraph => paragraph.Text));
    }

    public IReadOnlyList<IndexedParagraph> Paragraphs { get; }

    public IReadOnlyList<IndexedTable> Tables { get; }

    public string PlainText { get; }

    public bool TryGetParagraph(
        string id,
        out IndexedParagraph? paragraph)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        paragraph = Paragraphs.SingleOrDefault(
            candidate => candidate.Id == id);
        return paragraph is not null;
    }
}
