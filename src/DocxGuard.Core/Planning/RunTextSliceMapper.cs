using DocxGuard.Core.Indexing;

namespace DocxGuard.Core.Planning;

internal static class RunTextSliceMapper
{
    public static RunTextSlice[] Map(
        IndexedParagraph paragraph,
        int startOffset,
        int length)
    {
        int endOffset = startOffset + length;
        return paragraph.Runs
            .Where(run => run.IsVisible
                && run.Length != 0
                && run.StartOffset < endOffset
                && run.EndOffset > startOffset)
            .Select(run =>
            {
                int sliceStart = Math.Max(startOffset, run.StartOffset);
                int sliceEnd = Math.Min(endOffset, run.EndOffset);
                return new RunTextSlice(
                    run.Id,
                    run.Ordinal,
                    sliceStart,
                    sliceStart - run.StartOffset,
                    sliceEnd - sliceStart);
            })
            .ToArray();
    }
}
