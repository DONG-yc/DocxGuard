using System.Collections.ObjectModel;
using DocxGuard.Core.Packaging;

namespace DocxGuard.Core.Planning;

public enum TextLocationStatus
{
    NoMatch,
    Unique,
    Ambiguous,
    Rejected,
}

public sealed class TextLocationResult
{
    internal TextLocationResult(
        string query,
        string? paragraphScopeId,
        TextLocationStatus status,
        IEnumerable<TextMatch> matches,
        IEnumerable<TextLocationFinding> findings)
    {
        Query = query;
        ParagraphScopeId = paragraphScopeId;
        Status = status;
        Matches = new ReadOnlyCollection<TextMatch>(matches.ToArray());
        Findings = new ReadOnlyCollection<TextLocationFinding>(
            findings.ToArray());
        ValidateInvariants();
    }

    public string Query { get; }

    public string? ParagraphScopeId { get; }

    public TextLocationStatus Status { get; }

    public IReadOnlyList<TextMatch> Matches { get; }

    public IReadOnlyList<TextLocationFinding> Findings { get; }

    private void ValidateInvariants()
    {
        bool hasError = Findings.Any(
            finding => finding.Severity == FindingSeverity.Error);
        bool valid = Status switch
        {
            TextLocationStatus.NoMatch => Matches.Count == 0 && !hasError,
            TextLocationStatus.Unique => Matches.Count == 1 && !hasError,
            TextLocationStatus.Ambiguous => Matches.Count > 1 && !hasError,
            TextLocationStatus.Rejected => Matches.Count == 0 && hasError,
            _ => false,
        };
        if (!valid)
        {
            throw new InvalidOperationException(
                "Text location result state is inconsistent.");
        }
    }
}
