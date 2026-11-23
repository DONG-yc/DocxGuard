using System.Collections.ObjectModel;
using System.Security.Cryptography;
using DocxGuard.Core.Packaging;
using DocxGuard.Core.Planning;
using DocxGuard.Core.Wordprocessing;

namespace DocxGuard.Core.Execution;

public static class TextPatchExecutionFindingCodes
{
    public const string SourceInspectionNotPassed = "EXEC_001";
    public const string PreconditionsFailed = "EXEC_002";
    public const string UnsupportedRunContent = "EXEC_003";
    public const string ResourceLimitExceeded = "EXEC_004";
    public const string XmlMutationFailed = "EXEC_005";
    public const string PostconditionFailed = "EXEC_006";
    public const string UnsupportedPlan = "EXEC_007";
}

public sealed record TextPatchExecutionFinding(
    string Code,
    FindingSeverity Severity,
    string Message,
    string? OperationId = null,
    string? ParagraphId = null);

public enum TextPatchExecutionStatus
{
    Passed,
    Failed,
}

public sealed class InMemoryMainDocumentPatch
{
    private readonly byte[] xml;

    internal InMemoryMainDocumentPatch(
        PatchPlan plan,
        string sourcePackageSha256,
        string mainDocumentPartName,
        WordprocessingConformance conformance,
        ReadOnlySpan<byte> originalXml,
        ReadOnlySpan<byte> patchedXml)
    {
        Plan = plan;
        SourcePackageSha256 = sourcePackageSha256;
        MainDocumentPartName = mainDocumentPartName;
        Conformance = conformance;
        OriginalXmlSha256 = ComputeSha256(originalXml);
        PatchedXmlSha256 = ComputeSha256(patchedXml);
        xml = patchedXml.ToArray();
    }

    internal PatchPlan Plan { get; }

    public string PlanId => Plan.Id;

    public string SourcePackageSha256 { get; }

    public string MainDocumentPartName { get; }

    public WordprocessingConformance Conformance { get; }

    public string OriginalXmlSha256 { get; }

    public string PatchedXmlSha256 { get; }

    public int XmlLength => xml.Length;

    public ReadOnlyMemory<byte> Xml => xml.ToArray();

    private static string ComputeSha256(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
}

public sealed class TextPatchExecutionResult
{
    internal TextPatchExecutionResult(
        string planId,
        PatchPreconditionValidationResult? preconditionValidation,
        InMemoryMainDocumentPatch? artifact,
        IEnumerable<TextPatchExecutionFinding> findings)
    {
        PlanId = planId;
        PreconditionValidation = preconditionValidation;
        Artifact = artifact;
        Findings = new ReadOnlyCollection<TextPatchExecutionFinding>(
            findings.ToArray());
        ValidateInvariants();
    }

    public string PlanId { get; }

    public PatchPreconditionValidationResult? PreconditionValidation { get; }

    public InMemoryMainDocumentPatch? Artifact { get; }

    public IReadOnlyList<TextPatchExecutionFinding> Findings { get; }

    public TextPatchExecutionStatus Status => Findings.Any(
        finding => finding.Severity == FindingSeverity.Error)
        ? TextPatchExecutionStatus.Failed
        : TextPatchExecutionStatus.Passed;

    private void ValidateInvariants()
    {
        bool passed = !Findings.Any(
            finding => finding.Severity == FindingSeverity.Error);
        if (passed
            && (Artifact is null
                || PreconditionValidation?.Status
                    != PatchPreconditionValidationStatus.Passed))
        {
            throw new InvalidOperationException(
                "A passed execution must publish an artifact after valid preconditions.");
        }

        if (!passed && Artifact is not null)
        {
            throw new InvalidOperationException(
                "A failed execution cannot publish a partial XML artifact.");
        }
    }
}
