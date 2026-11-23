using DocxGuard.Core.Indexing;
using DocxGuard.Core.Packaging;

namespace DocxGuard.Core.Planning;

/// <summary>
/// Locates exact, ordinal text matches inside individual indexed paragraphs.
/// All overlapping candidates are retained so ambiguity cannot be hidden.
/// </summary>
public sealed class TextLocator
{
    public TextLocationResult Locate(
        WordDocumentIndex index,
        string query,
        string? paragraphScopeId = null,
        TextLocationLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(query);
        if (query.Length == 0)
        {
            throw new ArgumentException(
                "The location query cannot be empty.",
                nameof(query));
        }

        if (paragraphScopeId is not null
            && string.IsNullOrWhiteSpace(paragraphScopeId))
        {
            throw new ArgumentException(
                "The paragraph scope ID cannot be empty or whitespace.",
                nameof(paragraphScopeId));
        }

        limits ??= TextLocationLimits.Default;
        limits.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        if (query.Length > limits.MaxQueryCharacters)
        {
            return Rejected(
                query,
                paragraphScopeId,
                TextLocationFindingCodes.ResourceLimitExceeded,
                $"The query is {query.Length} characters; the limit is {limits.MaxQueryCharacters}.");
        }

        IReadOnlyList<IndexedParagraph> paragraphs;
        if (paragraphScopeId is null)
        {
            paragraphs = index.Paragraphs;
        }
        else if (index.TryGetParagraph(
            paragraphScopeId,
            out IndexedParagraph? scopedParagraph))
        {
            paragraphs = [scopedParagraph!];
        }
        else
        {
            return Rejected(
                query,
                paragraphScopeId,
                TextLocationFindingCodes.ParagraphScopeNotFound,
                $"Paragraph scope '{paragraphScopeId}' does not exist in the index.",
                paragraphScopeId);
        }

        List<TextMatch> matches = [];
        foreach (IndexedParagraph paragraph in paragraphs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int searchOffset = 0;
            while (searchOffset <= paragraph.Text.Length - query.Length)
            {
                int matchOffset = paragraph.Text.IndexOf(
                    query,
                    searchOffset,
                    StringComparison.Ordinal);
                if (matchOffset < 0)
                {
                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (matches.Count >= limits.MaxMatches)
                {
                    return Rejected(
                        query,
                        paragraphScopeId,
                        TextLocationFindingCodes.ResourceLimitExceeded,
                        $"The query exceeds the match limit of {limits.MaxMatches}.");
                }

                matches.Add(CreateMatch(
                    matches.Count,
                    paragraph,
                    matchOffset,
                    query.Length));
                searchOffset = matchOffset + 1;
            }
        }

        if (matches.Count == 0)
        {
            return new TextLocationResult(
                query,
                paragraphScopeId,
                TextLocationStatus.NoMatch,
                [],
                [
                    new TextLocationFinding(
                        TextLocationFindingCodes.NoMatch,
                        FindingSeverity.Warning,
                        "The exact text was not found in the selected paragraph scope.",
                        paragraphScopeId),
                ]);
        }

        if (matches.Count == 1)
        {
            return new TextLocationResult(
                query,
                paragraphScopeId,
                TextLocationStatus.Unique,
                matches,
                []);
        }

        return new TextLocationResult(
            query,
            paragraphScopeId,
            TextLocationStatus.Ambiguous,
            matches,
            [
                new TextLocationFinding(
                    TextLocationFindingCodes.AmbiguousMatch,
                    FindingSeverity.Warning,
                    $"The exact text has {matches.Count} candidate locations.",
                    paragraphScopeId),
            ]);
    }

    private static TextMatch CreateMatch(
        int matchOrdinal,
        IndexedParagraph paragraph,
        int startOffset,
        int length)
    {
        RunTextSlice[] slices = RunTextSliceMapper.Map(
            paragraph,
            startOffset,
            length);

        return new TextMatch(
            $"m{matchOrdinal + 1:D6}",
            paragraph.Id,
            paragraph.Ordinal,
            paragraph.TableCellId,
            startOffset,
            paragraph.Text.Substring(startOffset, length),
            slices);
    }

    private static TextLocationResult Rejected(
        string query,
        string? paragraphScopeId,
        string code,
        string message,
        string? findingParagraphId = null) =>
        new(
            query,
            paragraphScopeId,
            TextLocationStatus.Rejected,
            [],
            [
                new TextLocationFinding(
                    code,
                    FindingSeverity.Error,
                    message,
                    findingParagraphId),
            ]);
}
