namespace DocxGuard.Core.Packaging;

/// <summary>
/// Stable diagnostics for the OPC Content Types inspection layer.
/// </summary>
public static class ContentTypeFindingCodes
{
    public const string XmlReaderRejected = "DOCXGUARD_CT_001";
    public const string UnsupportedXmlEncoding = "DOCXGUARD_CT_002";
    public const string SchemaViolation = "DOCXGUARD_CT_003";
    public const string InvalidDefaultExtension = "DOCXGUARD_CT_004";
    public const string InvalidOverridePartName = "DOCXGUARD_CT_005";
    public const string InvalidMediaType = "DOCXGUARD_CT_006";
    public const string DuplicateDefault = "DOCXGUARD_CT_007";
    public const string DuplicateOverride = "DOCXGUARD_CT_008";
    public const string UnmappedPart = "DOCXGUARD_CT_009";
    public const string ResourceLimitExceeded = "DOCXGUARD_CT_010";
    public const string RelationshipContentTypeConflict = "DOCXGUARD_CT_011";
    public const string OpcMediaTypeParametersProhibited = "DOCXGUARD_CT_012";
    public const string UnusedOverride = "DOCXGUARD_CT_013";
    public const string InvalidPhysicalPartName = "DOCXGUARD_CT_014";
}

public sealed record ContentTypeFinding(
    string Code,
    FindingSeverity Severity,
    string Message,
    string? EntryName = null,
    string? PartName = null,
    int? LineNumber = null,
    int? LinePosition = null);
