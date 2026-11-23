using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxGuard.Core.Building;
using DocxGuard.Core.Indexing;
using DocxGuard.Core.Planning;
using DocxGuard.Core.Verification;

namespace DocxGuard.Tests;

public sealed class MultiPatchBuildTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "docxguard-batch-" + Guid.NewGuid().ToString("N"));

    public MultiPatchBuildTests() => Directory.CreateDirectory(directory);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MultiPlan_DryRunAndApply_PublishOneVerifiedBatch(bool strict)
    {
        using var sample = RebuildTestPackage.Create("""
            <w:p><w:r><w:rPr><w:b/></w:rPr><w:t>alpha beta gam</w:t></w:r><w:r><w:t>ma ends</w:t></w:r></w:p>
            <w:p><w:r><w:t>title</w:t></w:r></w:p>
            """, strict: strict);
        byte[] original = sample.ToArray();
        string source = Path.Combine(directory, "template.docx");
        File.WriteAllBytes(source, original);
        string requests = Path.Combine(directory, "requests.json");
        File.WriteAllText(requests, """
            [
              {"expectedText":"alpha","replacementText":"新版\tA\nB"},
              {"expectedText":"beta ","replacementText":""},
              {"expectedText":"gamma","replacementText":"G"},
              {"expectedText":"title","replacementText":"header"}
            ]
            """);
        var planned = await CliPlanningTests.RunProcess("multi-plan", source, "--requests", requests);
        Assert.Equal(0, planned.ExitCode);
        string plan = Path.Combine(directory, "batch.json");
        File.WriteAllText(plan, planned.Output);

        var preview = await CliPlanningTests.RunProcess("dry-run", source, "--plan", plan, "--json");
        Assert.Equal(0, preview.ExitCode);
        Assert.Empty(Directory.GetDirectories(directory));
        using var dryRun = JsonDocument.Parse(preview.Output);
        var evidence = dryRun.RootElement.GetProperty("buildReport");
        Assert.Equal("docxguard.multi-text.v1", evidence.GetProperty("profile").GetString());
        var operations = evidence.GetProperty("operations").EnumerateArray().ToArray();
        Assert.Equal(4, operations.Length);
        Assert.All(operations, operation => Assert.Equal(
            operation.GetProperty("expectedOutputParagraphSha256").GetString(),
            operation.GetProperty("actualOutputParagraphSha256").GetString()));
        Assert.Equal(2, operations.Select(o => o.GetProperty("actualOutputParagraphSha256").GetString()).Distinct().Count());
        Assert.Equal(1, evidence.GetProperty("changedEntryCount").GetInt32());

        string target = Path.Combine(directory, "result");
        var applied = await CliPlanningTests.RunProcess("apply", source, "--plan", plan, "--output-dir", target, "--json");
        Assert.Equal(0, applied.ExitCode);
        Assert.Equal(2, Directory.GetFiles(target).Length);
        string output = Path.Combine(target, "output.docx");
        using var report = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(target, "build-report.json")));
        string hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(output)));
        Assert.Equal(hash, report.RootElement.GetProperty("outputPackageSha256").GetString());
        Assert.Equal(hash, evidence.GetProperty("outputPackageSha256").GetString());
        var inspection = new DocxIndexInspector().Inspect(output);
        Assert.Equal(new[] { "新版\tA\nB G ends", "header" }, inspection.Index!.Paragraphs.Select(p => p.Text));
        Assert.Equal(original, File.ReadAllBytes(source));
        Assert.Equal(planned.Output, File.ReadAllText(plan));

        var retry = await CliPlanningTests.RunProcess("apply", source, "--plan", plan, "--output-dir", target, "--json");
        Assert.Equal(6, retry.ExitCode);
        Assert.Equal(hash, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(output))));
        Assert.Empty(Directory.GetDirectories(directory, ".docxguard-*.tmp"));
    }

    [Theory]
    [InlineData("unsupported", "EXEC_003")]
    [InlineData("changed-source", "MPLAN_005")]
    [InlineData("tampered-plan", "MPLAN_007")]
    public async Task Apply_RejectedBatch_PublishesNothing(string scenario, string expectedCode)
    {
        // The second paragraph is edited first in the Day 15 plan order.
        using var sample = RebuildTestPackage.Create("""
            <w:p><w:r><w:t>alpha</w:t><w:tab/></w:r></w:p>
            <w:p><w:r><w:t>beta</w:t></w:r></w:p>
            """);
        var inspection = new DocxIndexInspector().Inspect(sample);
        BoundMultiPatchPlan bound = BoundMultiPatchPlan.Create(inspection,
            [new("alpha", "A"), new("beta", "B")]).Plan!;
        byte[] original = sample.ToArray();
        if (scenario == "changed-source")
            original = RebuildTestPackage.Rewrite(original, entries => entries[RebuildTestPackage.Media][0] ^= 1);
        JsonNode json = JsonNode.Parse(bound.ToJson())!;
        if (scenario == "tampered-plan")
            json["plan"]!["operations"]![0]!["replacementText"] = "forged";
        string source = Path.Combine(directory, "template.docx");
        string plan = Path.Combine(directory, "plan.json");
        File.WriteAllBytes(source, original);
        File.WriteAllText(plan, json.ToJsonString());

        string target = Path.Combine(directory, "result");
        var result = await CliPlanningTests.RunProcess("apply", source, "--plan", plan, "--output-dir", target, "--json");
        Assert.Equal(4, result.ExitCode);
        using var report = JsonDocument.Parse(result.Output);
        var root = report.RootElement;
        Assert.Contains(root.GetProperty("findings").EnumerateArray(),
            finding => finding.GetProperty("code").GetString() == expectedCode);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("publication").ValueKind);
        if (scenario == "unsupported")
        {
            var build = root.GetProperty("buildReport");
            Assert.Equal("failed", build.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, build.GetProperty("outputPackageSha256").ValueKind);
            Assert.Empty(build.GetProperty("entries").EnumerateArray());
        }

        Assert.Empty(Directory.GetDirectories(directory));
        Assert.Equal(original, File.ReadAllBytes(source));
    }

    [Fact]
    public void Build_OverlappingTypedPlan_ReportsFailedPrecondition()
    {
        using var sample = RebuildTestPackage.Create("<w:p><w:r><w:t>alpha beta</w:t></w:r></w:p>");
        var index = new DocxIndexInspector().Inspect(sample).Index!;
        TextPatchPlanner planner = new();
        var first = planner.CreatePlan(index, new("alpha beta", "A")).Plan!.Operations[0];
        var second = planner.CreatePlan(index, new("beta", "B")).Plan!.Operations[0];
        PatchPlan overlap = new("test-overlap", [first, second]);
        DocxBuildOptions options = new()
        {
            Execution = new() { MaxOperations = 2 },
        };
        var build = new DocxBuildPipeline().Build(sample, overlap, options);

        Assert.Null(build.Artifact);
        Assert.Contains(build.Report.Findings, finding => finding.Code == "PRE_006");
        Assert.All(build.Report.Operations, operation => Assert.Null(operation.ExpectedOutputParagraphSha256));
    }

    [Theory]
    [InlineData("{bad")]
    [InlineData("[{\"expectedText\":\"alpha\"}]")]
    public async Task MultiPlan_InvalidRequestFile_ReturnsStructuredRejection(string contents)
    {
        using var sample = RebuildTestPackage.Create();
        string source = Path.Combine(directory, "template.docx");
        string requests = Path.Combine(directory, "requests.json");
        File.WriteAllBytes(source, sample.ToArray());
        File.WriteAllText(requests, contents);

        var result = await CliPlanningTests.RunProcess("multi-plan", source, "--requests", requests, "--json");
        Assert.Equal(4, result.ExitCode);
        using var report = JsonDocument.Parse(result.Output);
        Assert.Equal("MPLAN_007", report.RootElement.GetProperty("findings")[0].GetProperty("code").GetString());
    }

    [Fact]
    public void Verify_BatchCandidateWithWrongText_RejectsTheEditedRun()
    {
        using var source = RebuildTestPackage.Create("<w:p><w:r><w:t>alpha beta</w:t></w:r></w:p>");
        var inspection = new DocxIndexInspector().Inspect(source);
        var plan = BoundMultiPatchPlan.Create(inspection, [new("alpha", "first"), new("beta", "second")]).Plan!;
        var build = new DocxBuildPipeline().BuildBoundMulti(source, plan);
        var patch = new DocxGuard.Core.Execution.InMemoryTextPatchExecutor().Execute(inspection, plan.Plan,
            executionLimits: new() { MaxOperations = 2 }).Artifact!;
        using MemoryStream tampered = new(RebuildTestPackage.Rewrite(build.Artifact!.Bytes.ToArray(), entries =>
            RebuildTestPackage.ReplaceText(entries, RebuildTestPackage.Main, ">second</", ">wrong</")));

        var diff = new DocxStructuralDiffVerifier().Verify(source, tampered, patch);

        Assert.Equal(StructuralDiffStatus.Failed, diff.Status);
        Assert.Equal(StructuralDiffFindingCodes.ExpectedTextMismatch, Assert.Single(diff.Findings).Code);
        Assert.Empty(diff.Entries);
    }

    public void Dispose()
    {
        Assert.StartsWith(Path.Combine(Path.GetTempPath(), "docxguard-batch-"), Path.GetFullPath(directory));
        Directory.Delete(directory, recursive: true);
    }
}
