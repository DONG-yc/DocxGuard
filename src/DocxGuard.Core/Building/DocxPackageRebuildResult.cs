using System.Collections.ObjectModel;
using System.Security.Cryptography;
using DocxGuard.Core.Indexing;
using DocxGuard.Core.Packaging;
using DocxGuard.Core.Verification;

namespace DocxGuard.Core.Building;

public static class DocxPackageRebuildFindingCodes
{
    public const string SourceSnapshotLimitExceeded = "REBUILD_001";
    public const string SourceInspectionNotPassed = "REBUILD_002";
    public const string PatchDoesNotMatchSource = "REBUILD_003";
    public const string SourceEntryReadFailed = "REBUILD_004";
    public const string OutputResourceLimitExceeded = "REBUILD_005";
    public const string PackageRebuildFailed = "REBUILD_006";
    public const string OutputInspectionFailed = "REBUILD_007";
    public const string OutputPostconditionFailed = "REBUILD_008";
    public const string StructuralDiffFailed = "REBUILD_009";
}

public sealed record DocxPackageRebuildFinding(
    string Code,
    FindingSeverity Severity,
    string Message,
    string? EntryName = null);

public enum DocxPackageRebuildStatus
{
    Passed,
    Failed,
}

public sealed class InMemoryDocxPackage
{
    private readonly byte[] packageBytes;

    internal InMemoryDocxPackage(
        string planId,
        string mainDocumentPartName,
        ReadOnlySpan<byte> sourcePackage,
        ReadOnlySpan<byte> outputPackage,
        string originalMainDocumentXmlSha256,
        string patchedMainDocumentXmlSha256)
    {
        PlanId = planId;
        MainDocumentPartName = mainDocumentPartName;
        SourcePackageSha256 = ComputeSha256(sourcePackage);
        OutputPackageSha256 = ComputeSha256(outputPackage);
        OriginalMainDocumentXmlSha256 = originalMainDocumentXmlSha256;
        PatchedMainDocumentXmlSha256 = patchedMainDocumentXmlSha256;
        packageBytes = outputPackage.ToArray();
    }

    public string PlanId { get; }

    public string MainDocumentPartName { get; }

    public string SourcePackageSha256 { get; }

    public string OutputPackageSha256 { get; }

    public string OriginalMainDocumentXmlSha256 { get; }

    public string PatchedMainDocumentXmlSha256 { get; }

    public int PackageLength => packageBytes.Length;

    public ReadOnlyMemory<byte> Bytes => packageBytes.ToArray();

    private static string ComputeSha256(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
}

public sealed class DocxPackageRebuildResult
{
    internal DocxPackageRebuildResult(
        string planId,
        DocxIndexInspectionResult? sourceInspection,
        DocxIndexInspectionResult? outputInspection,
        InMemoryDocxPackage? artifact,
        IEnumerable<DocxPackageRebuildFinding> findings,
        StructuralDiffResult? structuralDiff = null)
    {
        PlanId = planId;
        SourceInspection = sourceInspection;
        OutputInspection = outputInspection;
        Artifact = artifact;
        StructuralDiff = structuralDiff;
        Findings = new ReadOnlyCollection<DocxPackageRebuildFinding>(
            findings.ToArray());
        ValidateInvariants();
    }

    public string PlanId { get; }

    public DocxIndexInspectionResult? SourceInspection { get; }

    public DocxIndexInspectionResult? OutputInspection { get; }

    public InMemoryDocxPackage? Artifact { get; }

    public StructuralDiffResult? StructuralDiff { get; }

    public IReadOnlyList<DocxPackageRebuildFinding> Findings { get; }

    public DocxPackageRebuildStatus Status => Findings.Any(
        finding => finding.Severity == FindingSeverity.Error)
        ? DocxPackageRebuildStatus.Failed
        : DocxPackageRebuildStatus.Passed;

    private void ValidateInvariants()
    {
        bool passed = Status == DocxPackageRebuildStatus.Passed;
        if (passed
            && (Artifact is null
                || StructuralDiff?.Status != StructuralDiffStatus.Passed
                || SourceInspection?.Status != PackageInspectionStatus.Passed
                || OutputInspection?.Status != PackageInspectionStatus.Passed))
        {
            throw new InvalidOperationException(
                "A passed rebuild must publish an artifact and passed source and output inspections.");
        }

        if (!passed && Artifact is not null)
        {
            throw new InvalidOperationException(
                "A failed rebuild cannot publish a partial DOCX artifact.");
        }
    }
}
