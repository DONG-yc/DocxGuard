using System.Text;
using System.Text.Json.Nodes;
using DocxGuard.Core.Indexing;
using DocxGuard.Core.Planning;

namespace DocxGuard.Tests;

public sealed class BoundMultiPatchPlanTests
{
    [Fact]
    public void Create_TwoNonOverlappingRequests_IsDeterministicAndSourceBound()
    {
        using MemoryStream source = RebuildTestPackage.Create(
            "<w:p><w:r><w:t>alpha beta gamma</w:t></w:r></w:p>");
        var inspection = new DocxIndexInspector().Inspect(source);
        TextReplacementRequest[] requests =
        [
            new("alpha", "A"),
            new("gamma", "G"),
        ];

        MultiPatchPlanResult result = BoundMultiPatchPlan.Create(inspection, requests);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
        Assert.Equal(2, result.Plan!.Plan.Operations.Count);
        Assert.Equal("op000002", result.Plan.Plan.Operations[0].Id);
        Assert.Equal("op000001", result.Plan.Plan.Operations[1].Id);
        Assert.Equal(result.Plan.ToJson(), BoundMultiPatchPlan.Parse(
            Encoding.UTF8.GetBytes(result.Plan.ToJson()), inspection).Plan!.ToJson());
    }

    [Fact]
    public void Create_OverlappingRanges_RejectsWholeBatch()
    {
        using MemoryStream source = RebuildTestPackage.Create(
            "<w:p><w:r><w:t>alpha beta gamma</w:t></w:r></w:p>");
        var inspection = new DocxIndexInspector().Inspect(source);

        MultiPatchPlanResult result = BoundMultiPatchPlan.Create(inspection,
        [
            new("alpha beta", "first"),
            new("beta", "second"),
        ]);

        Assert.False(result.Passed);
        MultiPlanFinding finding = Assert.Single(result.Findings);
        Assert.Equal(MultiPlanFindingCodes.OverlappingTargets, finding.Code);
        Assert.Equal(0, finding.RequestIndex);
        Assert.Equal(1, finding.ConflictingRequestIndex);
        Assert.Null(result.Plan);
    }

    [Fact]
    public void Create_UnresolvedRequest_RejectsEvenWhenAnotherRequestIsReady()
    {
        using MemoryStream source = RebuildTestPackage.Create(
            "<w:p><w:r><w:t>alpha beta</w:t></w:r></w:p>");
        var inspection = new DocxIndexInspector().Inspect(source);

        MultiPatchPlanResult result = BoundMultiPatchPlan.Create(inspection,
        [new("alpha", "A"), new("missing", "M")]);

        Assert.False(result.Passed);
        Assert.Contains(result.Findings, finding => finding.Code == TextPatchPlanningFindingCodes.NoMatch
            && finding.RequestIndex == 1);
    }

    [Fact]
    public void Parse_TamperedOperationOrUnknownField_Rejects()
    {
        using MemoryStream source = RebuildTestPackage.Create(
            "<w:p><w:r><w:t>alpha beta</w:t></w:r></w:p>");
        var inspection = new DocxIndexInspector().Inspect(source);
        BoundMultiPatchPlan plan = BoundMultiPatchPlan.Create(inspection,
            [new("alpha", "A"), new("beta", "B")]).Plan!;
        JsonObject json = JsonNode.Parse(plan.ToJson())!.AsObject();
        json["plan"]!["operations"]![0]!["replacementText"] = "forged";
        json["unexpected"] = true;

        MultiPatchPlanResult result = BoundMultiPatchPlan.Parse(
            Encoding.UTF8.GetBytes(json.ToJsonString()), inspection);

        Assert.False(result.Passed);
        Assert.Equal(MultiPlanFindingCodes.InvalidJson, Assert.Single(result.Findings).Code);
    }

    [Fact]
    public void Parse_SourceMismatch_IsRejectedBeforeRegeneration()
    {
        using MemoryStream original = RebuildTestPackage.Create(
            "<w:p><w:r><w:t>alpha beta</w:t></w:r></w:p>");
        using MemoryStream changed = RebuildTestPackage.Create(
            "<w:p><w:r><w:t>alpha changed</w:t></w:r></w:p>");
        var originalInspection = new DocxIndexInspector().Inspect(original);
        var changedInspection = new DocxIndexInspector().Inspect(changed);
        BoundMultiPatchPlan plan = BoundMultiPatchPlan.Create(originalInspection,
            [new("alpha", "A"), new("beta", "B")]).Plan!;

        MultiPatchPlanResult result = BoundMultiPatchPlan.Parse(
            Encoding.UTF8.GetBytes(plan.ToJson()), changedInspection);

        Assert.False(result.Passed);
        Assert.Equal(MultiPlanFindingCodes.SourceMismatch, Assert.Single(result.Findings).Code);
    }
}
