using System.Security.Cryptography;
using System.Text;
using DocxGuard.Core.Execution;
using DocxGuard.Core.Indexing;
using DocxGuard.Core.Packaging;
using DocxGuard.Core.Planning;
using DocxGuard.Core.Verification;

namespace DocxGuard.Core.Building;

public static class DocxBuildFindingCodes
{
    public const string SnapshotLimitExceeded = "BUILD_001";
    public const string SnapshotReadFailed = "BUILD_002";
    public const string BoundSourceMismatch = "BUILD_003";
}

/// <summary>Executes a plan against one bounded private snapshot and returns evidence with its artifact.</summary>
public sealed class DocxBuildPipeline
{
    public DocxBuildResult Build(Stream source, PatchPlan plan,
        DocxBuildOptions? options = null, CancellationToken cancellationToken = default)
        => BuildCore(source, plan, options, cancellationToken, null);

    public DocxBuildResult BuildBound(Stream source, BoundPatchPlan plan,
        DocxBuildOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return BuildCore(source, plan.Plan, options, cancellationToken, plan.SourcePackageSha256);
    }

    public DocxBuildResult BuildBoundMulti(Stream source, BoundMultiPatchPlan plan,
        DocxBuildOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        options ??= new DocxBuildOptions
        {
            Execution = TextPatchExecutionLimits.Default with { MaxOperations = BoundMultiPatchPlan.MaxOperations },
        };
        return BuildCore(source, plan.Plan, options, cancellationToken, plan.SourcePackageSha256, batch: true);
    }

    private static DocxBuildResult BuildCore(Stream source, PatchPlan plan,
        DocxBuildOptions? options, CancellationToken cancellationToken, string? requiredSourceHash, bool batch = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(plan);
        if (!source.CanRead || !source.CanSeek)
            throw new ArgumentException("The source must be readable and seekable.", nameof(source));
        options ??= new DocxBuildOptions();
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        BuildEvidence evidence = new(plan, options, batch || plan.Operations.Count > 1);
        long initialPosition = source.Position;
        try
        {
            byte[] snapshot;
            try
            {
                long length = source.Length;
                long limit = Math.Min(options.Rebuild.MaxSourcePackageBytes,
                    Math.Min(options.Package.MaxArchiveBytes, options.Opc.MaxPackageSnapshotBytes));
                if (length > limit || length < 0)
                {
                    evidence.RejectSnapshot(DocxBuildFindingCodes.SnapshotLimitExceeded,
                        $"The source exceeds the effective snapshot limit of {limit} bytes.");
                    return evidence.Complete();
                }

                snapshot = new byte[checked((int)length)];
                source.Position = 0;
                int offset = 0;
                while (offset < snapshot.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int count = source.Read(snapshot, offset, Math.Min(81_920, snapshot.Length - offset));
                    if (count == 0)
                        throw new IOException("Source ended before its reported length.");
                    offset += count;
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (source.ReadByte() != -1 || source.Length != length)
                    throw new IOException("Source length changed while capturing the snapshot.");
            }
            catch (Exception exception) when (exception is IOException or OverflowException)
            {
                evidence.RejectSnapshot(DocxBuildFindingCodes.SnapshotReadFailed, exception.Message);
                return evidence.Complete();
            }

            evidence.Checks["snapshot"] = BuildCheckStatus.Passed;
            evidence.SourceHash = Convert.ToHexString(SHA256.HashData(snapshot)).ToLowerInvariant();
            if (requiredSourceHash is not null && requiredSourceHash != evidence.SourceHash)
            {
                evidence.RejectSnapshot(DocxBuildFindingCodes.BoundSourceMismatch,
                    "The captured DOCX bytes no longer match the source-bound plan.");
                return evidence.Complete();
            }
            using MemoryStream captured = new(snapshot, writable: false);
            DocxIndexInspectionResult inspection = new DocxIndexInspector().Inspect(captured,
                options.Package, options.Opc, options.Word, options.Index, cancellationToken);
            evidence.Checks["sourceInspection"] = Check(inspection.Status == PackageInspectionStatus.Passed);
            evidence.Findings.AddRange(BuildInspectionFindings.Collect("sourceInspection", inspection));
            evidence.MainPart = inspection.DocumentInspection.MainDocument?.Part.PartName.Value;
            if (inspection.Status != PackageInspectionStatus.Passed)
                return evidence.Complete();

            TextPatchExecutionResult execution = new InMemoryTextPatchExecutor().Execute(
                inspection, plan, options.Execution, options.Word, options.Index, cancellationToken);
            evidence.Checks["execution"] = Check(execution.Status == TextPatchExecutionStatus.Passed);
            if (execution.PreconditionValidation is { } preconditions)
            {
                evidence.Checks["preconditions"] = Check(preconditions.Status == PatchPreconditionValidationStatus.Passed);
                evidence.Findings.AddRange(preconditions.Findings.Select(f => new BuildFinding(
                    "preconditions", f.Code, f.Severity, f.Message,
                    OperationId: f.OperationId, ParagraphId: f.ParagraphId)));
            }

            evidence.Findings.AddRange(execution.Findings.Select(f => new BuildFinding(
                "execution", f.Code, f.Severity, f.Message,
                OperationId: f.OperationId, ParagraphId: f.ParagraphId)));
            if (execution.Status != TextPatchExecutionStatus.Passed)
                return evidence.Complete();

            DocxPackageRebuildResult rebuild = new DocxPackageRebuilder().Rebuild(
                captured, execution.Artifact!, options.Rebuild, options.Package, options.Opc,
                options.Word, options.Index, cancellationToken);
            evidence.Checks["rebuild"] = Check(rebuild.Status == DocxPackageRebuildStatus.Passed);
            evidence.Findings.AddRange(rebuild.Findings.Select(f => new BuildFinding(
                "rebuild", f.Code, f.Severity, f.Message, EntryName: f.EntryName)));
            if (rebuild.SourceInspection is { } rebuildSource)
                evidence.Findings.AddRange(BuildInspectionFindings.Collect("rebuild", rebuildSource));
            if (rebuild.OutputInspection is { } output)
            {
                evidence.Checks["outputInspection"] = Check(output.Status == PackageInspectionStatus.Passed);
                evidence.Findings.AddRange(BuildInspectionFindings.Collect("outputInspection", output));
            }

            if (rebuild.StructuralDiff is { } diff)
            {
                evidence.Checks["structuralDiff"] = Check(diff.Status == StructuralDiffStatus.Passed);
                evidence.Findings.AddRange(diff.Findings.Select(f => new BuildFinding(
                    "structuralDiff", f.Code, f.Severity, f.Message, f.EntryName, XmlPath: f.XmlPath)));
            }

            cancellationToken.ThrowIfCancellationRequested();
            return evidence.Complete(rebuild);
        }
        finally
        {
            source.Position = initialPosition;
        }
    }

    internal static string HashText(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static BuildCheckStatus Check(bool passed) => passed ? BuildCheckStatus.Passed : BuildCheckStatus.Failed;

    private sealed class BuildEvidence(PatchPlan plan, DocxBuildOptions options, bool batch)
    {
        internal Dictionary<string, BuildCheckStatus> Checks { get; } = new(StringComparer.Ordinal)
        {
            ["snapshot"] = BuildCheckStatus.NotRun,
            ["sourceInspection"] = BuildCheckStatus.NotRun,
            ["preconditions"] = BuildCheckStatus.NotRun,
            ["execution"] = BuildCheckStatus.NotRun,
            ["rebuild"] = BuildCheckStatus.NotRun,
            ["outputInspection"] = BuildCheckStatus.NotRun,
            ["structuralDiff"] = BuildCheckStatus.NotRun,
        };

        internal List<BuildFinding> Findings { get; } = [];
        internal string? SourceHash { get; set; }
        internal string? MainPart { get; set; }

        internal void RejectSnapshot(string code, string message)
        {
            Checks["snapshot"] = BuildCheckStatus.Failed;
            Findings.Add(new("snapshot", code, FindingSeverity.Error, message));
        }

        internal DocxBuildResult Complete(DocxPackageRebuildResult? rebuild = null)
        {
            InMemoryDocxPackage? artifact = rebuild?.Artifact;
            var expectedHashes = PatchTextProjection.ExpectedParagraphs(plan)
                .ToDictionary(pair => pair.Key, pair => HashText(pair.Value), StringComparer.Ordinal);
            Dictionary<string, string>? outputHashes = artifact is null ? null
                : rebuild!.OutputInspection!.Index!.Paragraphs
                    .Where(paragraph => expectedHashes.ContainsKey(paragraph.Id))
                    .ToDictionary(paragraph => paragraph.Id, paragraph => HashText(paragraph.Text), StringComparer.Ordinal);
            var operations = plan.Operations.Select(operation => new BuildOperationEvidence(operation,
                expectedHashes.GetValueOrDefault(operation.Precondition.ParagraphId),
                outputHashes?.GetValueOrDefault(operation.Precondition.ParagraphId)));
            BuildReport report = new(plan.Id, SourceHash, MainPart, artifact, options, batch,
                Checks.Select(pair => new BuildCheck(pair.Key, pair.Value)), Findings.Distinct(), operations,
                artifact is null ? [] : rebuild!.StructuralDiff!.Entries);
            return new(report, artifact);
        }
    }
}
