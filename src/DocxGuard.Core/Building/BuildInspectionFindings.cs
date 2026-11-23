using DocxGuard.Core.Indexing;

namespace DocxGuard.Core.Building;

internal static class BuildInspectionFindings
{
    internal static IEnumerable<BuildFinding> Collect(string stage, DocxIndexInspectionResult inspection)
    {
        var word = inspection.DocumentInspection;
        var opc = word.OpcInspection;
        foreach (var f in opc.PackageInspection.Findings)
            yield return new(stage, f.Code, f.Severity, f.Message, EntryName: f.EntryName);
        foreach (var f in opc.ContentTypeFindings)
            yield return new(stage, f.Code, f.Severity, f.Message, f.EntryName, f.PartName,
                LineNumber: f.LineNumber, LinePosition: f.LinePosition);
        foreach (var f in opc.RelationshipFindings)
            yield return new(stage, f.Code, f.Severity, f.Message, PartName: f.RelationshipsPartName,
                LineNumber: f.LineNumber, LinePosition: f.LinePosition, RelationshipId: f.RelationshipId);
        foreach (var f in word.WordprocessingFindings)
            yield return new(stage, f.Code, f.Severity, f.Message, PartName: f.MainDocumentPartName,
                LineNumber: f.LineNumber, LinePosition: f.LinePosition, RelationshipId: f.RelationshipId);
        foreach (var f in inspection.IndexFindings)
            yield return new(stage, f.Code, f.Severity, f.Message, PartName: f.MainDocumentPartName,
                LineNumber: f.LineNumber, LinePosition: f.LinePosition);
    }
}
