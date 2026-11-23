using System.Text.Json;
using System.Text.Json.Serialization;
using DocxGuard.Core.Packaging;
using DocxGuard.Core.Planning;
using DocxGuard.Core.Verification;

namespace DocxGuard.Core.Building;

public enum BuildCheckStatus { NotRun, Passed, Failed }
public enum DocxBuildStatus { Passed, Failed }

public sealed record BuildCheck(string Stage, BuildCheckStatus Status);

public sealed record BuildFinding(
    string Stage, string Code, FindingSeverity Severity, string Message,
    string? EntryName = null, string? PartName = null, string? XmlPath = null,
    string? OperationId = null, string? ParagraphId = null,
    int? LineNumber = null, int? LinePosition = null,
    string? RelationshipId = null);

/// <summary>Text summaries use SHA-256 of UTF-8; ranges use UTF-16 code units.</summary>
public sealed class BuildOperationEvidence
{
    internal BuildOperationEvidence(TextPatchOperation operation, string? expectedParagraphSha256,
        string? actualParagraphSha256)
    {
        OperationId = operation.Id;
        SourceMatchId = operation.SourceMatchId;
        ParagraphId = operation.Precondition.ParagraphId;
        StartOffset = operation.Precondition.StartOffset;
        RemovedLength = operation.Precondition.Length;
        InsertedLength = operation.ReplacementText.Length;
        ExpectedTextSha256 = DocxBuildPipeline.HashText(operation.Precondition.ExpectedText);
        ReplacementTextSha256 = DocxBuildPipeline.HashText(operation.ReplacementText);
        SourceParagraphSha256 = operation.Precondition.ExpectedParagraphTextSha256;
        ExpectedOutputParagraphSha256 = expectedParagraphSha256;
        ActualOutputParagraphSha256 = actualParagraphSha256;
        RunSlices = Array.AsReadOnly(operation.Precondition.ExpectedRunSlices.ToArray());
    }

    public string OperationId { get; }
    public string SourceMatchId { get; }
    public string ParagraphId { get; }
    public int StartOffset { get; }
    public int RemovedLength { get; }
    public int InsertedLength { get; }
    public string ExpectedTextSha256 { get; }
    public string ReplacementTextSha256 { get; }
    public string SourceParagraphSha256 { get; }
    public string? ExpectedOutputParagraphSha256 { get; }
    public string? ActualOutputParagraphSha256 { get; }
    public IReadOnlyList<RunTextSlice> RunSlices { get; }
}

/// <summary>Versioned evidence, not an executable plan or proof of user approval.</summary>
public sealed class BuildReport
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    internal BuildReport(string planId, string? sourceSha256, string? mainPart,
        InMemoryDocxPackage? artifact, DocxBuildOptions limits, bool batch,
        IEnumerable<BuildCheck> checks, IEnumerable<BuildFinding> findings,
        IEnumerable<BuildOperationEvidence> operations, IEnumerable<VerifiedEntryChange> entries)
    {
        PlanId = planId;
        SourcePackageSha256 = sourceSha256;
        MainDocumentPartName = mainPart;
        OutputPackageSha256 = artifact?.OutputPackageSha256;
        OutputPackageLength = artifact?.PackageLength;
        OriginalMainDocumentXmlSha256 = artifact?.OriginalMainDocumentXmlSha256;
        PatchedMainDocumentXmlSha256 = artifact?.PatchedMainDocumentXmlSha256;
        Limits = limits;
        Profile = batch ? "docxguard.multi-text.v1" : "docxguard.single-text.v1";
        Checks = Array.AsReadOnly(checks.ToArray());
        Findings = Array.AsReadOnly(findings.ToArray());
        Operations = Array.AsReadOnly(operations.ToArray());
        Entries = Array.AsReadOnly(entries.ToArray());
        Status = artifact is null ? DocxBuildStatus.Failed : DocxBuildStatus.Passed;
        if (Status == DocxBuildStatus.Passed
            && (Checks.Any(check => check.Status != BuildCheckStatus.Passed)
                || Findings.Any(finding => finding.Severity == FindingSeverity.Error)
                || sourceSha256 != artifact!.SourcePackageSha256
                || planId != artifact.PlanId || mainPart != artifact.MainDocumentPartName
                || Entries.Count == 0 || Operations.Count == 0
                || Operations.Any(operation => operation.ExpectedOutputParagraphSha256 is null
                    || operation.ActualOutputParagraphSha256
                    != operation.ExpectedOutputParagraphSha256)))
        {
            throw new InvalidOperationException("A successful report requires all gates and matching artifact identity.");
        }

        if (Status == DocxBuildStatus.Failed
            && (!Findings.Any(finding => finding.Severity == FindingSeverity.Error) || Entries.Count != 0))
        {
            throw new InvalidOperationException("A failed report requires diagnostics and no partial output evidence.");
        }
    }

    public string SchemaVersion => "1.0";
    public string Profile { get; }
    public string EngineVersion => typeof(BuildReport).Assembly.GetName().Version!.ToString();
    public string PlanId { get; }
    public DocxBuildStatus Status { get; }
    public string? SourcePackageSha256 { get; }
    public string? MainDocumentPartName { get; }
    public string? OutputPackageSha256 { get; }
    public int? OutputPackageLength { get; }
    public string? OriginalMainDocumentXmlSha256 { get; }
    public string? PatchedMainDocumentXmlSha256 { get; }
    public DocxBuildOptions Limits { get; }
    public IReadOnlyList<BuildCheck> Checks { get; }
    public IReadOnlyList<BuildFinding> Findings { get; }
    public IReadOnlyList<BuildOperationEvidence> Operations { get; }
    public IReadOnlyList<VerifiedEntryChange> Entries { get; }
    public int ChangedEntryCount => Entries.Count(entry => entry.SourceSha256 != entry.OutputSha256);

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
}

public sealed class DocxBuildResult
{
    internal DocxBuildResult(BuildReport report, InMemoryDocxPackage? artifact)
    {
        Report = report;
        Artifact = artifact;
    }

    public BuildReport Report { get; }
    public InMemoryDocxPackage? Artifact { get; }
    public DocxBuildStatus Status => Report.Status;
}
