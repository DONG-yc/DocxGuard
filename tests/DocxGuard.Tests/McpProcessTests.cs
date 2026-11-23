using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxGuard.Core.Indexing;

namespace DocxGuard.Tests;

public sealed class McpProcessTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "docxguard-mcp-" + Guid.NewGuid().ToString("N"));

    public McpProcessTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task DefaultHost_ListsReadOnlyTools_AndRejectsExternalPaths()
    {
        WriteSource();
        await using var host = await McpProcess.Start(directory);
        JsonElement list = await host.Request("tools/list", new { });
        var tools = list.GetProperty("tools").EnumerateArray().ToArray();
        Assert.Equal(new[] { "docxguard_dry_run", "docxguard_inspect", "docxguard_plan", "docxguard_table_figure_list", "docxguard_table_figure_preview" },
            tools.Select(tool => tool.GetProperty("name").GetString()).Order(StringComparer.Ordinal));
        Assert.All(tools, tool => Assert.True(tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean()));
        Assert.All(tools, tool => Assert.Equal("object", tool.GetProperty("inputSchema").GetProperty("type").GetString()));

        var inspection = await host.Call("docxguard_inspect", new { sourcePath = "input.docx", offset = 0, limit = 1 });
        Assert.Equal("passed", Success(inspection).GetProperty("status").GetString());
        Assert.Single(Success(inspection).GetProperty("paragraphs").GetProperty("items").EnumerateArray());
        foreach (string path in new[] { "../input.docx", @"..\input.docx", Path.Combine(directory, "input.docx"), "C:input.docx" })
            AssertError(await host.Call("docxguard_inspect", new { sourcePath = path }), "MCP_001");
        Assert.Single(Directory.GetFiles(directory));
        AssertError(await host.Call("docxguard_table_figure_preview",
            new { sourcePath = "input.docx", requestPath = "../request.json", figurePath = "figure.png" }), "MCP_001");
    }

    [Fact]
    public async Task ApplyHost_PlanPreviewAndPublish_UseTheSameBatchAndRejectTampering()
    {
        byte[] original = WriteSource();
        await using var host = await McpProcess.Start(directory, apply: true);
        var list = await host.Request("tools/list", new { });
        var apply = Assert.Single(list.GetProperty("tools").EnumerateArray(),
            tool => tool.GetProperty("name").GetString() == "docxguard_apply");
        Assert.False(apply.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());

        string plan = await Plan(host);
        var preview = await host.Call("docxguard_dry_run", new { sourcePath = "input.docx", planJson = plan });
        string? hash = Success(preview).GetProperty("buildReport").GetProperty("outputPackageSha256").GetString();
        Assert.NotNull(hash);
        Assert.Empty(Directory.GetDirectories(directory));

        JsonNode forged = JsonNode.Parse(plan)!;
        forged["plan"]!["operations"]![0]!["replacementText"] = "unauthorized";
        AssertError(await Apply(host, forged.ToJsonString(), "tampered"), "MPLAN_007");
        AssertError(await Apply(host, plan, "../outside"), "MCP_001");
        Assert.Empty(Directory.GetDirectories(directory));

        JsonElement published = Success(await Apply(host, plan, "result"));
        Assert.Equal("published", published.GetProperty("status").GetString());
        Assert.Equal(hash, published.GetProperty("buildReport").GetProperty("outputPackageSha256").GetString());
        string output = Path.Combine(directory, "result", "output.docx");
        var document = new DocxIndexInspector().Inspect(output);
        Assert.Equal(new[] { "新版 A", "B" }, document.Index!.Paragraphs.Select(paragraph => paragraph.Text));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(directory, "result")).Length);
        Assert.True((await Apply(host, plan, "result")).GetProperty("isError").GetBoolean());
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(directory, "input.docx")));
        Assert.Equal(Path.Combine(directory, "result"), Assert.Single(Directory.GetDirectories(directory)));
    }

    [Fact]
    public async Task ApplyHost_UnsupportedRun_RejectsEntireBatch()
    {
        byte[] original = WriteSource(unsupported: true);
        await using var host = await McpProcess.Start(directory, apply: true);
        string plan = await Plan(host);

        AssertError(await Apply(host, plan, "result"), "EXEC_003");
        Assert.Empty(Directory.GetDirectories(directory));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(directory, "input.docx")));
    }

    [Fact]
    public async Task PreviewWorkflow_UsesExplicitParagraphScope_AndRejectsStalePlan()
    {
        const string relativeSource = "ambiguous.docx";
        string sourcePath = Path.Combine(directory, relativeSource);
        using var fixture = RebuildTestPackage.Create("""
            <w:p><w:r><w:t>Heading: target</w:t></w:r></w:p>
            <w:p><w:r><w:t>Body: target</w:t></w:r></w:p>
            """);
        byte[] original = fixture.ToArray();
        File.WriteAllBytes(sourcePath, original);
        await using var host = await McpProcess.Start(directory);

        var firstPage = Success(await host.Call("docxguard_inspect", new
        {
            sourcePath = relativeSource, offset = 0, limit = 1,
        })).GetProperty("paragraphs");
        Assert.True(firstPage.GetProperty("hasMore").GetBoolean());
        Assert.Equal("Heading: target", firstPage.GetProperty("items")[0].GetProperty("textPreview").GetString());
        var ambiguous = await host.Call("docxguard_plan", new
        {
            sourcePath = relativeSource,
            requests = new[] { new { expectedText = "target", replacementText = "updated" } },
        });
        AssertError(ambiguous, "PLAN_002");

        var secondPage = Success(await host.Call("docxguard_inspect", new
        {
            sourcePath = relativeSource, offset = 1, limit = 1,
        })).GetProperty("paragraphs");
        var paragraph = secondPage.GetProperty("items")[0];
        Assert.Equal("Body: target", paragraph.GetProperty("textPreview").GetString());
        string scope = paragraph.GetProperty("id").GetString()!;
        var scoped = await host.Call("docxguard_plan", new
        {
            sourcePath = relativeSource,
            requests = new[] { new { expectedText = "target", replacementText = "updated", paragraphScopeId = scope } },
        });
        Assert.Equal(scope, Success(scoped).GetProperty("plan").GetProperty("operations")[0]
            .GetProperty("precondition").GetProperty("paragraphId").GetString());
        string plan = scoped.GetProperty("content")[0].GetProperty("text").GetString()!;
        var preview = Success(await host.Call("docxguard_dry_run", new { sourcePath = relativeSource, planJson = plan }));
        var report = preview.GetProperty("buildReport");
        Assert.Equal("passed", report.GetProperty("status").GetString());
        Assert.Equal(scope, Assert.Single(report.GetProperty("operations").EnumerateArray())
            .GetProperty("paragraphId").GetString());
        Assert.Equal(original, File.ReadAllBytes(sourcePath));

        File.WriteAllBytes(sourcePath, RebuildTestPackage.Rewrite(original,
            entries => entries[RebuildTestPackage.Media][0] ^= 1));
        AssertError(await host.Call("docxguard_dry_run", new { sourcePath = relativeSource, planJson = plan }), "MPLAN_005");
        Assert.Empty(Directory.GetDirectories(directory));
        Assert.Single(Directory.GetFiles(directory));
    }

    private byte[] WriteSource(bool unsupported = false)
    {
        using var fixture = RebuildTestPackage.Create(unsupported
            ? "<w:p><w:r><w:t>alpha</w:t><w:tab/></w:r></w:p><w:p><w:r><w:t>beta</w:t></w:r></w:p>"
            : "<w:p><w:r><w:t>alpha A</w:t></w:r></w:p><w:p><w:r><w:t>beta</w:t></w:r></w:p>");
        byte[] bytes = fixture.ToArray();
        File.WriteAllBytes(Path.Combine(directory, "input.docx"), bytes);
        return bytes;
    }

    private static async Task<string> Plan(McpProcess host)
    {
        var response = await host.Call("docxguard_plan", new
        {
            sourcePath = "input.docx",
            requests = new[]
            {
                new { expectedText = "alpha", replacementText = "新版" },
                new { expectedText = "beta", replacementText = "B" },
            },
        });
        Assert.Equal("docxguard.multi-bound-plan.v1", Success(response).GetProperty("profile").GetString());
        return response.GetProperty("content")[0].GetProperty("text").GetString()!;
    }

    private static Task<JsonElement> Apply(McpProcess host, string plan, string output) =>
        host.Call("docxguard_apply", new { sourcePath = "input.docx", planJson = plan, outputDirectory = output });

    private static JsonElement Success(JsonElement result)
    {
        Assert.False(result.TryGetProperty("isError", out var error) && error.GetBoolean(), result.GetRawText());
        return result.GetProperty("structuredContent");
    }

    private static void AssertError(JsonElement result, string code)
    {
        Assert.True(result.GetProperty("isError").GetBoolean(), result.GetRawText());
        Assert.Contains(result.GetProperty("structuredContent").GetProperty("findings").EnumerateArray(),
            finding => finding.GetProperty("code").GetString() == code);
    }

    public void Dispose()
    {
        Assert.StartsWith(Path.Combine(Path.GetTempPath(), "docxguard-mcp-"), Path.GetFullPath(directory));
        Directory.Delete(directory, recursive: true);
    }

    private sealed class McpProcess(Process process) : IAsyncDisposable
    {
        private readonly CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        private readonly Task<string> errors = process.StandardError.ReadToEndAsync();
        private int requestId;

        internal static async Task<McpProcess> Start(string workspace, bool apply = false)
        {
            string[] args = apply
                ? ["mcp", "--workspace", workspace, "--allow-apply"]
                : ["mcp", "--workspace", workspace];
            ProcessStartInfo info = CliPlanningTests.CreateProcessStartInfo(args);
            info.RedirectStandardInput = true;
            info.StandardInputEncoding = new System.Text.UTF8Encoding(false);
            McpProcess host = new(Process.Start(info)!);
            try
            {
                var init = await host.Request("initialize", new
                {
                    protocolVersion = "2025-11-25",
                    clientInfo = new { name = "DocxGuard.Tests", version = "1.0" },
                    capabilities = new { },
                });
                Assert.Equal("DocxGuard", init.GetProperty("serverInfo").GetProperty("name").GetString());
                Assert.True(init.GetProperty("capabilities").TryGetProperty("tools", out _));
                await host.Send(new { jsonrpc = "2.0", method = "notifications/initialized" });
                return host;
            }
            catch
            {
                await host.DisposeAsync();
                throw;
            }
        }

        internal Task<JsonElement> Call(string name, object arguments) =>
            Request("tools/call", new { name, arguments });

        internal async Task<JsonElement> Request(string method, object parameters)
        {
            int id = ++requestId;
            await Send(new { jsonrpc = "2.0", id, method, @params = parameters });
            string? line = await process.StandardOutput.ReadLineAsync(timeout.Token);
            Assert.True(line is not null, "MCP closed stdout before responding. " + (errors.IsCompletedSuccessfully ? errors.Result : ""));
            using var message = JsonDocument.Parse(line);
            Assert.Equal("2.0", message.RootElement.GetProperty("jsonrpc").GetString());
            Assert.Equal(id, message.RootElement.GetProperty("id").GetInt32());
            Assert.True(message.RootElement.TryGetProperty("result", out var result), line);
            return result.Clone();
        }

        private async Task Send(object message)
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message));
            await process.StandardInput.FlushAsync(timeout.Token);
        }

        public async ValueTask DisposeAsync()
        {
            process.StandardInput.Close();
            using CancellationTokenSource shutdown = new(TimeSpan.FromSeconds(3));
            try
            {
                await process.WaitForExitAsync(shutdown.Token);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            finally
            {
                timeout.Dispose();
                process.Dispose();
            }
        }
    }
}
