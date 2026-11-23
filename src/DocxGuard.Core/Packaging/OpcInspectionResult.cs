using System.Collections.ObjectModel;

namespace DocxGuard.Core.Packaging;

public sealed record OpcPartInfo(
    string PackageEntryName,
    OpcPartName PartName,
    ResolvedContentType ContentType);

/// <summary>
/// Result of the package, Content Types, and Relationships trust boundaries.
/// Each later model is published only when its complete layer passes.
/// </summary>
public sealed class OpcInspectionResult
{
    internal OpcInspectionResult(
        PackageInspectionResult packageInspection,
        ContentTypeManifest? manifest,
        IEnumerable<OpcPartInfo> parts,
        IEnumerable<ContentTypeFinding> contentTypeFindings,
        RelationshipGraph? relationshipGraph = null,
        IEnumerable<RelationshipFinding>? relationshipFindings = null)
    {
        PackageInspection = packageInspection;
        ContentTypes = manifest;
        Parts = new ReadOnlyCollection<OpcPartInfo>(parts.ToArray());
        ContentTypeFindings = new ReadOnlyCollection<ContentTypeFinding>(
            contentTypeFindings.ToArray());
        Relationships = relationshipGraph;
        RelationshipFindings = new ReadOnlyCollection<RelationshipFinding>(
            (relationshipFindings ?? []).ToArray());

        bool contentTypeLayerFailed =
            PackageInspection.Status == PackageInspectionStatus.Failed
            || ContentTypeFindings.Any(
                finding => finding.Severity == FindingSeverity.Error);
        if (contentTypeLayerFailed
            && (ContentTypes is not null
                || Parts.Count != 0
                || Relationships is not null
                || RelationshipFindings.Count != 0))
        {
            throw new InvalidOperationException(
                "A failed Content Types layer cannot publish later OPC layers.");
        }

        if (!contentTypeLayerFailed && ContentTypes is null)
        {
            throw new InvalidOperationException(
                "A passed Content Types layer must publish its validated manifest.");
        }

        if (RelationshipFindings.Any(
            finding => finding.Severity == FindingSeverity.Error)
            && Relationships is not null)
        {
            throw new InvalidOperationException(
                "A failed Relationships layer cannot publish a partial graph.");
        }
    }

    public PackageInspectionResult PackageInspection { get; }

    public ContentTypeManifest? ContentTypes { get; }

    public IReadOnlyList<OpcPartInfo> Parts { get; }

    public IReadOnlyList<ContentTypeFinding> ContentTypeFindings { get; }

    public RelationshipGraph? Relationships { get; }

    public IReadOnlyList<RelationshipFinding> RelationshipFindings { get; }

    public PackageInspectionStatus Status => PackageInspection.Status
        == PackageInspectionStatus.Failed
        || ContentTypeFindings.Any(
            finding => finding.Severity == FindingSeverity.Error)
        || RelationshipFindings.Any(
            finding => finding.Severity == FindingSeverity.Error)
            ? PackageInspectionStatus.Failed
            : PackageInspectionStatus.Passed;
}
