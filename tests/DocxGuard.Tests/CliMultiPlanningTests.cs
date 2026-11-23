using System.Text;
using System.Text.Json;
using DocxGuard.Cli;

namespace DocxGuard.Tests;

public sealed class CliMultiPlanningTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "docxguard-multi-plan-" + Guid.NewGuid().ToString("N"));

    public CliMultiPlanningTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void MultiPlan_ValidRequests_EmitsBoundPlanJson()
    {
        string source = Write("input.docx", RebuildTestPackage.Create(
            "<w:p><w:r><w:t>alpha beta gamma</w:t></w:r></w:p>").ToArray());
        string requests = Write("requests.json", Encoding.UTF8.GetBytes("""
            [
              { "expectedText": "alpha", "replacementText": "A" },
              { "expectedText": "gamma", "replacementText": "G" }
            ]
            """));

        Result result = Run("multi-plan", source, "--requests", requests);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Error);
        using JsonDocument plan = JsonDocument.Parse(result.Output);
        Assert.Equal("docxguard.multi-bound-plan.v1", plan.RootElement.GetProperty("profile").GetString());
        Assert.Equal(2, plan.RootElement.GetProperty("requests").GetArrayLength());
        Assert.Equal(2, plan.RootElement.GetProperty("plan").GetProperty("operations").GetArrayLength());
    }

    [Fact]
    public void MultiPlan_Conflict_ReturnsUnresolvedJsonWithoutPlan()
    {
        string source = Write("input.docx", RebuildTestPackage.Create(
            "<w:p><w:r><w:t>alpha beta gamma</w:t></w:r></w:p>").ToArray());
        string requests = Write("conflict.json", Encoding.UTF8.GetBytes("""
            [
              { "expectedText": "alpha beta", "replacementText": "first" },
              { "expectedText": "beta", "replacementText": "second" }
            ]
            """));

        Result result = Run("multi-plan", source, "--requests", requests, "--json");

        Assert.Equal(4, result.ExitCode);
        using JsonDocument report = JsonDocument.Parse(result.Output);
        Assert.Equal("rejected", report.RootElement.GetProperty("status").GetString());
        Assert.Contains(report.RootElement.GetProperty("findings").EnumerateArray(),
            finding => finding.GetProperty("code").GetString() == "MPLAN_004");
    }

    [Fact]
    public void MultiPlan_MissingArguments_ReturnsUsageJson()
    {
        Result result = Run("multi-plan", "missing.docx", "--json");

        Assert.Equal(2, result.ExitCode);
        using JsonDocument report = JsonDocument.Parse(result.Output);
        Assert.Contains(report.RootElement.GetProperty("findings").EnumerateArray(),
            finding => finding.GetProperty("code").GetString() == "DOCXGUARD_CLI_001");
    }

    private Result Run(params string[] args)
    {
        using StringWriter output = new();
        using StringWriter error = new();
        int code = new CliApplication("test").Run(args, output, error);
        return new(code, output.ToString(), error.ToString());
    }

    private string Write(string name, byte[] bytes)
    {
        string path = Path.Combine(directory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public void Dispose()
    {
        foreach (string path in Directory.GetFiles(directory)) File.Delete(path);
        Directory.Delete(directory);
    }

    private sealed record Result(int ExitCode, string Output, string Error);
}
