using DocxGuard.Core.Indexing;
using DocxGuard.Core.Packaging;
using DocxGuard.Core.Planning;
using DocxGuard.Core.Wordprocessing;

namespace DocxGuard.Core.Execution;

/// <summary>
/// Applies a verified text plan to an in-memory main-document XML copy.
/// It never rewrites or repacks the source DOCX.
/// </summary>
public sealed class InMemoryTextPatchExecutor
{
    private readonly PatchPreconditionValidator preconditionValidator = new();

    public TextPatchExecutionResult Execute(
        DocxIndexInspectionResult source,
        PatchPlan plan,
        TextPatchExecutionLimits? executionLimits = null,
        WordprocessingInspectionLimits? wordprocessingLimits = null,
        DocumentIndexLimits? indexLimits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(plan);
        executionLimits ??= TextPatchExecutionLimits.Default;
        wordprocessingLimits ??= WordprocessingInspectionLimits.Default;
        indexLimits ??= DocumentIndexLimits.Default;
        executionLimits.Validate();
        wordprocessingLimits.Validate();
        indexLimits.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        if (source.Status != PackageInspectionStatus.Passed
            || source.Index is null
            || source.MainDocumentXml is null)
        {
            return Failure(
                plan,
                null,
                TextPatchExecutionFindingCodes.SourceInspectionNotPassed,
                "Execution requires a passed document index and verified XML snapshot.");
        }

        if (plan.Operations.Count > executionLimits.MaxOperations)
        {
            return Failure(
                plan,
                null,
                TextPatchExecutionFindingCodes.ResourceLimitExceeded,
                $"The plan exceeds the operation limit of {executionLimits.MaxOperations}.");
        }


        PatchPreconditionValidationResult validation =
            preconditionValidator.Validate(
                source.Index,
                plan,
                cancellationToken);
        if (validation.Status == PatchPreconditionValidationStatus.Failed)
        {
            return Failure(
                plan,
                validation,
                TextPatchExecutionFindingCodes.PreconditionsFailed,
                "The current document index no longer satisfies the PatchPlan preconditions.");
        }

        WordMainDocumentInfo main = source.DocumentInspection.MainDocument!;
        ReadOnlyMemory<byte> sourceXml = source.MainDocumentXml.Value;
        MainDocumentMutationResult mutation = MainDocumentTextMutator.Apply(
            sourceXml,
            main.Conformance,
            source.Index,
            plan,
            wordprocessingLimits,
            executionLimits,
            cancellationToken);
        if (mutation.Xml is null)
        {
            return new TextPatchExecutionResult(
                plan.Id,
                validation,
                null,
                [mutation.Finding!]);
        }

        WordMainDocumentParseResult structure = WordMainDocumentParser.Parse(
            mutation.Xml,
            main.Part.PartName,
            main.Conformance,
            wordprocessingLimits,
            cancellationToken);
        DocumentIndexParseResult parsed = WordDocumentIndexer.Parse(
            mutation.Xml,
            main.Part.PartName,
            main.Conformance,
            wordprocessingLimits,
            indexLimits,
            cancellationToken);
        if (structure.Structure is null
            || parsed.Index is null
            || !VerifyPostconditions(
                source.Index,
                parsed.Index,
                plan,
                cancellationToken))
        {
            return Failure(
                plan,
                validation,
                TextPatchExecutionFindingCodes.PostconditionFailed,
                "The patched XML failed re-indexing or did not satisfy the exact logical postconditions.",
                plan.Operations.Count == 1 ? plan.Operations[0] : null);
        }

        InMemoryMainDocumentPatch artifact = new(
            plan,
            source.SourcePackageSha256!,
            main.Part.PartName.Value,
            main.Conformance,
            sourceXml.Span,
            mutation.Xml);
        return new TextPatchExecutionResult(
            plan.Id,
            validation,
            artifact,
            []);
    }

    private static bool VerifyPostconditions(
        WordDocumentIndex source,
        WordDocumentIndex patched,
        PatchPlan plan,
        CancellationToken cancellationToken)
    {
        if (source.Paragraphs.Count != patched.Paragraphs.Count
            || source.Tables.Count != patched.Tables.Count)
        {
            return false;
        }

        var expectedParagraphs = PatchTextProjection.ExpectedParagraphs(plan);
        var editsByParagraph = PatchTextProjection.RunEditsByParagraph(plan);

        for (int index = 0; index < source.Paragraphs.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IndexedParagraph before = source.Paragraphs[index];
            IndexedParagraph after = patched.Paragraphs[index];
            string expectedText = expectedParagraphs.GetValueOrDefault(before.Id) ?? before.Text;
            editsByParagraph.TryGetValue(before.Id, out var runEdits);
            if (before.Id != after.Id
                || before.Ordinal != after.Ordinal
                || before.TableCellId != after.TableCellId
                || after.Text != expectedText
                || before.Runs.Count != after.Runs.Count)
            {
                return false;
            }

            for (int runIndex = 0; runIndex < before.Runs.Count; runIndex++)
            {
                IndexedRun beforeRun = before.Runs[runIndex];
                IndexedRun afterRun = after.Runs[runIndex];
                string expectedRunText = runEdits is not null
                    && runEdits.TryGetValue(runIndex, out var edits)
                    ? PatchTextProjection.Apply(beforeRun.Text, edits)
                    : beforeRun.Text;
                if (beforeRun.Id != afterRun.Id
                    || beforeRun.Ordinal != afterRun.Ordinal
                    || beforeRun.IsVisible != afterRun.IsVisible
                    || afterRun.Text != expectedRunText)
                {
                    return false;
                }
            }
        }

        return TablesEqual(source.Tables, patched.Tables);
    }


    private static bool TablesEqual(
        IReadOnlyList<IndexedTable> before,
        IReadOnlyList<IndexedTable> after)
    {
        for (int tableIndex = 0; tableIndex < before.Count; tableIndex++)
        {
            IndexedTable left = before[tableIndex];
            IndexedTable right = after[tableIndex];
            if (left.Id != right.Id
                || left.Ordinal != right.Ordinal
                || left.ParentCellId != right.ParentCellId
                || left.RowCount != right.RowCount
                || left.Cells.Count != right.Cells.Count)
            {
                return false;
            }

            for (int cellIndex = 0; cellIndex < left.Cells.Count; cellIndex++)
            {
                IndexedTableCell leftCell = left.Cells[cellIndex];
                IndexedTableCell rightCell = right.Cells[cellIndex];
                if (leftCell.Id != rightCell.Id
                    || leftCell.TableId != rightCell.TableId
                    || leftCell.RowIndex != rightCell.RowIndex
                    || leftCell.ColumnIndex != rightCell.ColumnIndex
                    || !leftCell.ParagraphIds.SequenceEqual(
                        rightCell.ParagraphIds)
                    || !leftCell.NestedTableIds.SequenceEqual(
                        rightCell.NestedTableIds))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static TextPatchExecutionResult Failure(
        PatchPlan plan,
        PatchPreconditionValidationResult? validation,
        string code,
        string message,
        TextPatchOperation? operation = null) =>
        new(
            plan.Id,
            validation,
            null,
            [
                new TextPatchExecutionFinding(
                    code,
                    FindingSeverity.Error,
                    message,
                    operation?.Id,
                    operation?.Precondition.ParagraphId),
            ]);
}
