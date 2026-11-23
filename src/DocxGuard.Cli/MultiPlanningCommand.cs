using System.Text.Json;
using DocxGuard.Core;
using DocxGuard.Core.Indexing;
using DocxGuard.Core.Packaging;
using DocxGuard.Core.Planning;

namespace DocxGuard.Cli;

internal static class MultiPlanningCommand
{
    internal static int Run(IReadOnlyList<string> arguments, string version,
        TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (arguments.Count == 2 && arguments[1] is "--help" or "-h")
        {
            output.WriteLine("Usage: docxguard multi-plan [--json] --requests <requests.json> [--] <file.docx>");
            output.WriteLine("Requests are a JSON array of expectedText, replacementText and optional paragraphScopeId.");
            output.WriteLine("The command only emits a source-bound plan; it never modifies the input DOCX.");
            output.WriteLine("Exit codes: 0 success; 2 usage/format; 3 input I/O; 4 rejected batch; 5 no match/ambiguous; 130 cancelled.");
            return 0;
        }

        bool json = false;
        string? sourcePath = null;
        string? requestsPath = null;
        string? usage = null;
        bool positionalOnly = false;
        for (int index = 1; index < arguments.Count; index++)
        {
            string value = arguments[index];
            if (!positionalOnly && value == "--") { positionalOnly = true; continue; }
            if (!positionalOnly && value.StartsWith("-", StringComparison.Ordinal))
            {
                if (value == "--json")
                {
                    if (json) usage ??= "Duplicate --json option.";
                    json = true;
                    continue;
                }

                int equals = value.IndexOf('=');
                string option = equals < 0 ? value : value[..equals];
                if (option != "--requests") { usage ??= "Unknown command option."; continue; }
                string optionValue;
                if (equals >= 0) optionValue = value[(equals + 1)..];
                else if (++index >= arguments.Count) { usage ??= "Missing --requests value."; break; }
                else optionValue = arguments[index];
                if (!string.IsNullOrWhiteSpace(requestsPath)) usage ??= "Duplicate --requests option.";
                requestsPath = optionValue;
            }
            else if (sourcePath is not null) usage ??= "Exactly one input DOCX path is required.";
            else sourcePath = value;
        }

        if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(requestsPath))
            usage ??= "Required path/options missing. Run the command with --help.";

        PlanningCommandReport result;
        BoundMultiPatchPlan? publishedPlan = null;
        if (usage is not null)
        {
            result = new("multi-plan", "failed", 2, null, null,
                [new("DOCXGUARD_CLI_001", usage)]);
        }
        else
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!DocumentFormatPolicy.IsSupportedInput(sourcePath!))
                    result = new("multi-plan", "failed", 2, null, null,
                        [new("DOCXGUARD_CLI_002", "Only .docx input files are supported.")]);
                else
                    result = Execute(sourcePath!, requestsPath!, version, out publishedPlan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                result = new("multi-plan", "failed", 130, null, null,
                    [new("DOCXGUARD_CLI_004", "Operation was cancelled.")]);
            }
            catch (JsonException)
            {
                result = new("multi-plan", "rejected", 4, null, null,
                    [new(MultiPlanFindingCodes.InvalidJson, "Invalid replacement request JSON.")]);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException or System.Security.SecurityException)
            {
                result = new("multi-plan", "failed", 3, null, null,
                    [new("DOCXGUARD_CLI_003", exception.Message)]);
            }
        }

        if (publishedPlan is not null)
        {
            output.WriteLine(publishedPlan.ToJson());
            return 0;
        }

        if (json) output.WriteLine(JsonSerializer.Serialize(result, InspectReport.JsonOptions));
        else
        {
            TextWriter writer = result.ExitCode == 0 ? output : error;
            writer.WriteLine($"multi-plan: {result.Status}");
            foreach (PlanningDiagnostic finding in result.Findings)
                writer.WriteLine($"{finding.Code}: {JsonSerializer.Serialize(finding.Message, InspectReport.JsonOptions)}");
            if (result.Inspection is { } inspection) inspection.Write(output, error, false);
            if (result.CandidateCount > 0)
                writer.WriteLine($"Candidates: {result.CandidateCount}; use a paragraph scope in the request file to disambiguate.");
        }

        return result.ExitCode;
    }

    private static PlanningCommandReport Execute(string sourcePath, string requestsPath,
        string version, out BoundMultiPatchPlan? publishedPlan, CancellationToken token)
    {
        publishedPlan = null;
        using FileStream source = new(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        DocxIndexInspectionResult inspection = new DocxIndexInspector().Inspect(source, cancellationToken: token);
        if (inspection.Status != PackageInspectionStatus.Passed)
            return new("multi-plan", "rejected", 4, null, null, [],
                Inspection: InspectReport.Create(version, inspection, 0, 20));

        IReadOnlyList<TextReplacementRequest> requests = ReadRequests(requestsPath, token);
        MultiPatchPlanResult multi = BoundMultiPatchPlan.Create(inspection, requests, token);
        if (!multi.Passed)
        {
            List<PlanningDiagnostic> findings = multi.Findings
                .Select(f => new PlanningDiagnostic(
                    f.Code,
                    f.RequestIndex is { } requestIndex
                        ? $"request[{requestIndex}]: {f.Message}"
                        : f.Message,
                    f.ParagraphId))
                .ToList();
            bool unresolved = multi.Planning.Any(p =>
                p.Status is TextPatchPlanningStatus.NoMatch or TextPatchPlanningStatus.Ambiguous);
            int candidateCount = multi.Planning.Sum(p => p.Location.Matches.Count);
            PlanningCandidate[] candidates = multi.Planning
                .SelectMany(p => p.Location.Matches)
                .Take(20)
                .Select(m => new PlanningCandidate(m.Id, m.ParagraphId, m.StartOffset, m.Length))
                .ToArray();
            return new("multi-plan", unresolved ? "unresolved" : "rejected", unresolved ? 5 : 4,
                inspection.SourcePackageSha256, null, findings, candidateCount, candidates);
        }

        BoundMultiPatchPlan plan = multi.Plan!;
        publishedPlan = plan;
        return new("multi-plan", "ready", 0, inspection.SourcePackageSha256,
            plan.Plan.Id, []);
    }

    private static IReadOnlyList<TextReplacementRequest> ReadRequests(string path, CancellationToken token)
    {
        using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > BoundMultiPatchPlan.MaxSerializedBytes)
            throw new ArgumentException("The requests file exceeds the 4 MiB limit.", nameof(path));
        using MemoryStream buffer = new();
        file.CopyTo(buffer);
        token.ThrowIfCancellationRequested();
        ReadOnlyMemory<byte> bytes = buffer.ToArray();
        if (bytes.Span.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) bytes = bytes[3..];
        using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new JsonException("The requests file must contain a JSON array.");
        List<TextReplacementRequest>? requests = document.RootElement
            .Deserialize<List<TextReplacementRequest>>(new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
        if (requests is null || requests.Any(request =>
            request?.ExpectedText is null || request.ReplacementText is null))
            throw new JsonException("Each request must contain non-null expected and replacement text.");
        return requests;
    }
}
