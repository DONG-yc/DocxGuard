using System.Collections.ObjectModel;
using DocxGuard.Core.Indexing;
using DocxGuard.Core.Packaging;

namespace DocxGuard.Core.Verification;

public static class StructuralDiffFindingCodes
{
    public const string SnapshotLimitExceeded = "DIFF_001";
    public const string InspectionFailed = "DIFF_002";
    public const string SourceMismatch = "DIFF_003";
    public const string EntryInventoryChanged = "DIFF_004";
    public const string ProtectedEntryChanged = "DIFF_005";
    public const string UnauthorizedXmlChange = "DIFF_006";
    public const string ExpectedTextMismatch = "DIFF_007";
    public const string EntryReadFailed = "DIFF_008";
    public const string PatchArtifactMismatch = "DIFF_009";
}

public enum StructuralDiffStatus
{
    Passed,
    Failed,
}

public sealed record StructuralDiffFinding(
    string Code,
    FindingSeverity Severity,
    string Message,
    string? EntryName = null,
    string? XmlPath = null);

public sealed record VerifiedEntryChange(
    string EntryName,
    long SourceLength,
    long OutputLength,
    string SourceSha256,
    string OutputSha256,
    bool IsMainDocument);

/// <summary>
/// Complete comparison evidence, or a diagnostic with no partial entry list.
/// Inspecting an output successfully does not by itself mean the diff passed.
/// </summary>
public sealed class StructuralDiffResult
{
    internal StructuralDiffResult(
        DocxIndexInspectionResult? sourceInspection,
        DocxIndexInspectionResult? outputInspection,
        IEnumerable<VerifiedEntryChange> entries,
        IEnumerable<StructuralDiffFinding> findings)
    {
        SourceInspection = sourceInspection;
        OutputInspection = outputInspection;
        Entries = new ReadOnlyCollection<VerifiedEntryChange>(entries.ToArray());
        Findings = new ReadOnlyCollection<StructuralDiffFinding>(findings.ToArray());
        if (Status == StructuralDiffStatus.Passed
            && (sourceInspection?.Status != PackageInspectionStatus.Passed
                || outputInspection?.Status != PackageInspectionStatus.Passed
                || Entries.Count == 0))
        {
            throw new InvalidOperationException(
                "A passed diff requires inspected inputs and complete entry evidence.");
        }

        if (Status == StructuralDiffStatus.Failed && Entries.Count != 0)
        {
            throw new InvalidOperationException(
                "A failed diff cannot publish partial entry evidence.");
        }
    }

    public DocxIndexInspectionResult? SourceInspection { get; }

    public DocxIndexInspectionResult? OutputInspection { get; }

    public IReadOnlyList<VerifiedEntryChange> Entries { get; }

    public IReadOnlyList<StructuralDiffFinding> Findings { get; }

    public StructuralDiffStatus Status => Findings.Any(
        finding => finding.Severity == FindingSeverity.Error)
        ? StructuralDiffStatus.Failed
        : StructuralDiffStatus.Passed;
}
