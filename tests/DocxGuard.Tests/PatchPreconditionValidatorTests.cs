using DocxGuard.Core.Execution;
using DocxGuard.Core.Indexing;
using DocxGuard.Core.Packaging;
using DocxGuard.Core.Planning;

namespace DocxGuard.Tests;

public sealed class PatchPreconditionValidatorTests
{
    private readonly PatchPreconditionValidator validator = new();

    [Fact]
    public void Validate_UnchangedIndex_Passes()
    {
        WordDocumentIndex index = CreateIndex(
            "<w:p><w:r><w:t>target</w:t></w:r></w:p>");
        PatchPlan plan = CreatePlan(index, "target", "replacement");

        PatchPreconditionValidationResult result = validator.Validate(
            index,
            plan);

        Assert.Equal(PatchPreconditionValidationStatus.Passed, result.Status);
        Assert.Equal(plan.Id, result.PlanId);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void Validate_ChangedParagraphTextAndHash_Fail()
    {
        WordDocumentIndex planned = CreateIndex(
            "<w:p><w:r><w:t>target suffix</w:t></w:r></w:p>");
        WordDocumentIndex current = CreateIndex(
            "<w:p><w:r><w:t>target changed</w:t></w:r></w:p>");
        PatchPlan plan = CreatePlan(planned, "target", "replacement");

        PatchPreconditionValidationResult result = validator.Validate(
            current,
            plan);

        Assert.Equal(PatchPreconditionValidationStatus.Failed, result.Status);
        Assert.Contains(
            result.Findings,
            finding => finding.Code
                == PatchPreconditionFindingCodes.ParagraphTextMismatch);
        Assert.Contains(
            result.Findings,
            finding => finding.Code
                == PatchPreconditionFindingCodes.ParagraphHashMismatch);
    }

    [Fact]
    public void Validate_MissingParagraph_Fails()
    {
        WordDocumentIndex planned = CreateIndex("""
            <w:p><w:r><w:t>keep</w:t></w:r></w:p>
            <w:p><w:r><w:t>target</w:t></w:r></w:p>
            """);
        WordDocumentIndex current = CreateIndex(
            "<w:p><w:r><w:t>keep</w:t></w:r></w:p>");
        PatchPlan plan = CreatePlan(planned, "target", "replacement");

        PatchPreconditionValidationResult result = validator.Validate(
            current,
            plan);

        PatchPreconditionFinding finding = Assert.Single(result.Findings);
        Assert.Equal(PatchPreconditionFindingCodes.ParagraphMissing, finding.Code);
        Assert.Equal("p000002", finding.ParagraphId);
    }

    [Fact]
    public void Validate_TargetMovedWithinParagraph_FailsRange()
    {
        WordDocumentIndex planned = CreateIndex(
            "<w:p><w:r><w:t>prefix target</w:t></w:r></w:p>");
        WordDocumentIndex current = CreateIndex(
            "<w:p><w:r><w:t>target prefix</w:t></w:r></w:p>");
        PatchPlan plan = CreatePlan(planned, "target", "replacement");

        PatchPreconditionValidationResult result = validator.Validate(
            current,
            plan);

        Assert.Contains(
            result.Findings,
            finding => finding.Code
                == PatchPreconditionFindingCodes.TargetRangeMismatch);
    }

    [Fact]
    public void Validate_SameTextWithDifferentRunBoundaries_FailsSlices()
    {
        WordDocumentIndex planned = CreateIndex("""
            <w:p><w:r><w:t>tar</w:t></w:r><w:r><w:t>get</w:t></w:r></w:p>
            """);
        WordDocumentIndex current = CreateIndex(
            "<w:p><w:r><w:t>target</w:t></w:r></w:p>");
        PatchPlan plan = CreatePlan(planned, "target", "replacement");

        PatchPreconditionValidationResult result = validator.Validate(
            current,
            plan);

        PatchPreconditionFinding finding = Assert.Single(result.Findings);
        Assert.Equal(
            PatchPreconditionFindingCodes.RunSlicesMismatch,
            finding.Code);
    }

    [Fact]
    public void Validate_CancellationAndNullArguments_Throw()
    {
        WordDocumentIndex index = CreateIndex(
            "<w:p><w:r><w:t>target</w:t></w:r></w:p>");
        PatchPlan plan = CreatePlan(index, "target", "replacement");
        using CancellationTokenSource source = new();
        source.Cancel();

        Assert.Throws<ArgumentNullException>(
            () => validator.Validate(null!, plan));
        Assert.Throws<ArgumentNullException>(
            () => validator.Validate(index, null!));
        Assert.Throws<OperationCanceledException>(
            () => validator.Validate(index, plan, source.Token));
    }

    private static PatchPlan CreatePlan(
        WordDocumentIndex index,
        string expected,
        string replacement) =>
        new TextPatchPlanner().CreatePlan(
            index,
            new TextReplacementRequest(expected, replacement)).Plan!;

    private static WordDocumentIndex CreateIndex(string bodyContent)
    {
        using MemoryStream stream = IndexTestPackage.WordStream(bodyContent);
        DocxIndexInspectionResult result = new DocxIndexInspector().Inspect(stream);
        Assert.Equal(PackageInspectionStatus.Passed, result.Status);
        return result.Index!;
    }
}
