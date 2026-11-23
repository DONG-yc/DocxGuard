using System.Collections.ObjectModel;

namespace DocxGuard.Core.Planning;

public sealed record RunTextSlice(
    string RunId,
    int RunOrdinal,
    int ParagraphStartOffset,
    int StartOffsetInRun,
    int Length)
{
    public int ParagraphEndOffset => ParagraphStartOffset + Length;

    public int EndOffsetInRun => StartOffsetInRun + Length;
}

public sealed class TextMatch
{
    internal TextMatch(
        string id,
        string paragraphId,
        int paragraphOrdinal,
        string? tableCellId,
        int startOffset,
        string text,
        IEnumerable<RunTextSlice> runSlices)
    {
        Id = id;
        ParagraphId = paragraphId;
        ParagraphOrdinal = paragraphOrdinal;
        TableCellId = tableCellId;
        StartOffset = startOffset;
        Text = text;
        RunSlices = new ReadOnlyCollection<RunTextSlice>(
            runSlices.ToArray());

        if (StartOffset < 0 || Text.Length == 0 || RunSlices.Count == 0)
        {
            throw new InvalidOperationException(
                "A text match must contain one non-empty mapped range.");
        }

        int expectedOffset = StartOffset;
        foreach (RunTextSlice slice in RunSlices)
        {
            if (slice.Length <= 0
                || slice.StartOffsetInRun < 0
                || slice.ParagraphStartOffset != expectedOffset)
            {
                throw new InvalidOperationException(
                    "Run slices must be positive, ordered, and contiguous.");
            }

            expectedOffset = slice.ParagraphEndOffset;
        }

        if (expectedOffset != StartOffset + Text.Length)
        {
            throw new InvalidOperationException(
                "Run slices must cover the complete matched text.");
        }
    }

    public string Id { get; }

    public string ParagraphId { get; }

    public int ParagraphOrdinal { get; }

    public string? TableCellId { get; }

    public int StartOffset { get; }

    public int Length => Text.Length;

    public int EndOffset => StartOffset + Length;

    public string Text { get; }

    public IReadOnlyList<RunTextSlice> RunSlices { get; }
}
