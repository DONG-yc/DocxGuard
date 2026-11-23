using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DocxGuard.Cli;
using DocxGuard.Core.Indexing;

namespace DocxGuard.Tests;

public sealed class CliPlanningTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "docxguard-plan-" + Guid.NewGuid().ToString("N"));
    public CliPlanningTests() => Directory.CreateDirectory(directory);

    [Theory]
    [InlineData("replacement")]
    [InlineData("")]
    [InlineData("中文\t第二项\n新行")]
    public void PlanAndDryRun_RealFiles_PreserveSourceAndPublishReportOnly(string replacement)
    {
        using MemoryStream sample = RebuildTestPackage.Create();
        string source = Write("模板.docx", sample.ToArray());
        var planned = Run("plan", source, "--find", "target", "--replace", replacement);
        Assert.Equal(0, planned.ExitCode);
        Assert.Empty(planned.Error);
        using JsonDocument plan = JsonDocument.Parse(planned.Output);
        Assert.Equal("docxguard.bound-plan.v1", plan.RootElement.GetProperty("profile").GetString());
        Assert.Equal(replacement, plan.RootElement.GetProperty("request").GetProperty("replacementText").GetString());
        Assert.Single(Directory.GetFiles(directory));
        string planPath = Write("plan.json", Encoding.UTF8.GetBytes(planned.Output));
        var dryRun = Run("dry-run", source, "--plan", planPath, "--json");
        Assert.Equal(0, dryRun.ExitCode);
        Assert.Empty(dryRun.Error);
        using JsonDocument report = JsonDocument.Parse(dryRun.Output);
        Assert.Equal("dry-run", report.RootElement.GetProperty("command").GetString());
        Assert.Equal("passed", report.RootElement.GetProperty("status").GetString());
        var build = report.RootElement.GetProperty("buildReport");
        Assert.Equal(plan.RootElement.GetProperty("sourcePackageSha256").GetString(), build.GetProperty("sourcePackageSha256").GetString());
        Assert.Equal(plan.RootElement.GetProperty("plan").GetProperty("id").GetString(), build.GetProperty("planId").GetString());
        Assert.Equal(64, build.GetProperty("outputPackageSha256").GetString()!.Length);
        Assert.All(build.GetProperty("checks").EnumerateArray(), c => Assert.Equal("passed", c.GetProperty("status").GetString()));
        Assert.Equal(sample.ToArray(), File.ReadAllBytes(source));
        Assert.Equal(2, Directory.GetFiles(directory).Length);
        var human = Run("dry-run", source, "--plan", planPath);
        Assert.Equal(0, human.ExitCode);
        Assert.Contains("no output DOCX was saved", human.Output);
    }

    [Fact]
    public void Plan_AmbiguousCandidates_CanBeScopedByParagraph()
    {
        using MemoryStream sample = RebuildTestPackage.Create(
            "<w:p><w:r><w:t>target</w:t></w:r></w:p><w:p><w:r><w:t>target</w:t></w:r></w:p>");
        string source = Write("input.docx", sample.ToArray());
        var ambiguous = Run("plan", source, "--find", "target", "--replace", "new", "--json");
        Assert.Equal(5, ambiguous.ExitCode);
        using var json = JsonDocument.Parse(ambiguous.Output);
        Assert.Equal(2, json.RootElement.GetProperty("candidateCount").GetInt32());
        string scope = json.RootElement.GetProperty("candidates")[1].GetProperty("paragraphId").GetString()!;
        Assert.Equal(0, Run("plan", source, "--find", "target", "--replace", "new", "--paragraph", scope).ExitCode);
        Assert.Equal(5, Run("plan", source, "--find", "missing", "--replace", "new", "--json").ExitCode);
    }

    [Fact]
    public void DryRun_UnsupportedRun_FailsAfterPlanningWithoutOutput()
    {
        using MemoryStream sample = RebuildTestPackage.Create("<w:p><w:r><w:t>target</w:t><w:tab/></w:r></w:p>");
        string source = Write("input.docx", sample.ToArray());
        var planned = Run("plan", source, "--find", "target", "--replace", "new");
        Assert.Equal(0, planned.ExitCode);
        string planPath = Write("plan.json", Encoding.UTF8.GetBytes(planned.Output));
        var dryRun = Run("dry-run", source, "--plan", planPath, "--json");
        Assert.Equal(4, dryRun.ExitCode);
        using var json = JsonDocument.Parse(dryRun.Output);
        var report = json.RootElement.GetProperty("buildReport");
        Assert.Equal("failed", report.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, report.GetProperty("outputPackageSha256").ValueKind);
        Assert.Equal(2, Directory.GetFiles(directory).Length);
    }

    [Fact]
    public void DryRun_SourceChanged_RejectsBeforeBuilding()
    {
        using MemoryStream sample = RebuildTestPackage.Create();
        string source = Write("input.docx", sample.ToArray());
        var planned = Run("plan", source, "--find", "target", "--replace", "new");
        string planPath = Write("plan.json", Encoding.UTF8.GetBytes(planned.Output));
        Write("input.docx", RebuildTestPackage.Rewrite(sample.ToArray(), e => e[RebuildTestPackage.Media] = [42]));
        AssertError(Run("dry-run", source, "--plan", planPath, "--json"), 4, "BPLAN_004");
    }

    [Theory]
    [InlineData("plan", "input.docx", "--find", "target", "--json")]
    [InlineData("plan", "input.docx", "--replace", "new", "--json")]
    [InlineData("plan", "input.docx", "--find", "target", "--replace", "new", "--replace", "again", "--json")]
    [InlineData("dry-run", "input.docx", "--json")]
    [InlineData("dry-run", "input.docx", "--plan", "plan.json", "--find", "target", "--json")]
    [InlineData("plan", "one.docx", "two.docx", "--find", "target", "--replace", "new", "--json")]
    public void Commands_InvalidArguments_ReturnUsageBeforeIo(params string[] args) =>
        AssertError(Run(args), 2, "DOCXGUARD_CLI_001");

    [Fact]
    public void DryRun_InvalidOrOversizedJson_Rejects()
    {
        using MemoryStream sample = RebuildTestPackage.Create();
        string source = Write("input.docx", sample.ToArray());
        string plan = Write("plan.json", Encoding.UTF8.GetBytes("{bad"));
        AssertError(Run("dry-run", source, "--plan", plan, "--json"), 4, "BPLAN_006");
        Write("plan.json", new byte[DocxGuard.Core.Planning.BoundPatchPlan.MaxSerializedBytes + 1]);
        AssertError(Run("dry-run", source, "--plan", plan, "--json"), 4, "BPLAN_002");
    }

    [Fact]
    public void Commands_FormatIoAndCancellation_HaveDistinctCodes()
    {
        AssertError(Run("plan", "bad.docm", "--find", "target", "--replace", "new", "--json"), 2, "DOCXGUARD_CLI_002");
        AssertError(Run("dry-run", Path.Combine(directory, "missing.docx"), "--plan", "plan.json", "--json"), 3, "DOCXGUARD_CLI_003");
        using StringWriter output = new();
        using StringWriter error = new();
        int code = new CliApplication("test").Run(["plan", "input.docx", "--find", "target", "--replace", "new", "--json"],
            output, error, new CancellationToken(true));
        AssertError(new(code, output.ToString(), error.ToString()), 130, "DOCXGUARD_CLI_004");
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("dry-run")]
    public void Commands_HelpAndRejectedSource_Work(string command)
    {
        Assert.Equal(0, Run(command, "--help").ExitCode);
        string source = Write("bad.docx", [1, 2, 3]);
        string[] args = command == "plan"
            ? [command, source, "--find", "target", "--replace", "new", "--json"]
            : [command, source, "--plan", "unused.json", "--json"];
        var result = Run(args);
        Assert.Equal(4, result.ExitCode);
        using var json = JsonDocument.Parse(result.Output);
        Assert.Equal("failed", json.RootElement.GetProperty("inspection").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("buildReport").ValueKind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("中文\t新行\n内容")]
    public async Task Process_PlanAndDryRun_ProduceJsonAndCorrectExitCodes(string replacement)
    {
        using MemoryStream sample = RebuildTestPackage.Create();
        string source = Write("process.docx", sample.ToArray());
        var planned = await RunProcess("plan", source, "--find", "target", "--replace", replacement);
        Assert.Equal(0, planned.ExitCode);
        Assert.Empty(planned.Error);
        using var plan = JsonDocument.Parse(planned.Output);
        Assert.Equal(replacement, plan.RootElement.GetProperty("request").GetProperty("replacementText").GetString());
        string planFile = Write("process-plan.json", Encoding.UTF8.GetBytes(planned.Output));
        var preview = await RunProcess("dry-run", source, "--plan", planFile, "--json");
        Assert.Equal(0, preview.ExitCode);
        Assert.Empty(preview.Error);
        using var json = JsonDocument.Parse(preview.Output);
        Assert.Equal("passed", json.RootElement.GetProperty("status").GetString());
        Assert.Equal(2, Directory.GetFiles(directory).Length);
    }

    internal static ProcessStartInfo CreateProcessStartInfo(params string[] args)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DocxGuard.sln"))) root = root.Parent;
        Assert.NotNull(root);
        string localHost = Path.Combine(root.FullName, ".dotnet", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        ProcessStartInfo start = new(File.Exists(localHost) ? localHost : "dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        string testAssembly = typeof(CliPlanningTests).Assembly.Location;
        foreach (string arg in new[] { "exec", "--runtimeconfig", Path.ChangeExtension(testAssembly, ".runtimeconfig.json"),
            "--depsfile", Path.ChangeExtension(testAssembly, ".deps.json"), typeof(CliApplication).Assembly.Location }.Concat(args))
            start.ArgumentList.Add(arg);
        return start;
    }

    internal static async Task<Result> RunProcess(params string[] args)
    {
        ProcessStartInfo start = CreateProcessStartInfo(args);
        using Process process = Process.Start(start)!;
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        try
        {
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(timeout.Token));
            return new(process.ExitCode, await stdout, await stderr);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public void Plan_InlineEmptyReplacement_SupportsDeletionWithoutShellEmptyArguments()
    {
        using MemoryStream sample = RebuildTestPackage.Create();
        string source = Write("inline.docx", sample.ToArray());
        var result = Run("plan", source, "--find=target", "--replace=");
        Assert.Equal(0, result.ExitCode);
        using var json = JsonDocument.Parse(result.Output);
        Assert.Equal("", json.RootElement.GetProperty("request").GetProperty("replacementText").GetString());
        AssertError(Run("plan", source, "--find=target", "--find", "target", "--replace=", "--json"), 2, "DOCXGUARD_CLI_001");
    }

    private static void AssertError(Result result, int code, string finding)
    {
        Assert.Equal(code, result.ExitCode);
        Assert.Empty(result.Error);
        using var json = JsonDocument.Parse(result.Output);
        Assert.Contains(json.RootElement.GetProperty("findings").EnumerateArray(), f => f.GetProperty("code").GetString() == finding);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("buildReport").ValueKind);
    }

    private string Write(string name, byte[] bytes)
    {
        string path = Path.Combine(directory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static Result Run(params string[] args)
    {
        using StringWriter output = new();
        using StringWriter error = new();
        int code = new CliApplication("test").Run(args, output, error);
        return new(code, output.ToString(), error.ToString());
    }

    internal sealed record Result(int ExitCode, string Output, string Error);
    public void Dispose()
    {
        foreach (string path in Directory.GetFiles(directory)) File.Delete(path);
        Directory.Delete(directory);
    }
}
