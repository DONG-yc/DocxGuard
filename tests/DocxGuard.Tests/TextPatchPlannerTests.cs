using System.Security.Cryptography;
using System.Text;
using DocxGuard.Core.Indexing;
using DocxGuard.Core.Packaging;
using DocxGuard.Core.Planning;

namespace DocxGuard.Tests;

public sealed class TextPatchPlannerTests
{
    private readonly TextPatchPlanner planner = new();

    [Fact]
    public void CreatePlan_UniqueCrossRunTarget_PublishesStrongPreconditions()
    {
        WordDocumentIndex index = CreateIndex("""
            <w:p>
              <w:r><w:t xml:space="preserve">prefix </w:t></w:r>
              <w:r><w:t>tar</w:t></w:r>
              <w:r><w:t>get</w:t></w:r>
              <w:r><w:t xml:space="preserve"> suffix</w:t></w:r>
            </w:p>
            """);
        TextReplacementRequest request = new("target", "replacement");

        TextPatchPlanningResult result = planner.CreatePlan(index, request);

        Assert.Equal(TextPatchPlanningStatus.Ready, result.Status);
        Assert.Empty(result.Findings);
        PatchPlan plan = Assert.IsType<PatchPlan>(result.Plan);
        Assert.StartsWith("plan-", plan.Id, StringComparison.Ordinal);
        Assert.Equal(37, plan.Id.Length);

        TextPatchOperation operation = Assert.Single(plan.Operations);
        Assert.Equal("op000001", operation.Id);
        Assert.Equal(PatchOperationKind.ReplaceText, operation.Kind);
        Assert.Equal("m000001", operation.SourceMatchId);
        Assert.Equal("replacement", operation.ReplacementText);

        TextPatchPrecondition condition = operation.Precondition;
        Assert.Equal("p000001", condition.ParagraphId);
        Assert.Equal("prefix target suffix", condition.ExpectedParagraphText);
        Assert.Equal(7, condition.StartOffset);
        Assert.Equal(13, condition.EndOffset);
        Assert.Equal("target", condition.ExpectedText);
        Assert.Equal(
            ComputeSha256(condition.ExpectedParagraphText),
            condition.ExpectedParagraphTextSha256);
        Assert.Equal(
            ["p000001/r0002", "p000001/r0003"],
            condition.ExpectedRunSlices.Select(slice => slice.RunId));
    }

    [Fact]
    public void CreatePlan_IsDeterministicAndReplacementSensitive()
    {
        WordDocumentIndex index = CreateIndex(
            "<w:p><w:r><w:t>target</w:t></w:r></w:p>");

        PatchPlan first = planner.CreatePlan(
            index,
            new TextReplacementRequest("target", "first")).Plan!;
        PatchPlan repeated = planner.CreatePlan(
            index,
            new TextReplacementRequest("target", "first")).Plan!;
        PatchPlan different = planner.CreatePlan(
            index,
            new TextReplacementRequest("target", "second")).Plan!;

        Assert.Equal(first.Id, repeated.Id);
        Assert.NotEqual(first.Id, different.Id);
    }

    [Fact]
    public void CreatePlan_AmbiguousTarget_PublishesCandidatesButNoPlan()
    {
        WordDocumentIndex index = CreateIndex("""
            <w:p><w:r><w:t>target</w:t></w:r></w:p>
            <w:p><w:r><w:t>target</w:t></w:r></w:p>
            """);

        TextPatchPlanningResult result = planner.CreatePlan(
            index,
            new TextReplacementRequest("target", "replacement"));

        Assert.Equal(TextPatchPlanningStatus.Ambiguous, result.Status);
        Assert.Null(result.Plan);
        Assert.Equal(2, result.Location.Matches.Count);
        Assert.Contains(
            result.Findings,
            finding => finding.Code
                == TextPatchPlanningFindingCodes.AmbiguousMatch
                && finding.Severity == FindingSeverity.Warning);
    }

    [Fact]
    public void CreatePlan_ParagraphScope_ResolvesAmbiguity()
    {
        WordDocumentIndex index = CreateIndex("""
            <w:p><w:r><w:t>target</w:t></w:r></w:p>
            <w:p><w:r><w:t>target</w:t></w:r></w:p>
            """);

        TextPatchPlanningResult result = planner.CreatePlan(
            index,
            new TextReplacementRequest(
                "target",
                "replacement",
                ParagraphScopeId: "p000002"));

        Assert.Equal(TextPatchPlanningStatus.Ready, result.Status);
        Assert.Equal(
            "p000002",
            result.Plan!.Operations[0].Precondition.ParagraphId);
    }

    [Fact]
    public void CreatePlan_NoMatch_PublishesNoPlan()
    {
        WordDocumentIndex index = CreateIndex("<w:p/>");

        TextPatchPlanningResult result = planner.CreatePlan(
            index,
            new TextReplacementRequest("missing", "replacement"));

        Assert.Equal(TextPatchPlanningStatus.NoMatch, result.Status);
        Assert.Null(result.Plan);
        Assert.Empty(result.Location.Matches);
        Assert.Contains(
            result.Findings,
            finding => finding.Code == TextPatchPlanningFindingCodes.NoMatch);
    }

    [Fact]
    public void CreatePlan_MissingScope_IsRejected()
    {
        WordDocumentIndex index = CreateIndex("<w:p/>");

        TextPatchPlanningResult result = planner.CreatePlan(
            index,
            new TextReplacementRequest(
                "target",
                "replacement",
                ParagraphScopeId: "p999999"));

        Assert.Equal(TextPatchPlanningStatus.Rejected, result.Status);
        Assert.Null(result.Plan);
        Assert.Equal(TextLocationStatus.Rejected, result.Location.Status);
        Assert.Contains(
            result.Findings,
            finding => finding.Code
                == TextPatchPlanningFindingCodes.LocationRejected);
    }

    [Fact]
    public void CreatePlan_EmptyReplacement_CreatesDeletionPlan()
    {
        WordDocumentIndex index = CreateIndex(
            "<w:p><w:r><w:t>remove me</w:t></w:r></w:p>");

        TextPatchPlanningResult result = planner.CreatePlan(
            index,
            new TextReplacementRequest("remove me", ""));

        Assert.Equal(TextPatchPlanningStatus.Ready, result.Status);
        Assert.Empty(result.Plan!.Operations[0].ReplacementText);
    }

    [Fact]
    public void CreatePlan_NoOp_IsRejected()
    {
        WordDocumentIndex index = CreateIndex(
            "<w:p><w:r><w:t>same</w:t></w:r></w:p>");

        TextPatchPlanningResult result = planner.CreatePlan(
            index,
            new TextReplacementRequest("same", "same"));

        AssertRejected(result, TextPatchPlanningFindingCodes.NoOp);
    }

    [Theory]
    [InlineData("line\rbreak")]
    [InlineData("invalid\0character")]
    [InlineData("object\uFFFCmarker")]
    public void CreatePlan_InvalidReplacementText_IsRejected(
        string replacement)
    {
        WordDocumentIndex index = CreateIndex(
            "<w:p><w:r><w:t>target</w:t></w:r></w:p>");

        TextPatchPlanningResult result = planner.CreatePlan(
            index,
            new TextReplacementRequest("target", replacement));

        AssertRejected(
            result,
            TextPatchPlanningFindingCodes.InvalidReplacementText);
    }

    [Fact]
    public void CreatePlan_UnpairedSurrogateReplacement_IsRejected()
    {
        WordDocumentIndex index = CreateIndex(
            "<w:p><w:r><w:t>target</w:t></w:r></w:p>");
        string replacement = new(['x', '\uD800', 'y']);

        TextPatchPlanningResult result = planner.CreatePlan(
            index,
            new TextReplacementRequest("target", replacement));

        AssertRejected(
            result,
            TextPatchPlanningFindingCodes.InvalidReplacementText);
    }

    [Fact]
    public void CreatePlan_ObjectTarget_IsRejected()
    {
        WordDocumentIndex index = CreateIndex("""
            <w:p><w:r><w:drawing/><w:t>target</w:t></w:r></w:p>
            """);

        TextPatchPlanningResult result = planner.CreatePlan(
            index,
            new TextReplacementRequest("\uFFFCtarget", "replacement"));

        AssertRejected(
            result,
            TextPatchPlanningFindingCodes.UnsupportedTarget);
    }

    [Fact]
    public void CreatePlan_QueryReplacementAndCandidateLimits_AreEnforced()
    {
        WordDocumentIndex index = CreateIndex(
            "<w:p><w:r><w:t>aaaa</w:t></w:r></w:p>");

        TextPatchPlanningResult query = planner.CreatePlan(
            index,
            new TextReplacementRequest("aa", "x"),
            TextPatchPlanningLimits.Default with
            {
                MaxExpectedTextCharacters = 1,
            });
        TextPatchPlanningResult replacement = planner.CreatePlan(
            index,
            new TextReplacementRequest("a", "xx"),
            TextPatchPlanningLimits.Default with
            {
                MaxReplacementTextCharacters = 1,
            });
        TextPatchPlanningResult candidates = planner.CreatePlan(
            index,
            new TextReplacementRequest("a", "x"),
            TextPatchPlanningLimits.Default with
            {
                MaxCandidateMatches = 2,
            });

        AssertRejected(query, TextPatchPlanningFindingCodes.ResourceLimitExceeded);
        AssertRejected(
            replacement,
            TextPatchPlanningFindingCodes.ResourceLimitExceeded);
        AssertRejected(
            candidates,
            TextPatchPlanningFindingCodes.ResourceLimitExceeded);
    }

    [Fact]
    public void CreatePlan_InvalidLimitsNullTextAndCancellation_Throw()
    {
        WordDocumentIndex index = CreateIndex("<w:p/>");
        using CancellationTokenSource source = new();
        source.Cancel();

        Assert.Throws<ArgumentNullException>(
            () => planner.CreatePlan(
                index,
                new TextReplacementRequest(null!, "x")));
        Assert.Throws<ArgumentNullException>(
            () => planner.CreatePlan(
                index,
                new TextReplacementRequest("x", null!)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => planner.CreatePlan(
                index,
                new TextReplacementRequest("x", "y"),
                TextPatchPlanningLimits.Default with
                {
                    MaxCandidateMatches = 0,
                }));
        Assert.Throws<OperationCanceledException>(
            () => planner.CreatePlan(
                index,
                new TextReplacementRequest("x", "y"),
                cancellationToken: source.Token));
    }

    private static WordDocumentIndex CreateIndex(string bodyContent)
    {
        using MemoryStream stream = IndexTestPackage.WordStream(bodyContent);
        DocxIndexInspectionResult result = new DocxIndexInspector().Inspect(stream);
        Assert.Equal(PackageInspectionStatus.Passed, result.Status);
        return result.Index!;
    }

    private static string ComputeSha256(string value) =>
        Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static void AssertRejected(
        TextPatchPlanningResult result,
        string findingCode)
    {
        Assert.Equal(TextPatchPlanningStatus.Rejected, result.Status);
        Assert.Null(result.Plan);
        Assert.Contains(
            result.Findings,
            finding => finding.Code == findingCode
                && finding.Severity == FindingSeverity.Error);
    }
}
