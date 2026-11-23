namespace DocxGuard.Core.Planning;

public sealed record TextReplacementRequest(
    string ExpectedText,
    string ReplacementText,
    string? ParagraphScopeId = null);
