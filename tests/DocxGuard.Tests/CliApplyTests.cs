using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxGuard.Cli;
using DocxGuard.Core.Indexing;
using DocxGuard.Core.Packaging;
using DocxGuard.Core.Planning;

namespace DocxGuard.Tests;

public sealed class CliApplyTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "docxguard-apply-" + Guid.NewGuid().ToString("N"));
    public CliApplyTests() => Directory.CreateDirectory(directory);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Apply_ActualProcess_PublishesVerifiedFilesAndRejectsRepeat(bool strict)
    {
        var (source, plan) = Inputs(strict);
        byte[] original = File.ReadAllBytes(source);
        byte[] originalPlan = File.ReadAllBytes(plan);
        string target = Path.Combine(directory, "结果 output");
        var run = await CliPlanningTests.RunProcess("apply", source, "--plan", plan, "--output-dir", target, "--json");
        Assert.Equal(0, run.ExitCode);
        Assert.Empty(run.Error);
        using var json = JsonDocument.Parse(run.Output);
        Assert.Equal("published", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("passed", json.RootElement.GetProperty("buildReport").GetProperty("status").GetString());
        var publication = json.RootElement.GetProperty("publication");
        Assert.Equal("published", publication.GetProperty("status").GetString());
        string docxPath = publication.GetProperty("docxPath").GetString()!;
        string reportPath = publication.GetProperty("reportPath").GetString()!;
        Assert.Equal(Path.Combine(target, "output.docx"), docxPath);
        Assert.Equal(Path.Combine(target, "build-report.json"), reportPath);
        byte[] docxBytes = File.ReadAllBytes(docxPath);
        byte[] reportBytes = File.ReadAllBytes(reportPath);
        using var report = JsonDocument.Parse(reportBytes);
        Assert.Equal(Hash(docxBytes), report.RootElement.GetProperty("outputPackageSha256").GetString());
        Assert.Equal(Hash(reportBytes), publication.GetProperty("reportSha256").GetString());
        var inspection = new DocxIndexInspector().Inspect(docxPath);
        Assert.Equal(PackageInspectionStatus.Passed, inspection.Status);
        Assert.Contains("新名称\t第二项\n新行", inspection.Index!.PlainText);
        Assert.Equal(original, File.ReadAllBytes(source));
        Assert.Equal(originalPlan, File.ReadAllBytes(plan));
        Assert.Equal(2, Directory.GetFiles(target).Length);
        var retry = await CliPlanningTests.RunProcess("apply", source, "--plan", plan, "--output-dir", target, "--json");
        Assert.Equal(6, retry.ExitCode);
        Assert.Equal(docxBytes, File.ReadAllBytes(docxPath));
        Assert.Equal(reportBytes, File.ReadAllBytes(reportPath));
        AssertNoStaging();
    }

    [Theory]
    [InlineData("source")]
    [InlineData("plan")]
    [InlineData("existingDirectory")]
    [InlineData("missingParent")]
    public void Apply_UnsafeOrExistingDestination_RefusesPublication(string kind)
    {
        var (source, plan) = Inputs();
        byte[] beforeSource = File.ReadAllBytes(source);
        byte[] beforePlan = File.ReadAllBytes(plan);
        string target = kind switch
        {
            "source" => source,
            "plan" => plan,
            "existingDirectory" => directory,
            _ => Path.Combine(directory, "missing", "output"),
        };
        var result = Run("apply", source, "--plan", plan, "--output-dir", target, "--json");
        Assert.Equal(6, result.ExitCode);
        Assert.Empty(result.Error);
        using var json = JsonDocument.Parse(result.Output);
        Assert.Equal("passed", json.RootElement.GetProperty("buildReport").GetProperty("status").GetString());
        Assert.Equal("failed", json.RootElement.GetProperty("publication").GetProperty("status").GetString());
        Assert.Equal(beforeSource, File.ReadAllBytes(source));
        Assert.Equal(beforePlan, File.ReadAllBytes(plan));
        AssertNoStaging();
    }

    [Theory]
    [InlineData("source")]
    [InlineData("plan")]
    [InlineData("build")]
    public void Apply_RejectedInputOrBuild_CreatesNoOutput(string kind)
    {
        var (source, plan) = Inputs(complex: kind == "build");
        if (kind == "source") File.WriteAllBytes(source, [1, 2, 3]);
        if (kind == "plan") File.WriteAllText(plan, "{}");
        string target = Path.Combine(directory, "result");
        var result = Run("apply", source, "--plan", plan, "--output-dir", target, "--json");
        Assert.Equal(4, result.ExitCode);
        using var json = JsonDocument.Parse(result.Output);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("publication").ValueKind);
        Assert.False(Directory.Exists(target));
        AssertNoStaging();
    }

    [Theory]
    [InlineData("apply", "input.docx", "--plan", "plan.json", "--json")]
    [InlineData("apply", "input.docx", "--output-dir", "output", "--json")]
    [InlineData("apply", "input.docx", "--plan", "plan.json", "--output-dir=", "--json")]
    [InlineData("apply", "input.docx", "--plan", "plan.json", "--output-dir", "one", "--output-dir", "two", "--json")]
    [InlineData("apply", "input.docx", "--plan", "plan.json", "--output-dir", "one", "--force", "--json")]
    public void Apply_InvalidArguments_RejectsBeforeIo(params string[] args)
    {
        var result = Run(args);
        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.Error);
        Assert.Empty(Directory.GetFileSystemEntries(directory));
    }

    [Fact]
    public void Apply_CancelledBeforeBuild_Returns130WithoutFiles()
    {
        var (source, plan) = Inputs();
        using StringWriter output = new();
        using StringWriter error = new();
        int code = new CliApplication("test").Run(["apply", source, "--plan", plan,
            "--output-dir", Path.Combine(directory, "result"), "--json"], output, error, new CancellationToken(true));
        Assert.Equal(130, code);
        Assert.Single(Directory.GetFiles(directory, "*.docx"));
        AssertNoStaging();
    }

    [Fact]
    public void Apply_HumanOutputAndHelp_ShowSavedPathsAndNoOverwritePolicy()
    {
        var help = Run("apply", "--help");
        Assert.Equal(0, help.ExitCode);
        Assert.Contains("no overwrite", help.Output);
        var (source, plan) = Inputs();
        var result = Run("apply", source, "--plan=" + plan, "--output-dir=" + Path.Combine(directory, "human"));
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Error);
        Assert.Contains("output.docx", result.Output);
        Assert.Contains("build-report.json", result.Output);
        Assert.DoesNotContain("no output DOCX was saved", result.Output);
    }

    private (string Source, string Plan) Inputs(bool strict = false, bool complex = false)
    {
        using MemoryStream sample = RebuildTestPackage.Create(strict: strict,
            body: complex ? "<w:p><w:r><w:t>target</w:t><w:tab/></w:r></w:p>" : null);
        string source = Path.Combine(directory, "模板.docx");
        string plan = Path.Combine(directory, "plan.json");
        File.WriteAllBytes(source, sample.ToArray());
        var inspection = new DocxIndexInspector().Inspect(sample);
        var bound = BoundPatchPlan.Create(inspection, new("target", "新名称\t第二项\n新行")).Plan!;
        File.WriteAllText(plan, bound.ToJson(), new UTF8Encoding(false));
        return (source, plan);
    }

    private static CliPlanningTests.Result Run(params string[] args)
    {
        using StringWriter output = new();
        using StringWriter error = new();
        int code = new CliApplication("test").Run(args, output, error);
        return new(code, output.ToString(), error.ToString());
    }

    private void AssertNoStaging() => Assert.Empty(Directory.GetDirectories(directory, ".docxguard-*.tmp"));
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public void Dispose()
    {
        Assert.StartsWith(Path.Combine(Path.GetTempPath(), "docxguard-apply-"), Path.GetFullPath(directory));
        Directory.Delete(directory, recursive: true);
    }
}
