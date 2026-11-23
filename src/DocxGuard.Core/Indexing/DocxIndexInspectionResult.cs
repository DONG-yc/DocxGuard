using System.Collections.ObjectModel;
using DocxGuard.Core.Packaging;
using DocxGuard.Core.Wordprocessing;

namespace DocxGuard.Core.Indexing;

public sealed class DocxIndexInspectionResult
{
    internal DocxIndexInspectionResult(
        DocxInspectionResult documentInspection,
        WordDocumentIndex? index,
        IEnumerable<DocumentIndexFinding> indexFindings,
        ReadOnlyMemory<byte>? mainDocumentXml,
        string? sourcePackageSha256 = null)
    {
        DocumentInspection = documentInspection;
        Index = index;
        IndexFindings = new ReadOnlyCollection<DocumentIndexFinding>(
            indexFindings.ToArray());
        MainDocumentXml = mainDocumentXml;
        SourcePackageSha256 = sourcePackageSha256;

        if (DocumentInspection.Status == PackageInspectionStatus.Failed
            && (Index is not null || IndexFindings.Count != 0))
        {
            throw new InvalidOperationException(
                "A failed document layer cannot publish index results.");
        }

        bool indexFailed = IndexFindings.Any(
            finding => finding.Severity == FindingSeverity.Error);
        if (indexFailed && Index is not null)
        {
            throw new InvalidOperationException(
                "A failed indexing layer cannot publish a partial index.");
        }

        if (DocumentInspection.Status == PackageInspectionStatus.Passed
            && !indexFailed
            && Index is null)
        {
            throw new InvalidOperationException(
                "A passed index inspection must publish its index.");
        }

        if ((Index is null) != (SourcePackageSha256 is null)
            || (Index is null) != (MainDocumentXml is null)
            || (MainDocumentXml is not null && MainDocumentXml.Value.IsEmpty))
        {
            throw new InvalidOperationException(
                "A document index and its verified XML snapshot must be published together.");
        }
    }

    public DocxInspectionResult DocumentInspection { get; }

    public WordDocumentIndex? Index { get; }

    public string? SourcePackageSha256 { get; }

    public IReadOnlyList<DocumentIndexFinding> IndexFindings { get; }

    internal ReadOnlyMemory<byte>? MainDocumentXml { get; }

    public PackageInspectionStatus Status =>
        DocumentInspection.Status == PackageInspectionStatus.Failed
        || IndexFindings.Any(
            finding => finding.Severity == FindingSeverity.Error)
            ? PackageInspectionStatus.Failed
            : PackageInspectionStatus.Passed;
}
