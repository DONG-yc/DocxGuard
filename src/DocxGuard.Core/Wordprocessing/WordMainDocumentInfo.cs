using DocxGuard.Core.Packaging;

namespace DocxGuard.Core.Wordprocessing;

public enum WordprocessingConformance
{
    Transitional,
    Strict,
}

public sealed record WordMainDocumentInfo(
    string RelationshipId,
    string RelationshipType,
    OpcPartInfo Part,
    WordprocessingConformance Conformance,
    int BodyChildElementCount,
    bool HasSectionProperties);
