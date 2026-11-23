using DocxGuard.Core.Packaging;

namespace DocxGuard.Core.Planning;

public static class TextLocationFindingCodes
{
    public const string NoMatch = "LOC_001";
    public const string AmbiguousMatch = "LOC_002";
    public const string ResourceLimitExceeded = "LOC_003";
    public const string ParagraphScopeNotFound = "LOC_004";
}

public sealed record TextLocationFinding(
    string Code,
    FindingSeverity Severity,
    string Message,
    string? ParagraphId = null);
