using System.Text.Json;
using DocxGuard.Core;
using DocxGuard.Core.Building;
using DocxGuard.Core.Indexing;
using DocxGuard.Core.Packaging;
using DocxGuard.Core.Planning;

namespace DocxGuard.Cli;

internal sealed record PlanningDiagnostic(string Code, string Message, string? ParagraphId = null);
internal sealed record PlanningCandidate(string MatchId, string ParagraphId, int StartOffset, int Length);
internal sealed record PlanningCommandReport(string Command, string Status, int ExitCode,
    string? SourcePackageSha256, string? PlanId, IReadOnlyList<PlanningDiagnostic> Findings,
    int CandidateCount = 0, IReadOnlyList<PlanningCandidate>? Candidates = null,
    InspectReport? Inspection = null, BuildReport? BuildReport = null,
    DocxPublicationResult? Publication = null)
{
    public string SchemaVersion => "1.0";
    public string Profile => "docxguard.planning-command.v1";
}

internal static class PlanningCommand
{
    internal static int Run(IReadOnlyList<string> arguments, string version,
        TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        string command = arguments[0];
        bool usesPlan = command != "plan";
        bool apply = command == "apply";
        if (arguments.Count == 2 && arguments[1] is "--help" or "-h")
        {
            output.WriteLine(apply
                ? "Usage: docxguard apply [--json] --plan <plan.json> --output-dir <new-directory> [--] <file.docx>"
                : usesPlan
                ? "Usage: docxguard dry-run [--json] --plan <plan.json> [--] <file.docx>"
                : "Usage: docxguard plan [--json] --find <text> --replace <text> [--paragraph <id>] [--] <file.docx>");
            output.WriteLine(usesPlan
                ? "The --plan file may come from either plan or multi-plan."
                : "Plan success emits source-bound JSON. Empty replacement deletes text; use actual Tab/LF, not literal escape sequences.");
            output.WriteLine(apply
                ? "Apply publishes output.docx and build-report.json together in a new directory. The parent must exist; no overwrite."
                : "Dry-run builds and verifies in memory; no files are written.");
            output.WriteLine("Exit codes: 0 success; 2 usage/format; 3 input I/O; 4 document/plan/build rejected; 5 no match/ambiguous; 6 publication failed; 130 cancelled.");
            return 0;
        }

        Dictionary<string, string> options = new(StringComparer.Ordinal);
        bool json = false;
        bool positionalOnly = false;
        string? sourcePath = null;
        string? usage = null;
        for (int i = 1; i < arguments.Count; i++)
        {
            string value = arguments[i];
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
                bool supported = usesPlan ? option == "--plan" || (apply && option == "--output-dir")
                    : option is "--find" or "--replace" or "--paragraph";
                if (!supported) { usage ??= "Unknown command option."; continue; }
                string optionValue;
                if (equals >= 0) optionValue = value[(equals + 1)..];
                else
                {
                    if (++i >= arguments.Count) { usage ??= "Missing option value."; break; }
                    optionValue = arguments[i];
                }

                if (!options.TryAdd(option, optionValue)) usage ??= "Duplicate command option.";
            }
            else if (sourcePath is not null) usage ??= "Exactly one input DOCX path is required.";
            else sourcePath = value;
        }

        if (string.IsNullOrWhiteSpace(sourcePath)
            || (usesPlan ? !options.ContainsKey("--plan")
                : !options.ContainsKey("--find") || !options.ContainsKey("--replace"))
            || (options.TryGetValue("--plan", out string? planPath) && string.IsNullOrWhiteSpace(planPath))
            || (options.TryGetValue("--paragraph", out string? scope) && string.IsNullOrWhiteSpace(scope))
            || (apply && (!options.TryGetValue("--output-dir", out string? target) || string.IsNullOrWhiteSpace(target))))
            usage ??= "Required path/options missing. Run the command with --help.";

        PlanningCommandReport result;
        BoundPatchPlan? publishedPlan = null;
        if (usage is not null)
            result = Failure(command, 2, "DOCXGUARD_CLI_001", usage);
        else
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!DocumentFormatPolicy.IsSupportedInput(sourcePath))
                    result = Failure(command, 2, "DOCXGUARD_CLI_002", "Only .docx input files are supported.");
                else
                    result = Execute(command, sourcePath!, options, version, out publishedPlan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                result = Failure(command, 130, "DOCXGUARD_CLI_004", "Operation was cancelled.");
            }
            catch (PlanSizeException)
            {
                result = Failure(command, 4, "BPLAN_002", "Serialized plan exceeds the 4 MiB limit.");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException or System.Security.SecurityException)
            {
                result = Failure(command, 3, "DOCXGUARD_CLI_003", exception.Message);
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
            writer.WriteLine($"{command}: {result.Status}");
            foreach (var finding in result.Findings)
                writer.WriteLine($"{finding.Code}: {JsonSerializer.Serialize(finding.Message, InspectReport.JsonOptions)}");
            if (result.Inspection is { } inspection) inspection.Write(output, error, false);
            if (result.BuildReport is { } report)
            {
                writer.WriteLine($"Plan: {report.PlanId}; source: {report.SourcePackageSha256}");
                writer.WriteLine($"Verified output SHA-256: {report.OutputPackageSha256 ?? "unavailable"}");
                foreach (var check in report.Checks) writer.WriteLine($"  {check.Stage}: {check.Status}");
                if (command == "dry-run") writer.WriteLine("Dry-run only: no output DOCX was saved.");
            }

            if (result.Publication is { } publication)
            {
                writer.WriteLine($"Publication: {publication.Status}");
                if (publication.Status == DocxPublicationStatus.Published)
                {
                    writer.WriteLine($"DOCX: {JsonSerializer.Serialize(publication.DocxPath, InspectReport.JsonOptions)}");
                    writer.WriteLine($"Report: {JsonSerializer.Serialize(publication.ReportPath, InspectReport.JsonOptions)}");
                }

                foreach (string path in publication.ResidualPaths)
                    writer.WriteLine($"Cleanup required: {JsonSerializer.Serialize(path, InspectReport.JsonOptions)}");
            }

            if (result.CandidateCount > 0)
            {
                writer.WriteLine($"Candidates: {result.CandidateCount}; showing up to 20. Use --paragraph to scope the request.");
                foreach (var candidate in result.Candidates ?? [])
                    writer.WriteLine($"  {JsonSerializer.Serialize(candidate, InspectReport.JsonOptions)}");
            }
        }

        return result.ExitCode;
    }

    private static PlanningCommandReport Execute(string command, string sourcePath,
        Dictionary<string, string> options, string version, out BoundPatchPlan? publishedPlan, CancellationToken token)
    {
        publishedPlan = null;
        using FileStream source = new(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        DocxIndexInspectionResult inspection = new DocxIndexInspector().Inspect(source, cancellationToken: token);
        if (inspection.Status != PackageInspectionStatus.Passed)
            return new(command, "rejected", 4, null, null, [],
                Inspection: InspectReport.Create(version, inspection, 0, 20));

        if (command != "plan")
            return BuildFromPlan(source, inspection, ReadPlan(options["--plan"], token),
                options.GetValueOrDefault("--output-dir"), token);

        BoundPlanResult bound = BoundPatchPlan.Create(inspection, new(options["--find"], options["--replace"],
            options.GetValueOrDefault("--paragraph")), token);
        if (!bound.Passed)
            return RejectSinglePlan(command, inspection, bound);

        token.ThrowIfCancellationRequested();
        publishedPlan = bound.Plan!;
        return new(command, "ready", 0, inspection.SourcePackageSha256, publishedPlan.Plan.Id, []);
    }

    internal static PlanningCommandReport BuildFromPlan(Stream source, DocxIndexInspectionResult inspection,
        ReadOnlyMemory<byte> serializedPlan, string? outputDirectory, CancellationToken token)
    {
        string command = outputDirectory is null ? "dry-run" : "apply";
        DocxBuildPipeline pipeline = new();
        if (IsMultiPlan(serializedPlan))
        {
            MultiPatchPlanResult batch = BoundMultiPatchPlan.Parse(serializedPlan, inspection, token);
            if (batch.Plan is not { } plan)
                return new(command, "rejected", 4, inspection.SourcePackageSha256, null,
                    batch.Findings.Select(f => new PlanningDiagnostic(f.Code, f.Message, f.ParagraphId)).ToArray());
            return FinishBuild(command, outputDirectory,
                pipeline.BuildBoundMulti(source, plan, cancellationToken: token), token);
        }

        BoundPlanResult bound = BoundPatchPlan.Parse(serializedPlan, inspection, token);
        if (bound.Plan is not { } single)
            return RejectSinglePlan(command, inspection, bound);
        return FinishBuild(command, outputDirectory,
            pipeline.BuildBound(source, single, cancellationToken: token), token);
    }

    private static PlanningCommandReport RejectSinglePlan(string command,
        DocxIndexInspectionResult inspection, BoundPlanResult bound)
    {
        var findings = bound.Findings.Select(f => new PlanningDiagnostic(f.Code, f.Message)).ToList();
        if (bound.Planning is { } planning)
        {
            findings.AddRange(planning.Findings.Select(f => new PlanningDiagnostic(f.Code, f.Message, f.ParagraphId)));
            findings.AddRange(planning.Location.Findings.Select(f => new PlanningDiagnostic(f.Code, f.Message)));
        }

        bool unresolved = command == "plan"
            && bound.Planning?.Status is TextPatchPlanningStatus.NoMatch or TextPatchPlanningStatus.Ambiguous;
        return new(command, unresolved ? "unresolved" : "rejected", unresolved ? 5 : 4,
            inspection.SourcePackageSha256, null, findings,
            bound.Planning?.Location.Matches.Count ?? 0,
            bound.Planning?.Location.Matches.Take(20).Select(m => new PlanningCandidate(m.Id, m.ParagraphId, m.StartOffset, m.Length)).ToArray());
    }

    private static PlanningCommandReport FinishBuild(string command, string? outputDirectory,
        DocxBuildResult build, CancellationToken token)
    {
        PlanningCommandReport result = new(command, build.Status == DocxBuildStatus.Passed ? "passed" : "rejected",
            build.Status == DocxBuildStatus.Passed ? 0 : 4, build.Report.SourcePackageSha256, build.Report.PlanId,
            build.Report.Findings.Select(f => new PlanningDiagnostic(f.Code, f.Message, f.ParagraphId)).ToArray(),
            BuildReport: build.Report);
        if (outputDirectory is not null && build.Status == DocxBuildStatus.Passed)
        {
            DocxPublicationResult publication = new DocxBuildPublisher().Publish(build, outputDirectory, token);
            result = result with
            {
                Status = publication.Status == DocxPublicationStatus.Published ? "published"
                    : publication.Status == DocxPublicationStatus.Cancelled ? "cancelled" : "failed",
                ExitCode = publication.Status == DocxPublicationStatus.Published ? 0
                    : publication.Status == DocxPublicationStatus.Cancelled ? 130 : 6,
                Publication = publication,
                Findings = result.Findings.Concat(publication.Findings.Select(f => new PlanningDiagnostic(f.Code, f.Message))).ToArray(),
            };
        }

        return result;
    }

    private static bool IsMultiPlan(ReadOnlyMemory<byte> json)
    {
        if (json.Span.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) json = json[3..];
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 48 });
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("profile", out JsonElement profile)
                && profile.ValueKind == JsonValueKind.String
                && profile.GetString() == BoundMultiPatchPlan.ProfileId;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            // The existing plan parser supplies the detailed rejection.
            return false;
        }
    }

    private static byte[] ReadPlan(string path, CancellationToken token)
    {
        using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > BoundPatchPlan.MaxSerializedBytes) throw new PlanSizeException();
        using MemoryStream buffer = new();
        byte[] chunk = new byte[8192];
        while (true)
        {
            token.ThrowIfCancellationRequested();
            int count = file.Read(chunk);
            if (count == 0) break;
            if (buffer.Length + count > BoundPatchPlan.MaxSerializedBytes) throw new PlanSizeException();
            buffer.Write(chunk, 0, count);
        }

        return buffer.ToArray();
    }

    private sealed class PlanSizeException : Exception;
    private static PlanningCommandReport Failure(string command, int exitCode, string code, string message) =>
        new(command, "failed", exitCode, null, null, [new(code, message)]);
}
