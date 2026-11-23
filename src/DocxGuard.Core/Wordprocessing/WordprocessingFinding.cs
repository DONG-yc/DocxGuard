using DocxGuard.Core.Packaging;

namespace DocxGuard.Core.Wordprocessing;

public static class WordprocessingFindingCodes
{
    public const string MainDocumentRelationshipMissing = "WML_001";
    public const string MultipleMainDocumentRelationships = "WML_002";
    public const string UnsupportedMainDocumentContentType = "WML_003";
    public const string ResourceLimitExceeded = "WML_004";
    public const string UnsupportedXmlEncoding = "WML_005";
    public const string XmlReaderRejected = "WML_006";
    public const string SchemaViolation = "WML_007";
    public const string ConformanceMismatch = "WML_008";
}

public sealed record WordprocessingFinding(
    string Code,
    FindingSeverity Severity,
    string Message,
    string? MainDocumentPartName = null,
    string? RelationshipId = null,
    int? LineNumber = null,
    int? LinePosition = null);
