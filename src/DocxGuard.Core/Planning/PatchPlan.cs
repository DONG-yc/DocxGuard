using System.Collections.ObjectModel;

namespace DocxGuard.Core.Planning;

public enum PatchOperationKind
{
    ReplaceText,
}

public sealed class TextPatchPrecondition
{
    internal TextPatchPrecondition(
        string paragraphId,
        string expectedParagraphText,
        string expectedParagraphTextSha256,
        int startOffset,
        string expectedText,
        IEnumerable<RunTextSlice> expectedRunSlices)
    {
        ParagraphId = paragraphId;
        ExpectedParagraphText = expectedParagraphText;
        ExpectedParagraphTextSha256 = expectedParagraphTextSha256;
        StartOffset = startOffset;
        ExpectedText = expectedText;
        ExpectedRunSlices = new ReadOnlyCollection<RunTextSlice>(
            expectedRunSlices.ToArray());

        if (StartOffset < 0
            || ExpectedText.Length == 0
            || StartOffset > ExpectedParagraphText.Length - ExpectedText.Length
            || !string.Equals(
                ExpectedParagraphText.Substring(StartOffset, ExpectedText.Length),
                ExpectedText,
                StringComparison.Ordinal)
            || ExpectedRunSlices.Sum(slice => slice.Length)
                != ExpectedText.Length)
        {
            throw new InvalidOperationException(
                "Text patch preconditions must describe one complete paragraph range.");
        }
    }

    public string ParagraphId { get; }

    public string ExpectedParagraphText { get; }

    public string ExpectedParagraphTextSha256 { get; }

    public int StartOffset { get; }

    public int Length => ExpectedText.Length;

    public int EndOffset => StartOffset + Length;

    public string ExpectedText { get; }

    public IReadOnlyList<RunTextSlice> ExpectedRunSlices { get; }
}

public sealed class TextPatchOperation
{
    internal TextPatchOperation(
        string id,
        string sourceMatchId,
        string replacementText,
        TextPatchPrecondition precondition)
    {
        Id = id;
        SourceMatchId = sourceMatchId;
        ReplacementText = replacementText;
        Precondition = precondition;
    }

    public string Id { get; }

    public PatchOperationKind Kind => PatchOperationKind.ReplaceText;

    public string SourceMatchId { get; }

    public string ReplacementText { get; }

    public TextPatchPrecondition Precondition { get; }
}

public sealed class PatchPlan
{
    internal static PatchPlan CreateInternal(string id, IEnumerable<TextPatchOperation> operations) =>
        new(id, operations);

    internal PatchPlan(
        string id,
        IEnumerable<TextPatchOperation> operations)
    {
        Id = id;
        Operations = new ReadOnlyCollection<TextPatchOperation>(
            operations.ToArray());
        if (Operations.Count == 0)
        {
            throw new InvalidOperationException(
                "A patch plan must contain at least one operation.");
        }
    }

    public string Id { get; }

    public IReadOnlyList<TextPatchOperation> Operations { get; }
}
