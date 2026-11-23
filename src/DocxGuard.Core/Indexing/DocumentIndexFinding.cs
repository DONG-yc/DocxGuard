using DocxGuard.Core.Packaging;

namespace DocxGuard.Core.Indexing;

public static class DocumentIndexFindingCodes
{
    public const string ResourceLimitExceeded = "IDX_001";
    public const string XmlReaderRejected = "IDX_002";
    public const string StructureViolation = "IDX_003";
}

public sealed record DocumentIndexFinding(
    string Code,
    FindingSeverity Severity,
    string Message,
    string MainDocumentPartName,
    int? LineNumber = null,
    int? LinePosition = null);
