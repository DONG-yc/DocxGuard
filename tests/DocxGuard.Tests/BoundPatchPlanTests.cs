using System.Text;
using System.Text.Json.Nodes;
using DocxGuard.Core.Building;
using DocxGuard.Core.Indexing;
using DocxGuard.Core.Planning;

namespace DocxGuard.Tests;

public sealed class BoundPatchPlanTests
{
    [Theory]
    [InlineData(false, "中文\t新名称\n第二行")]
    [InlineData(true, "replacement")]
    [InlineData(false, "")]
    public void Plan_RoundTrip_RebuildsAndPreservesSource(bool strict, string replacement)
    {
        using MemoryStream source = RebuildTestPackage.Create(strict: strict);
        byte[] before = source.ToArray();
        var inspection = new DocxIndexInspector().Inspect(source);
        BoundPatchPlan plan = BoundPatchPlan.Create(inspection, new("target", replacement)).Plan!;
        BoundPlanResult loaded = BoundPatchPlan.Parse(Encoding.UTF8.GetBytes(plan.ToJson()), inspection);
        Assert.True(loaded.Passed);
        Assert.Equal(plan.ToJson(), loaded.Plan!.ToJson());
        source.Position = 5;
        var built = new DocxBuildPipeline().BuildBound(source, loaded.Plan);
        Assert.Equal(DocxBuildStatus.Passed, built.Status);
        Assert.Equal(plan.SourcePackageSha256, built.Report.SourcePackageSha256);
        Assert.Equal(plan.Plan.Id, built.Report.PlanId);
        Assert.Equal(5, source.Position);
        Assert.Equal(before, source.ToArray());
    }

    [Theory]
    [InlineData("version", "BPLAN_003")]
    [InlineData("profile", "BPLAN_003")]
    [InlineData("source", "BPLAN_004")]
    [InlineData("part", "BPLAN_005")]
    [InlineData("replacement", "BPLAN_005")]
    [InlineData("id", "BPLAN_005")]
    [InlineData("offset", "BPLAN_005")]
    [InlineData("run", "BPLAN_005")]
    [InlineData("unknown", "BPLAN_005")]
    [InlineData("missing", "BPLAN_006")]
    [InlineData("null", "BPLAN_005")]
    public void Parse_TamperedFields_RejectWithoutPublishingPlan(string field, string code)
    {
        using MemoryStream source = RebuildTestPackage.Create();
        var inspection = new DocxIndexInspector().Inspect(source);
        JsonNode json = JsonNode.Parse(BoundPatchPlan.Create(inspection, new("target", "replacement")).Plan!.ToJson())!;
        switch (field)
        {
            case "version": json["schemaVersion"] = "2.0"; break;
            case "profile": json["profile"] = "other"; break;
            case "source": json["sourcePackageSha256"] = new string('0', 64); break;
            case "part": json["mainDocumentPartName"] = "/other.xml"; break;
            case "replacement": json["request"]!["replacementText"] = "tampered"; break;
            case "id": json["plan"]!["id"] = "plan-forged"; break;
            case "offset": json["plan"]!["operations"]![0]!["precondition"]!["startOffset"] = 0; break;
            case "run": json["plan"]!["operations"]![0]!["precondition"]!["expectedRunSlices"]![0]!["length"] = 99; break;
            case "unknown": json["unexpected"] = true; break;
            case "missing": json.AsObject().Remove("request"); break;
            case "null": json["request"]!["replacementText"] = null; break;
        }

        var result = BoundPatchPlan.Parse(Encoding.UTF8.GetBytes(json.ToJsonString()), inspection);
        Assert.False(result.Passed);
        Assert.Null(result.Plan);
        Assert.Equal(code, Assert.Single(result.Findings).Code);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":\"1.0\",\"schemaVersion\":\"1.0\"}")]
    [InlineData("{\"request\":{\"a\":1,\"\\u0061\":2}}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{ /* comment */ }")]
    [InlineData("{\"a\":1,}")]
    public void Parse_InvalidJson_Rejects(string json)
    {
        using MemoryStream source = RebuildTestPackage.Create();
        var result = BoundPatchPlan.Parse(Encoding.UTF8.GetBytes(json), new DocxIndexInspector().Inspect(source));
        Assert.False(result.Passed);
        Assert.Equal("BPLAN_006", Assert.Single(result.Findings).Code);
    }

    [Fact]
    public void Parse_ReformattedJsonAndUtf8Bom_AreAccepted()
    {
        using MemoryStream source = RebuildTestPackage.Create();
        var inspection = new DocxIndexInspector().Inspect(source);
        string json = BoundPatchPlan.Create(inspection, new("target", "replacement")).Plan!.ToJson();
        var reordered = new JsonObject();
        foreach (var pair in JsonNode.Parse(json)!.AsObject().Reverse()) reordered.Add(pair.Key, pair.Value?.DeepClone());
        byte[] bytes = [0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes(reordered.ToJsonString())];
        Assert.True(BoundPatchPlan.Parse(bytes, inspection).Passed);
    }

    [Fact]
    public void ParseAndBuild_ChangedMediaWithIdenticalMainText_RejectsSource()
    {
        using MemoryStream source = RebuildTestPackage.Create();
        var inspection = new DocxIndexInspector().Inspect(source);
        var bound = BoundPatchPlan.Create(inspection, new("target", "replacement")).Plan!;
        using MemoryStream changed = new(RebuildTestPackage.Rewrite(source.ToArray(), e => e[RebuildTestPackage.Media] = [1, 2, 3]));
        var read = BoundPatchPlan.Parse(Encoding.UTF8.GetBytes(bound.ToJson()), new DocxIndexInspector().Inspect(changed));
        Assert.Equal("BPLAN_004", Assert.Single(read.Findings).Code);
        changed.Position = 3;
        var build = new DocxBuildPipeline().BuildBound(changed, bound);
        Assert.Equal(DocxBuildStatus.Failed, build.Status);
        Assert.Null(build.Artifact);
        Assert.Equal(DocxBuildFindingCodes.BoundSourceMismatch, Assert.Single(build.Report.Findings).Code);
        Assert.Equal(3, changed.Position);
    }

    [Fact]
    public void Parse_SizeDepthAndEncodingLimits_AreEnforced()
    {
        using MemoryStream source = RebuildTestPackage.Create();
        var inspection = new DocxIndexInspector().Inspect(source);
        Assert.Equal("BPLAN_002", Assert.Single(BoundPatchPlan.Parse(new byte[BoundPatchPlan.MaxSerializedBytes + 1], inspection).Findings).Code);
        Assert.Equal("BPLAN_006", Assert.Single(BoundPatchPlan.Parse(new byte[] { 0xff }, inspection).Findings).Code);
        Assert.Equal("BPLAN_006", Assert.Single(BoundPatchPlan.Parse(Encoding.UTF8.GetBytes(new string('[', 40) + new string(']', 40)), inspection).Findings).Code);
    }

    [Fact]
    public void Create_UnresolvedAndInvalidSource_NeverPublishesPlan()
    {
        using MemoryStream source = RebuildTestPackage.Create();
        var inspection = new DocxIndexInspector().Inspect(source);
        var missing = BoundPatchPlan.Create(inspection, new("not found", "replacement"));
        Assert.Null(missing.Plan);
        Assert.Equal(TextPatchPlanningStatus.NoMatch, missing.Planning!.Status);
        using MemoryStream invalid = new(new byte[] { 1, 2, 3 });
        var bad = new DocxIndexInspector().Inspect(invalid);
        Assert.Equal("BPLAN_001", Assert.Single(BoundPatchPlan.Create(bad, new("target", "replacement")).Findings).Code);
        Assert.Equal("BPLAN_001", Assert.Single(BoundPatchPlan.Parse(new byte[] { }, bad).Findings).Code);
        Assert.Throws<OperationCanceledException>(() => BoundPatchPlan.Parse(new byte[] { }, inspection, new CancellationToken(true)));
    }
}
