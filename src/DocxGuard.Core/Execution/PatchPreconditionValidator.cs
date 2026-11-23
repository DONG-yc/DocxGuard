using System.Security.Cryptography;
using System.Text;
using DocxGuard.Core.Indexing;
using DocxGuard.Core.Packaging;
using DocxGuard.Core.Planning;

namespace DocxGuard.Core.Execution;

public sealed class PatchPreconditionValidator
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public PatchPreconditionValidationResult Validate(
        WordDocumentIndex currentIndex,
        PatchPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentIndex);
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();

        List<PatchPreconditionFinding> findings = [];
        foreach (TextPatchOperation operation in plan.Operations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateOperation(
                currentIndex,
                operation,
                findings);
        }

        foreach (var paragraph in plan.Operations.GroupBy(operation => operation.Precondition.ParagraphId))
        {
            int end = 0;
            foreach (TextPatchOperation operation in paragraph.OrderBy(operation => operation.Precondition.StartOffset))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (operation.Precondition.StartOffset < end)
                {
                    findings.Add(Error(PatchPreconditionFindingCodes.OverlappingTargets,
                        "The planned target overlaps another operation in this paragraph.", operation));
                }

                end = Math.Max(end, operation.Precondition.EndOffset);
            }
        }

        return new PatchPreconditionValidationResult(plan.Id, findings);
    }

    private static void ValidateOperation(
        WordDocumentIndex currentIndex,
        TextPatchOperation operation,
        List<PatchPreconditionFinding> findings)
    {
        TextPatchPrecondition expected = operation.Precondition;
        if (!currentIndex.TryGetParagraph(
            expected.ParagraphId,
            out IndexedParagraph? current))
        {
            findings.Add(Error(
                PatchPreconditionFindingCodes.ParagraphMissing,
                "The planned paragraph no longer exists in the current index.",
                operation));
            return;
        }

        if (!string.Equals(
            current!.Text,
            expected.ExpectedParagraphText,
            StringComparison.Ordinal))
        {
            findings.Add(Error(
                PatchPreconditionFindingCodes.ParagraphTextMismatch,
                "The current paragraph text differs from the planned snapshot.",
                operation));
        }

        string currentHash = Convert.ToHexString(
            SHA256.HashData(StrictUtf8.GetBytes(current.Text)))
            .ToLowerInvariant();
        if (!string.Equals(
            currentHash,
            expected.ExpectedParagraphTextSha256,
            StringComparison.Ordinal))
        {
            findings.Add(Error(
                PatchPreconditionFindingCodes.ParagraphHashMismatch,
                "The current paragraph hash differs from the planned snapshot.",
                operation));
        }

        bool rangeValid = expected.StartOffset >= 0
            && expected.StartOffset
                <= current.Text.Length - expected.ExpectedText.Length
            && string.Equals(
                current.Text.Substring(
                    expected.StartOffset,
                    expected.ExpectedText.Length),
                expected.ExpectedText,
                StringComparison.Ordinal);
        if (!rangeValid)
        {
            findings.Add(Error(
                PatchPreconditionFindingCodes.TargetRangeMismatch,
                "The expected text is no longer present at the planned range.",
                operation));
            return;
        }

        RunTextSlice[] currentSlices = RunTextSliceMapper.Map(
            current,
            expected.StartOffset,
            expected.ExpectedText.Length);
        if (!currentSlices.SequenceEqual(expected.ExpectedRunSlices))
        {
            findings.Add(Error(
                PatchPreconditionFindingCodes.RunSlicesMismatch,
                "The current run boundaries differ from the planned slices.",
                operation));
        }
    }

    private static PatchPreconditionFinding Error(
        string code,
        string message,
        TextPatchOperation operation) =>
        new(
            code,
            FindingSeverity.Error,
            message,
            operation.Id,
            operation.Precondition.ParagraphId);
}
