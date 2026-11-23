using System.Collections.ObjectModel;
using DocxGuard.Core.Packaging;

namespace DocxGuard.Core.Execution;

public static class PatchPreconditionFindingCodes
{
    public const string ParagraphMissing = "PRE_001";
    public const string ParagraphTextMismatch = "PRE_002";
    public const string ParagraphHashMismatch = "PRE_003";
    public const string TargetRangeMismatch = "PRE_004";
    public const string RunSlicesMismatch = "PRE_005";
    public const string OverlappingTargets = "PRE_006";
}

public sealed record PatchPreconditionFinding(
    string Code,
    FindingSeverity Severity,
    string Message,
    string OperationId,
    string ParagraphId);

public enum PatchPreconditionValidationStatus
{
    Passed,
    Failed,
}

public sealed class PatchPreconditionValidationResult
{
    internal PatchPreconditionValidationResult(
        string planId,
        IEnumerable<PatchPreconditionFinding> findings)
    {
        PlanId = planId;
        Findings = new ReadOnlyCollection<PatchPreconditionFinding>(
            findings.ToArray());
    }

    public string PlanId { get; }

    public IReadOnlyList<PatchPreconditionFinding> Findings { get; }

    public PatchPreconditionValidationStatus Status => Findings.Any(
        finding => finding.Severity == FindingSeverity.Error)
        ? PatchPreconditionValidationStatus.Failed
        : PatchPreconditionValidationStatus.Passed;
}
