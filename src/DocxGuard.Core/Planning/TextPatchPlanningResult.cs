using System.Collections.ObjectModel;
using DocxGuard.Core.Packaging;

namespace DocxGuard.Core.Planning;

public static class TextPatchPlanningFindingCodes
{
    public const string NoMatch = "PLAN_001";
    public const string AmbiguousMatch = "PLAN_002";
    public const string ResourceLimitExceeded = "PLAN_003";
    public const string UnsupportedTarget = "PLAN_004";
    public const string NoOp = "PLAN_005";
    public const string InvalidReplacementText = "PLAN_006";
    public const string LocationRejected = "PLAN_007";
}

public sealed record TextPatchPlanningFinding(
    string Code,
    FindingSeverity Severity,
    string Message,
    string? ParagraphId = null);

public enum TextPatchPlanningStatus
{
    Ready,
    NoMatch,
    Ambiguous,
    Rejected,
}

public sealed class TextPatchPlanningResult
{
    internal TextPatchPlanningResult(
        TextReplacementRequest request,
        TextLocationResult location,
        TextPatchPlanningStatus status,
        PatchPlan? plan,
        IEnumerable<TextPatchPlanningFinding> findings)
    {
        Request = request;
        Location = location;
        Status = status;
        Plan = plan;
        Findings = new ReadOnlyCollection<TextPatchPlanningFinding>(
            findings.ToArray());
        ValidateInvariants();
    }

    public TextReplacementRequest Request { get; }

    public TextLocationResult Location { get; }

    public TextPatchPlanningStatus Status { get; }

    public PatchPlan? Plan { get; }

    public IReadOnlyList<TextPatchPlanningFinding> Findings { get; }

    private void ValidateInvariants()
    {
        bool hasError = Findings.Any(
            finding => finding.Severity == FindingSeverity.Error);
        bool valid = Status switch
        {
            TextPatchPlanningStatus.Ready =>
                Plan is not null
                && Location.Status == TextLocationStatus.Unique
                && !hasError,
            TextPatchPlanningStatus.NoMatch =>
                Plan is null
                && Location.Status == TextLocationStatus.NoMatch
                && !hasError,
            TextPatchPlanningStatus.Ambiguous =>
                Plan is null
                && Location.Status == TextLocationStatus.Ambiguous
                && !hasError,
            TextPatchPlanningStatus.Rejected =>
                Plan is null && hasError,
            _ => false,
        };
        if (!valid)
        {
            throw new InvalidOperationException(
                "Text patch planning result state is inconsistent.");
        }
    }
}
