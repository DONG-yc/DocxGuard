using System.Collections.ObjectModel;
using DocxGuard.Core.Packaging;

namespace DocxGuard.Core.Wordprocessing;

public sealed class DocxInspectionResult
{
    internal DocxInspectionResult(
        OpcInspectionResult opcInspection,
        WordMainDocumentInfo? mainDocument,
        IEnumerable<WordprocessingFinding> wordprocessingFindings)
    {
        OpcInspection = opcInspection;
        MainDocument = mainDocument;
        WordprocessingFindings = new ReadOnlyCollection<WordprocessingFinding>(
            wordprocessingFindings.ToArray());

        if (OpcInspection.Status == PackageInspectionStatus.Failed
            && (MainDocument is not null || WordprocessingFindings.Count != 0))
        {
            throw new InvalidOperationException(
                "A failed OPC layer cannot publish WordprocessingML results.");
        }

        bool wordprocessingFailed = WordprocessingFindings.Any(
            finding => finding.Severity == FindingSeverity.Error);
        if (wordprocessingFailed && MainDocument is not null)
        {
            throw new InvalidOperationException(
                "A failed WordprocessingML layer cannot publish a partial model.");
        }

        if (OpcInspection.Status == PackageInspectionStatus.Passed
            && !wordprocessingFailed
            && MainDocument is null)
        {
            throw new InvalidOperationException(
                "A passed DOCX inspection must publish its main document.");
        }
    }

    public OpcInspectionResult OpcInspection { get; }

    public WordMainDocumentInfo? MainDocument { get; }

    public IReadOnlyList<WordprocessingFinding> WordprocessingFindings { get; }

    public PackageInspectionStatus Status =>
        OpcInspection.Status == PackageInspectionStatus.Failed
        || WordprocessingFindings.Any(
            finding => finding.Severity == FindingSeverity.Error)
            ? PackageInspectionStatus.Failed
            : PackageInspectionStatus.Passed;
}
