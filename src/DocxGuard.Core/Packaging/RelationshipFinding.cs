namespace DocxGuard.Core.Packaging;

public static class RelationshipFindingCodes
{
    public const string XmlReaderRejected = "REL_001";
    public const string UnsupportedXmlEncoding = "REL_002";
    public const string SchemaViolation = "REL_003";
    public const string ResourceLimitExceeded = "REL_004";
    public const string InvalidId = "REL_005";
    public const string DuplicateId = "REL_006";
    public const string InvalidType = "REL_007";
    public const string InvalidTargetMode = "REL_008";
    public const string ExternalRelationshipProhibited = "REL_009";
    public const string InvalidInternalTarget = "REL_010";
    public const string TargetPartMissing = "REL_011";
    public const string SourcePartMissing = "REL_012";
    public const string RelationshipPartReferenceProhibited = "REL_013";
}

public sealed record RelationshipFinding(
    string Code,
    FindingSeverity Severity,
    string Message,
    string RelationshipsPartName,
    string? SourcePartName = null,
    string? RelationshipId = null,
    string? RawTarget = null,
    int? LineNumber = null,
    int? LinePosition = null);
