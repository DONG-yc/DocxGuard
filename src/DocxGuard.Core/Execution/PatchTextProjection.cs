using System.Text;
using DocxGuard.Core.Planning;

namespace DocxGuard.Core.Execution;

// All ranges refer to the original source. Each replacement is inserted in
// its first affected run, preserving the existing single-edit format policy.
internal static class PatchTextProjection
{
    internal sealed record Edit(int Start, int End, string Replacement, TextPatchOperation Operation);

    internal static Dictionary<string, Dictionary<int, Edit[]>> RunEditsByParagraph(PatchPlan plan) =>
        plan.Operations.GroupBy(operation => operation.Precondition.ParagraphId, StringComparer.Ordinal)
            .ToDictionary(paragraph => paragraph.Key, paragraph => paragraph
                .SelectMany(operation => operation.Precondition.ExpectedRunSlices.Select((slice, index) =>
                    (slice.RunOrdinal, Edit: new Edit(slice.StartOffsetInRun, slice.EndOffsetInRun,
                        index == 0 ? operation.ReplacementText : string.Empty, operation))))
                .GroupBy(item => item.RunOrdinal)
                .ToDictionary(run => run.Key, run => run.Select(item => item.Edit)
                    .OrderBy(edit => edit.Start).ToArray()), StringComparer.Ordinal);

    internal static Dictionary<string, string> ExpectedParagraphs(PatchPlan plan)
    {
        Dictionary<string, string> expected = new(StringComparer.Ordinal);
        foreach (var paragraph in plan.Operations.GroupBy(operation => operation.Precondition.ParagraphId))
        {
            string original = paragraph.First().Precondition.ExpectedParagraphText;
            Edit[] edits = paragraph.OrderBy(operation => operation.Precondition.StartOffset)
                .Select(operation => new Edit(operation.Precondition.StartOffset,
                    operation.Precondition.EndOffset, operation.ReplacementText, operation)).ToArray();
            if (edits.Any(edit => edit.Operation.Precondition.ExpectedParagraphText != original)
                || edits.Zip(edits.Skip(1)).Any(pair => pair.First.End > pair.Second.Start))
                continue;

            expected.Add(paragraph.Key, Apply(original, edits));
        }

        return expected;
    }

    internal static string Apply(string original, IEnumerable<Edit> edits)
    {
        StringBuilder result = new();
        int cursor = 0;
        foreach (Edit edit in edits)
        {
            result.Append(original, cursor, edit.Start - cursor);
            result.Append(edit.Replacement);
            cursor = edit.End;
        }

        return result.Append(original, cursor, original.Length - cursor).ToString();
    }
}
