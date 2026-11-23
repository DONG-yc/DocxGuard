using System.ComponentModel;
using System.Text;
using System.Text.Json;
using DocxGuard.Core.Indexing;
using DocxGuard.Core.Packaging;
using DocxGuard.Core.Planning;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DocxGuard.Cli;

internal sealed class DocxMcpTools(McpWorkspace workspace, string version)
{
    private readonly SemaphoreSlim gate = new(1);

    [McpServerTool(Name = "docxguard_inspect", ReadOnly = true, OpenWorld = false)]
    [Description("Inspect a local DOCX and return paginated paragraph previews and document diagnostics.")]
    public Task<CallToolResult> Inspect(
        [Description("Workspace-relative path to a .docx file.")] string sourcePath,
        int offset = 0, int limit = 20, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            if (offset < 0 || limit is < 1 or > 100)
                return Error("MCP_001", "The offset must be nonnegative and the preview limit must be 1–100.");

            using FileStream source = OpenSource(sourcePath);
            InspectReport report = InspectReport.Create(version, InspectSource(source, cancellationToken), offset, limit);
            return Result(report, report.ExitCode != 0);
        }, cancellationToken);

    [McpServerTool(Name = "docxguard_plan", ReadOnly = true, OpenWorld = false)]
    [Description("Create a source-bound plan for 1–64 exact text replacements. Each request must resolve uniquely in the original document.")]
    public Task<CallToolResult> Plan(
        [Description("Workspace-relative path to a .docx file.")] string sourcePath,
        [Description("Text replacements with expectedText, replacementText, and optional paragraphScopeId. An empty replacement deletes text.")]
        TextReplacementRequest[] requests,
        CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            if (requests is null || requests.Length is < 1 or > BoundMultiPatchPlan.MaxOperations
                || requests.Any(request => request?.ExpectedText is null || request.ReplacementText is null))
                return Error("MCP_001", "Supply 1–64 requests with non-null expectedText and replacementText.");

            using FileStream source = OpenSource(sourcePath);
            DocxIndexInspectionResult inspection = InspectSource(source, cancellationToken);
            if (inspection.Status != PackageInspectionStatus.Passed)
                return Result(InspectReport.Create(version, inspection, 0, 20), isError: true);

            MultiPatchPlanResult result = BoundMultiPatchPlan.Create(inspection, requests, cancellationToken);
            if (result.Plan is not { } plan)
                return Result(new { status = "rejected", result.Findings }, isError: true);

            string json = plan.ToJson();
            using JsonDocument document = JsonDocument.Parse(json);
            return new CallToolResult
            {
                StructuredContent = document.RootElement.Clone(),
                Content = [new TextContentBlock { Text = json }],
                IsError = false,
            };
        }, cancellationToken);

    [McpServerTool(Name = "docxguard_dry_run", ReadOnly = true, OpenWorld = false)]
    [Description("Build and verify a source-bound plan in memory. Returns a report with no files published.")]
    public Task<CallToolResult> DryRun(
        [Description("Workspace-relative path to the original .docx file.")] string sourcePath,
        [Description("The unchanged plan JSON from docxguard_plan or the CLI.")] string planJson,
        CancellationToken cancellationToken = default) =>
        Run(() => Build(sourcePath, planJson, null, cancellationToken), cancellationToken);

    internal Task<CallToolResult> Apply(string sourcePath, string planJson,
        string outputDirectory, CancellationToken token) =>
        Run(() => Build(sourcePath, planJson, workspace.OutputDirectory(outputDirectory), token), token);

    [McpServerTool(Name = "docxguard_table_figure_list", ReadOnly = true, OpenWorld = false)]
    [Description("Find body tables and embedded pictures by headers and neighboring text. Returns source hash and identifiers for editing. Follow hasMore to inspect remaining candidates.")]
    public Task<CallToolResult> TableFigureList(string sourcePath, int offset = 0, int limit = 50,
        CancellationToken cancellationToken = default) => Run(() =>
        {
            using var source = OpenSource(sourcePath);
            var inspection = InspectSource(source, cancellationToken);
            if (inspection.Status != PackageInspectionStatus.Passed)
                return Result(InspectReport.Create(version, inspection, 0, 1), true);
            try { return Result(TableFigureCatalog.Read(inspection, offset, limit), false); }
            catch (InvalidDataException ex) { return Error("FIG_001", ex.Message); }
        }, cancellationToken);

    [McpServerTool(Name = "docxguard_table_figure_preview", ReadOnly = true, OpenWorld = false)]
    [Description("Preview appended table rows and a replacement PNG. Supply either requestPath or inline requestJson with sourceSha256, table, rows and imageRelationship from discovery. Paths are workspace-relative.")]
    public Task<CallToolResult> TableFigurePreview(string sourcePath, string figurePath, string? requestPath = null,
        string? requestJson = null,
        CancellationToken cancellationToken = default) =>
        TableFigure(sourcePath, requestPath, requestJson, figurePath, null, null, cancellationToken);

    internal Task<CallToolResult> TableFigure(string sourcePath, string? requestPath, string? requestJson, string figurePath,
        string? expected, string? destination, CancellationToken token) =>
        Run(() =>
        {
            if ((requestPath == null) == (requestJson == null))
                return Error("MCP_001", "Supply exactly one of requestPath and requestJson.");
            DirectoryInfo? temp = null;
            try
            {
                string path;
                if (requestJson != null)
                {
                    if (Encoding.UTF8.GetByteCount(requestJson) > 4 * 1024 * 1024)
                        return Error("MCP_001", "Inline request exceeds 4 MiB.");
                    temp = Directory.CreateTempSubdirectory("docxguard-request-");
                    path = Path.Combine(temp.FullName, "request.json");
                    File.WriteAllText(path, requestJson);
                }
                else path = workspace.InputFile(requestPath!);
                return Result(TableFigureCommand.Build(workspace.SourcePath(sourcePath), path,
                    workspace.InputFile(figurePath), expected, destination == null ? null : workspace.OutputDirectory(destination),
                    token), false);
            }
            catch (InvalidDataException ex) { return Error("FIG_001", ex.Message); }
            finally { temp?.Delete(recursive: true); }
        }, token);

    private CallToolResult Build(string sourcePath, string planJson, string? outputDirectory, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(planJson) || Encoding.UTF8.GetByteCount(planJson) > BoundPatchPlan.MaxSerializedBytes)
            return Error("MCP_001", "Supply a nonempty plan JSON string of at most 4 MiB.");

        using FileStream source = OpenSource(sourcePath);
        DocxIndexInspectionResult inspection = InspectSource(source, token);
        if (inspection.Status != PackageInspectionStatus.Passed)
            return Result(InspectReport.Create(version, inspection, 0, 20), isError: true);

        PlanningCommandReport report = PlanningCommand.BuildFromPlan(
            source, inspection, Encoding.UTF8.GetBytes(planJson), outputDirectory, token);
        return Result(report, report.ExitCode != 0);
    }

    private FileStream OpenSource(string path) => new(workspace.SourcePath(path),
        FileMode.Open, FileAccess.Read, FileShare.Read);

    private static DocxIndexInspectionResult InspectSource(Stream source, CancellationToken token) =>
        new DocxIndexInspector().Inspect(source, cancellationToken: token);

    private async Task<CallToolResult> Run(Func<CallToolResult> invoke, CancellationToken token)
    {
        if (!gate.Wait(0, token))
            return Error("MCP_003", "Another DocxGuard operation is running. Retry after it completes.");

        try
        {
            return await Task.Run(invoke, token);
        }
        catch (ArgumentException exception)
        {
            return Error("MCP_001", exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Error("MCP_002", "The file or directory could not be accessed within the workspace.");
        }
        finally
        {
            gate.Release();
        }
    }

    private static CallToolResult Error(string code, string message) =>
        Result(new { status = "rejected", findings = new[] { new { code, message } } }, isError: true);

    private static CallToolResult Result<T>(T value, bool isError)
    {
        JsonElement json = JsonSerializer.SerializeToElement(value, InspectReport.JsonOptions);
        return new CallToolResult
        {
            StructuredContent = json,
            Content = [new TextContentBlock { Text = json.GetRawText() }],
            IsError = isError,
        };
    }
}

internal sealed class DocxMcpApplyTool(DocxMcpTools tools)
{
    [McpServerTool(Name = "docxguard_table_figure_apply", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("After approval, publish the previewed table and figure update to a new workspace directory. Requires the output SHA-256 returned by preview.")]
    public Task<CallToolResult> TableFigureApply(string sourcePath, string figurePath,
        string previewSha256, string outputDirectory, string? requestPath = null, string? requestJson = null,
        CancellationToken cancellationToken = default) =>
        tools.TableFigure(sourcePath, requestPath, requestJson, figurePath, previewSha256, outputDirectory, cancellationToken);

    [McpServerTool(Name = "docxguard_apply", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("After user approval, verify a source-bound plan and publish output.docx and build-report.json together in a new workspace directory.")]
    public Task<CallToolResult> Apply(
        [Description("Workspace-relative path to the original .docx file.")] string sourcePath,
        [Description("The unchanged plan JSON from docxguard_plan or the CLI.")] string planJson,
        [Description("New workspace-relative directory. Its parent must exist. Existing results are never overwritten.")] string outputDirectory,
        CancellationToken cancellationToken = default) =>
        tools.Apply(sourcePath, planJson, outputDirectory, cancellationToken);
}
