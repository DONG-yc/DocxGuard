using System.Text;

namespace DocxGuard.Core.Indexing;

internal sealed class IndexParagraphBuilder
{
    private readonly StringBuilder text = new();
    private readonly List<IndexedRun> runs = [];

    public IndexParagraphBuilder(string id, int ordinal, string? tableCellId)
    {
        Id = id;
        Ordinal = ordinal;
        TableCellId = tableCellId;
    }

    public string Id { get; }
    public int Ordinal { get; }
    public string? TableCellId { get; }
    public int TextLength => text.Length;

    public void Append(string value) => text.Append(value);

    public void AddRun(IndexRunBuilder run) => runs.Add(
        new IndexedRun(run.Id, run.Ordinal, run.StartOffset, run.Text, run.IsVisible));

    public IndexedParagraph Build() => new(
        Id, Ordinal, TableCellId, text.ToString(), runs);
}

internal sealed class IndexRunBuilder
{
    private readonly StringBuilder text = new();

    public IndexRunBuilder(string id, int ordinal, int startOffset, bool isVisible)
    {
        Id = id;
        Ordinal = ordinal;
        StartOffset = startOffset;
        IsVisible = isVisible;
    }

    public string Id { get; }
    public int Ordinal { get; }
    public int StartOffset { get; }
    public bool IsVisible { get; }
    public string Text => text.ToString();

    public void Append(string value) => text.Append(value);
}

internal sealed class IndexTableBuilder
{
    private readonly List<IndexTableCellBuilder> cells = [];
    private int nextColumnIndex;

    public IndexTableBuilder(string id, int ordinal, string? parentCellId)
    {
        Id = id;
        Ordinal = ordinal;
        ParentCellId = parentCellId;
    }

    public string Id { get; }
    public int Ordinal { get; }
    public string? ParentCellId { get; }
    public int RowCount { get; private set; }
    public bool RowOpen { get; private set; }
    public IndexTableCellBuilder? CurrentCell { get; private set; }

    public bool TryBeginRow()
    {
        if (RowOpen || CurrentCell is not null)
        {
            return false;
        }

        RowOpen = true;
        RowCount++;
        nextColumnIndex = 0;
        return true;
    }

    public bool TryEndRow()
    {
        if (!RowOpen || CurrentCell is not null)
        {
            return false;
        }

        RowOpen = false;
        return true;
    }

    public IndexTableCellBuilder? BeginCell()
    {
        if (!RowOpen || CurrentCell is not null)
        {
            return null;
        }

        int rowIndex = RowCount - 1;
        int columnIndex = nextColumnIndex++;
        CurrentCell = new IndexTableCellBuilder(
            $"{Id}/r{rowIndex + 1:D4}/c{columnIndex + 1:D4}",
            Id,
            rowIndex,
            columnIndex);
        return CurrentCell;
    }

    public bool TryEndCell()
    {
        if (CurrentCell is null)
        {
            return false;
        }

        cells.Add(CurrentCell);
        CurrentCell = null;
        return true;
    }

    public bool CanClose => !RowOpen && CurrentCell is null;

    public IndexedTable Build() => new(
        Id, Ordinal, ParentCellId, RowCount, cells.Select(cell => cell.Build()));
}

internal sealed class IndexTableCellBuilder
{
    private readonly List<string> paragraphIds = [];
    private readonly List<string> nestedTableIds = [];

    public IndexTableCellBuilder(
        string id,
        string tableId,
        int rowIndex,
        int columnIndex)
    {
        Id = id;
        TableId = tableId;
        RowIndex = rowIndex;
        ColumnIndex = columnIndex;
    }

    public string Id { get; }
    public string TableId { get; }
    public int RowIndex { get; }
    public int ColumnIndex { get; }

    public void AddParagraph(string paragraphId) => paragraphIds.Add(paragraphId);
    public void AddNestedTable(string tableId) => nestedTableIds.Add(tableId);

    public IndexedTableCell Build() => new(
        Id, TableId, RowIndex, ColumnIndex, paragraphIds, nestedTableIds);
}
