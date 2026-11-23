namespace DocxGuard.Core.Packaging;

public enum FindingSeverity
{
    Warning,
    Error,
}

/// <summary>
/// Stable diagnostic identifiers intended for future CLI and JSON adapters.
/// </summary>
public static class PackageFindingCodes
{
    public const string ArchiveTooLarge = "DOCXGUARD_PKG_001";
    public const string InvalidZip = "DOCXGUARD_PKG_002";
    public const string EntryCountExceeded = "DOCXGUARD_PKG_003";
    public const string InvalidEntryPath = "DOCXGUARD_PKG_004";
    public const string DuplicateEntryName = "DOCXGUARD_PKG_005";
    public const string EncryptedEntry = "DOCXGUARD_PKG_006";
    public const string EntryTooLarge = "DOCXGUARD_PKG_007";
    public const string TotalUncompressedSizeExceeded = "DOCXGUARD_PKG_008";
    public const string CompressionRatioExceeded = "DOCXGUARD_PKG_009";
    public const string EntryDataInvalid = "DOCXGUARD_PKG_010";
    public const string EntryLengthMismatch = "DOCXGUARD_PKG_011";
    public const string CrcMismatch = "DOCXGUARD_PKG_012";
    public const string MissingContentTypes = "DOCXGUARD_PKG_013";
    public const string MissingRootRelationships = "DOCXGUARD_PKG_014";
    public const string UnsupportedZipFeature = "DOCXGUARD_PKG_015";
    public const string HeaderMismatch = "DOCXGUARD_PKG_016";
    public const string InvalidEntryEncoding = "DOCXGUARD_PKG_017";
}

public sealed record PackageFinding(
    string Code,
    FindingSeverity Severity,
    string Message,
    string? EntryName = null);
