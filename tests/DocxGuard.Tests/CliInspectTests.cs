using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxGuard.Cli;

namespace DocxGuard.Tests;

public sealed class CliInspectTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "docxguard-inspect-" + Guid.NewGuid().ToString("N"));

    public CliInspectTests() => Directory.CreateDirectory(directory);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Inspect_ValidDocument_ReportsStructureAndLeavesInputUnchanged(bool strict)
    {
        using MemoryStream source = RebuildTestPackage.Create(strict: strict);
        string path = Write(source.ToArray(), "中文 sample.DOCX");
        byte[] before = File.ReadAllBytes(path);
        var run = Run("inspect", path, "--json");
        Assert.Equal(0, run.ExitCode);
        Assert.Empty(run.Error);
        using JsonDocument json = JsonDocument.Parse(run.Output);
        var root = json.RootElement;
        Assert.Equal("1.0", root.GetProperty("schemaVersion").GetString());
        Assert.Equal("inspect", root.GetProperty("command").GetString());
        Assert.Equal("passed", root.GetProperty("status").GetString());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(before)).ToLowerInvariant(), root.GetProperty("sourcePackageSha256").GetString());
        var summary = root.GetProperty("summary");
        Assert.Equal("/word/document.xml", summary.GetProperty("mainDocumentPartName").GetString());
        Assert.Equal(strict ? "Strict" : "Transitional", summary.GetProperty("conformance").GetString());
        Assert.Equal(2, summary.GetProperty("paragraphCount").GetInt32());
        Assert.Equal(3, summary.GetProperty("runCount").GetInt32());
        Assert.Equal(3, summary.GetProperty("relationshipCount").GetInt32());
        Assert.Equal("pre target post", root.GetProperty("paragraphs").GetProperty("items")[0].GetProperty("textPreview").GetString());
        Assert.All(root.GetProperty("checks").EnumerateArray(), c => Assert.Equal("passed", c.GetProperty("status").GetString()));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(directory));
        Assert.Equal(run.Output, Run("inspect", path, "--json").Output);
    }

    [Fact]
    public void Inspect_Paging_ReportsStableParagraphIdentityAndEmptyOutOfRangePage()
    {
        using MemoryStream source = RebuildTestPackage.Create();
        string path = Write(source.ToArray());
        using var first = JsonDocument.Parse(Run("inspect", "--limit", "1", "--json", path).Output);
        var page = first.RootElement.GetProperty("paragraphs");
        Assert.True(page.GetProperty("hasMore").GetBoolean());
        string? firstId = page.GetProperty("items")[0].GetProperty("id").GetString();
        using var second = JsonDocument.Parse(Run("inspect", path, "--offset", "1", "--limit", "1", "--json").Output);
        var secondPage = second.RootElement.GetProperty("paragraphs");
        Assert.False(secondPage.GetProperty("hasMore").GetBoolean());
        Assert.NotEqual(firstId, secondPage.GetProperty("items")[0].GetProperty("id").GetString());
        Assert.Equal("untouched", secondPage.GetProperty("items")[0].GetProperty("textPreview").GetString());
        using var empty = JsonDocument.Parse(Run("inspect", path, "--offset", int.MaxValue.ToString(), "--json").Output);
        Assert.Equal(0, empty.RootElement.GetProperty("paragraphs").GetProperty("items").GetArrayLength());
        Assert.False(empty.RootElement.GetProperty("paragraphs").GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public void Inspect_TableAndUnicodePreview_RetainsCellIdentityAndSurrogatePairs()
    {
        string text = new string('中', 199) + "😀tail";
        using MemoryStream source = RebuildTestPackage.Create(
            $"<w:tbl><w:tr><w:tc><w:p><w:r><w:t>{text}</w:t></w:r></w:p></w:tc></w:tr></w:tbl>");
        var run = Run("inspect", Write(source.ToArray()), "--json");
        Assert.Equal(0, run.ExitCode);
        using var json = JsonDocument.Parse(run.Output);
        Assert.Equal(1, json.RootElement.GetProperty("summary").GetProperty("tableCount").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("summary").GetProperty("cellCount").GetInt32());
        var p = json.RootElement.GetProperty("paragraphs").GetProperty("items")[0];
        Assert.Equal(new string('中', 199), p.GetProperty("textPreview").GetString());
        Assert.Equal(text.Length, p.GetProperty("textLength").GetInt32());
        Assert.True(p.GetProperty("truncated").GetBoolean());
        Assert.NotNull(p.GetProperty("tableCellId").GetString());
    }

    [Fact]
    public void Inspect_TextOutput_IsReadableAndEscapesSourceControlCharacters()
    {
        using MemoryStream source = RebuildTestPackage.Create(
            "<w:p><w:r><w:t>中文</w:t><w:tab/><w:t>next</w:t><w:br/><w:t>line</w:t></w:r></w:p><w:p/>");
        string path = Write(source.ToArray());
        var run = Run("inspect", path, "--limit", "1");
        Assert.Equal(0, run.ExitCode);
        Assert.Empty(run.Error);
        Assert.Contains("中文\\tnext\\nline", run.Output);
        Assert.Contains("More paragraphs: use --offset 1", run.Output);
        Assert.Contains("Runs: 1", run.Output);
        Assert.DoesNotContain('\u001b', run.Output);
    }

    [Theory]
    [InlineData("zip", "package")]
    [InlineData("[Content_Types].xml", "contentTypes")]
    [InlineData("_rels/.rels", "relationships")]
    [InlineData("word/document.xml", "word")]
    public void Inspect_RejectedInput_ReturnsLayerFindingsAndNoPartialSummary(string target, string stage)
    {
        using MemoryStream valid = RebuildTestPackage.Create();
        byte[] bytes = target == "zip" ? [1, 2, 3] : RebuildTestPackage.Rewrite(valid.ToArray(),
            entries => entries[target] = Encoding.UTF8.GetBytes("<invalid/>"));
        string path = Write(bytes);
        var run = Run("inspect", path, "--json");
        Assert.Equal(4, run.ExitCode);
        Assert.Empty(run.Error);
        using var json = JsonDocument.Parse(run.Output);
        Assert.Equal("failed", json.RootElement.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("summary").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("paragraphs").ValueKind);
        Assert.Contains(json.RootElement.GetProperty("findings").EnumerateArray(), f => f.GetProperty("stage").GetString() == stage);
        var checks = json.RootElement.GetProperty("checks").EnumerateArray().ToArray();
        Assert.Equal("failed", Assert.Single(checks, c => c.GetProperty("stage").GetString() == stage).GetProperty("status").GetString());
        Assert.Equal("notRun", checks[^1].GetProperty("status").GetString());
        var text = Run("inspect", path);
        Assert.Equal(4, text.ExitCode);
        Assert.Empty(text.Output);
        Assert.Contains("Inspection: failed", text.Error);
    }

    [Theory]
    [InlineData("--limit", "0")]
    [InlineData("--limit", "1001")]
    [InlineData("--offset", "-1")]
    [InlineData("--offset", "2147483648")]
    [InlineData("--offset", "1.5")]
    [InlineData("--offset", "+1")]
    [InlineData("--format", "xml")]
    public void Inspect_InvalidOptions_ReturnUsageJsonBeforeOpeningInput(string option, string value)
    {
        var run = Run("inspect", "missing.docx", option, value, "--json");
        AssertError(run, 2, "DOCXGUARD_CLI_001");
    }

    [Theory]
    [InlineData("inspect", "--json")]
    [InlineData("inspect", "missing.docx", "--json", "--json")]
    [InlineData("inspect", "a.docx", "b.docx", "--json")]
    [InlineData("inspect", "missing.docx", "--json", "--limit")]
    [InlineData("inspect", "missing.docx", "--offset", "0", "--offset", "1", "--json")]
    public void Inspect_MissingOrDuplicateArguments_ReturnUsageJson(params string[] arguments) =>
        AssertError(Run(arguments), 2, "DOCXGUARD_CLI_001");

    [Fact]
    public void Inspect_UnsupportedFormatAndMissingFile_HaveDistinctExitCodes()
    {
        AssertError(Run("inspect", "missing.docm", "--json"), 2, "DOCXGUARD_CLI_002");
        AssertError(Run("inspect", Path.Combine(directory, "missing.docx"), "--json"), 3, "DOCXGUARD_CLI_003");
    }

    [Fact]
    public void Inspect_LockedFile_ReturnsIoDiagnostic()
    {
        using MemoryStream valid = RebuildTestPackage.Create();
        string path = Write(valid.ToArray());
        using FileStream locked = new(path, FileMode.Open, FileAccess.Read, FileShare.None);
        AssertError(Run("inspect", path, "--json"), 3, "DOCXGUARD_CLI_003");
    }

    [Fact]
    public void Inspect_Cancelled_Returns130WithoutOpeningInput()
    {
        using StringWriter output = new();
        using StringWriter error = new();
        int exitCode = new CliApplication("test").Run(["inspect", "missing.docx", "--json"],
            output, error, new CancellationToken(true));
        AssertError(new(exitCode, output.ToString(), error.ToString()), 130, "DOCXGUARD_CLI_004");
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public void Inspect_CommandHelp_DocumentsOptionsAndExitCodes(string help)
    {
        var run = Run("inspect", help);
        Assert.Equal(0, run.ExitCode);
        Assert.Contains("--offset N", run.Output);
        Assert.Contains("130 cancelled", run.Output);
        Assert.Empty(run.Error);
    }

    [Fact]
    public void Inspect_OptionTerminator_AcceptsInputAndStopsOptionParsing()
    {
        using MemoryStream source = RebuildTestPackage.Create();
        Assert.Equal(0, Run("inspect", "--json", "--", Write(source.ToArray(), "--file.docx")).ExitCode);
        AssertError(Run("inspect", "--json", "--", "--json"), 2, "DOCXGUARD_CLI_002");
    }

    private static void AssertError(RunResult result, int exitCode, string code)
    {
        Assert.Equal(exitCode, result.ExitCode);
        Assert.Empty(result.Error);
        using JsonDocument json = JsonDocument.Parse(result.Output);
        Assert.Equal(exitCode, json.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Equal(code, json.RootElement.GetProperty("findings")[0].GetProperty("code").GetString());
        Assert.All(json.RootElement.GetProperty("checks").EnumerateArray(), c => Assert.Equal("notRun", c.GetProperty("status").GetString()));
    }

    private string Write(byte[] bytes, string name = "input.docx")
    {
        string path = Path.Combine(directory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static RunResult Run(params string[] arguments)
    {
        using StringWriter output = new();
        using StringWriter error = new();
        int code = new CliApplication("test-version").Run(arguments, output, error);
        return new(code, output.ToString(), error.ToString());
    }

    private sealed record RunResult(int ExitCode, string Output, string Error);

    public void Dispose()
    {
        foreach (string path in Directory.GetFiles(directory)) File.Delete(path);
        Directory.Delete(directory);
    }
}
