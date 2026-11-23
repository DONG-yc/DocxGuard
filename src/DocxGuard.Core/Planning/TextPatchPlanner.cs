using System.Security.Cryptography;
using System.Text;
using System.Xml;
using DocxGuard.Core.Indexing;
using DocxGuard.Core.Packaging;

namespace DocxGuard.Core.Planning;

/// <summary>
/// Compiles one exact text replacement into an immutable, non-executable plan.
/// No package or XML content is modified by this type.
/// </summary>
public sealed class TextPatchPlanner
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly TextLocator locator = new();

    public TextPatchPlanningResult CreatePlan(
        WordDocumentIndex index,
        TextReplacementRequest request,
        TextPatchPlanningLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.ExpectedText);
        ArgumentNullException.ThrowIfNull(request.ReplacementText);
        limits ??= TextPatchPlanningLimits.Default;
        limits.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        TextLocationResult location = locator.Locate(
            index,
            request.ExpectedText,
            request.ParagraphScopeId,
            limits.ToLocationLimits(),
            cancellationToken);
        if (location.Status == TextLocationStatus.Rejected)
        {
            bool resourceRejected = location.Findings.Any(
                finding => finding.Code
                    == TextLocationFindingCodes.ResourceLimitExceeded);
            return Rejected(
                request,
                location,
                resourceRejected
                    ? TextPatchPlanningFindingCodes.ResourceLimitExceeded
                    : TextPatchPlanningFindingCodes.LocationRejected,
                resourceRejected
                    ? "Text location exceeded a planning resource limit."
                    : "Text location was rejected before a patch plan could be created.",
                request.ParagraphScopeId);
        }

        if (request.ReplacementText.Length > limits.MaxReplacementTextCharacters)
        {
            return Rejected(
                request,
                location,
                TextPatchPlanningFindingCodes.ResourceLimitExceeded,
                $"The replacement is {request.ReplacementText.Length} characters; the limit is {limits.MaxReplacementTextCharacters}.");
        }

        if (!IsSupportedReplacementText(request.ReplacementText))
        {
            return Rejected(
                request,
                location,
                TextPatchPlanningFindingCodes.InvalidReplacementText,
                "Replacement text must contain XML-safe characters, use LF instead of CR, and cannot synthesize object placeholders.");
        }

        if (string.Equals(
            request.ExpectedText,
            request.ReplacementText,
            StringComparison.Ordinal))
        {
            return Rejected(
                request,
                location,
                TextPatchPlanningFindingCodes.NoOp,
                "Expected and replacement text are identical, so the request is a no-op.");
        }

        if (location.Status == TextLocationStatus.NoMatch)
        {
            return new TextPatchPlanningResult(
                request,
                location,
                TextPatchPlanningStatus.NoMatch,
                null,
                [
                    new TextPatchPlanningFinding(
                        TextPatchPlanningFindingCodes.NoMatch,
                        FindingSeverity.Warning,
                        "No exact target was found; no patch plan was published.",
                        request.ParagraphScopeId),
                ]);
        }

        if (location.Status == TextLocationStatus.Ambiguous)
        {
            return new TextPatchPlanningResult(
                request,
                location,
                TextPatchPlanningStatus.Ambiguous,
                null,
                [
                    new TextPatchPlanningFinding(
                        TextPatchPlanningFindingCodes.AmbiguousMatch,
                        FindingSeverity.Warning,
                        $"The request has {location.Matches.Count} candidates; add a paragraph scope before planning.",
                        request.ParagraphScopeId),
                ]);
        }

        TextMatch match = location.Matches[0];
        if (match.Text.Contains('\uFFFC'))
        {
            return Rejected(
                request,
                location,
                TextPatchPlanningFindingCodes.UnsupportedTarget,
                "Text replacements cannot cross drawing, object, or symbol placeholders.",
                match.ParagraphId);
        }

        if (!index.TryGetParagraph(
            match.ParagraphId,
            out IndexedParagraph? paragraph))
        {
            throw new InvalidOperationException(
                "A location result referenced a paragraph outside its source index.");
        }

        string paragraphHash = ComputeSha256(paragraph!.Text);
        TextPatchPrecondition precondition = new(
            paragraph.Id,
            paragraph.Text,
            paragraphHash,
            match.StartOffset,
            match.Text,
            match.RunSlices);
        TextPatchOperation operation = new(
            "op000001",
            match.Id,
            request.ReplacementText,
            precondition);
        PatchPlan plan = new(
            CreatePlanId(operation),
            [operation]);
        return new TextPatchPlanningResult(
            request,
            location,
            TextPatchPlanningStatus.Ready,
            plan,
            []);
    }

    private static bool IsSupportedReplacementText(string value)
    {
        if (value.Contains('\r') || value.Contains('\uFFFC'))
        {
            return false;
        }

        try
        {
            XmlConvert.VerifyXmlChars(value);
            return true;
        }
        catch (XmlException)
        {
            return false;
        }
    }

    private static string ComputeSha256(string value)
    {
        byte[] hash = SHA256.HashData(StrictUtf8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string CreatePlanId(TextPatchOperation operation)
    {
        using MemoryStream canonical = new();
        using (BinaryWriter writer = new(
            canonical,
            StrictUtf8,
            leaveOpen: true))
        {
            writer.Write(operation.Id);
            writer.Write((int)operation.Kind);
            writer.Write(operation.SourceMatchId);
            writer.Write(operation.ReplacementText);
            writer.Write(operation.Precondition.ParagraphId);
            writer.Write(operation.Precondition.ExpectedParagraphTextSha256);
            writer.Write(operation.Precondition.StartOffset);
            writer.Write(operation.Precondition.ExpectedText);
            writer.Write(operation.Precondition.ExpectedRunSlices.Count);
            foreach (RunTextSlice slice in
                operation.Precondition.ExpectedRunSlices)
            {
                writer.Write(slice.RunId);
                writer.Write(slice.RunOrdinal);
                writer.Write(slice.ParagraphStartOffset);
                writer.Write(slice.StartOffsetInRun);
                writer.Write(slice.Length);
            }
        }

        byte[] hash = SHA256.HashData(canonical.ToArray());
        return $"plan-{Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant()}";
    }

    private static TextPatchPlanningResult Rejected(
        TextReplacementRequest request,
        TextLocationResult location,
        string code,
        string message,
        string? paragraphId = null) =>
        new(
            request,
            location,
            TextPatchPlanningStatus.Rejected,
            null,
            [
                new TextPatchPlanningFinding(
                    code,
                    FindingSeverity.Error,
                    message,
                    paragraphId),
            ]);
}
