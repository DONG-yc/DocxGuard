using System.Collections.ObjectModel;

namespace DocxGuard.Core.Packaging;

public enum PackageInspectionStatus
{
    Passed,
    Failed,
}

public sealed record PackageEntryInfo(
    string Name,
    string? CanonicalName,
    long CompressedLength,
    long UncompressedLength,
    double CompressionRatio,
    uint DeclaredCrc32,
    bool IsDirectory,
    bool IsEncrypted,
    bool IntegrityVerified);

public sealed class PackageInspectionResult
{
    internal PackageInspectionResult(
        long archiveLength,
        long declaredTotalCompressedBytes,
        long declaredTotalUncompressedBytes,
        IEnumerable<PackageEntryInfo> entries,
        IEnumerable<PackageFinding> findings)
    {
        ArchiveLength = archiveLength;
        DeclaredTotalCompressedBytes = declaredTotalCompressedBytes;
        DeclaredTotalUncompressedBytes = declaredTotalUncompressedBytes;
        Entries = new ReadOnlyCollection<PackageEntryInfo>(entries.ToArray());
        Findings = new ReadOnlyCollection<PackageFinding>(findings.ToArray());
    }

    public long ArchiveLength { get; }

    public long DeclaredTotalCompressedBytes { get; }

    public long DeclaredTotalUncompressedBytes { get; }

    public IReadOnlyList<PackageEntryInfo> Entries { get; }

    public IReadOnlyList<PackageFinding> Findings { get; }

    public PackageInspectionStatus Status => Findings.Any(
        finding => finding.Severity == FindingSeverity.Error)
        ? PackageInspectionStatus.Failed
        : PackageInspectionStatus.Passed;
}
