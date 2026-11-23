using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxGuard.Core.Building;
using DocxGuard.Core.Execution;
using DocxGuard.Core.Indexing;
using DocxGuard.Core.Packaging;
using DocxGuard.Core.Planning;

namespace DocxGuard.Tests;

public sealed class DocxBuildPipelineTests
{
    [Theory]
    [InlineData(false, "replacement")]
    [InlineData(true, "新名称\t第二项\n下一行")]
    [InlineData(false, "")]
    public void Build_Success_BindsReportToArtifactAndPreservesSource(bool strict, string replacement)
    {
        using MemoryStream source = RebuildTestPackage.Create(strict: strict);
        PatchPlan plan = Plan(source, replacement);
        byte[] before = source.ToArray();
        source.Position = 9;

        DocxBuildResult result = new DocxBuildPipeline().Build(source, plan);

        Assert.Equal(DocxBuildStatus.Passed, result.Status);
        Assert.All(result.Report.Checks, check => Assert.Equal(BuildCheckStatus.Passed, check.Status));
        Assert.Equal(7, result.Report.Checks.Count);
        Assert.Equal(before, source.ToArray());
        Assert.Equal(9, source.Position);
        Assert.True(source.CanRead);
        Assert.Equal(Hash(before), result.Report.SourcePackageSha256);
        Assert.Equal(Hash(result.Artifact!.Bytes.ToArray()), result.Report.OutputPackageSha256);
        Assert.Equal(result.Artifact.PackageLength, result.Report.OutputPackageLength);
        Assert.Equal(plan.Id, result.Report.PlanId);
        Assert.Equal("/word/document.xml", result.Report.MainDocumentPartName);
        Assert.Equal(1, result.Report.ChangedEntryCount);
        Assert.Equal(RebuildTestPackage.Read(before).Count, result.Report.Entries.Count);
        BuildOperationEvidence operation = Assert.Single(result.Report.Operations);
        Assert.Equal(2, operation.RunSlices.Count);
        Assert.Equal(4, operation.StartOffset);
        Assert.Equal(6, operation.RemovedLength);
        Assert.Equal(replacement.Length, operation.InsertedLength);
        Assert.Equal(operation.ExpectedOutputParagraphSha256, operation.ActualOutputParagraphSha256);
        using MemoryStream output = new(result.Artifact.Bytes.ToArray());
        Assert.Equal(PackageInspectionStatus.Passed, new DocxIndexInspector().Inspect(output).Status);
    }

    [Fact]
    public void Build_Json_HasVersionedDeterministicShapeAndNoDocumentTextOrBytes()
    {
        using MemoryStream source = RebuildTestPackage.Create();
        PatchPlan plan = Plan(source, "confidential-replacement-0928");
        var pipeline = new DocxBuildPipeline();
        string json = pipeline.Build(source, plan).Report.ToJson();
        Assert.Equal(json, pipeline.Build(source, plan).Report.ToJson());
        using JsonDocument parsed = JsonDocument.Parse(json);
        var root = parsed.RootElement;
        Assert.Equal("1.0", root.GetProperty("schemaVersion").GetString());
        Assert.Equal("docxguard.single-text.v1", root.GetProperty("profile").GetString());
        Assert.Equal("passed", root.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("checks").ValueKind);
        Assert.Equal(7, root.GetProperty("checks").GetArrayLength());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("entries").ValueKind);
        Assert.Equal(JsonValueKind.Object, root.GetProperty("limits").ValueKind);
        Assert.DoesNotContain("confidential-replacement-0928", json);
        Assert.DoesNotContain("pre target post", json);
        Assert.False(root.TryGetProperty("bytes", out _));
        Assert.False(root.TryGetProperty("artifact", out _));
    }

    [Fact]
    public void Build_StaleParagraph_ReportsPreconditionFailureWithoutOutput()
    {
        using MemoryStream original = RebuildTestPackage.Create();
        PatchPlan plan = Plan(original);
        using MemoryStream changed = RebuildTestPackage.Create(
            "<w:p><w:r><w:t>changed target</w:t></w:r></w:p>");
        var result = new DocxBuildPipeline().Build(changed, plan);
        AssertFailed(result, "preconditions", BuildCheckStatus.Failed);
        Assert.Contains(result.Report.Findings, f => f.Code == PatchPreconditionFindingCodes.ParagraphTextMismatch);
        AssertCheck(result, "rebuild", BuildCheckStatus.NotRun);
        Assert.All(result.Report.Operations, operation => Assert.Null(operation.ActualOutputParagraphSha256));
    }

    [Theory]
    [InlineData("zip")]
    [InlineData("contentTypes")]
    [InlineData("relationships")]
    [InlineData("word")]
    public void Build_RejectedSource_RetainsLayerDiagnostic(string layer)
    {
        using MemoryStream original = RebuildTestPackage.Create();
        PatchPlan plan = Plan(original);
        byte[] bytes = layer == "zip" ? [1, 2, 3]
            : RebuildTestPackage.Rewrite(original.ToArray(), entries =>
            {
                string name = layer switch
                {
                    "contentTypes" => "[Content_Types].xml",
                    "relationships" => "_rels/.rels",
                    _ => RebuildTestPackage.Main,
                };
                entries[name] = Encoding.UTF8.GetBytes("<invalid/>");
            });
        using MemoryStream bad = new(bytes);
        bad.Position = 1;
        var result = new DocxBuildPipeline().Build(bad, plan);
        AssertFailed(result, "sourceInspection", BuildCheckStatus.Failed);
        Assert.Equal(Hash(bytes), result.Report.SourcePackageSha256);
        Assert.Contains(result.Report.Findings, f => f.Stage == "sourceInspection" && f.Severity == FindingSeverity.Error);
        AssertCheck(result, "execution", BuildCheckStatus.NotRun);
        Assert.Equal(1, bad.Position);
    }

    [Fact]
    public void Build_UnsupportedRun_PreconditionsPassButExecutionFails()
    {
        using MemoryStream source = RebuildTestPackage.Create(
            "<w:p><w:r><w:t>target</w:t><w:tab/></w:r></w:p>");
        var result = new DocxBuildPipeline().Build(source, Plan(source));
        AssertFailed(result, "execution", BuildCheckStatus.Failed);
        AssertCheck(result, "preconditions", BuildCheckStatus.Passed);
        Assert.Contains(result.Report.Findings, f => f.Code == TextPatchExecutionFindingCodes.UnsupportedRunContent);
    }

    [Fact]
    public void Build_OutputLimit_FailureDoesNotPublishCandidateEvidence()
    {
        using MemoryStream source = RebuildTestPackage.Create();
        var result = new DocxBuildPipeline().Build(source, Plan(source), new DocxBuildOptions
        {
            Rebuild = new DocxPackageRebuildLimits { MaxOutputPackageBytes = 32 },
        });
        AssertFailed(result, "rebuild", BuildCheckStatus.Failed);
        AssertCheck(result, "execution", BuildCheckStatus.Passed);
        AssertCheck(result, "outputInspection", BuildCheckStatus.NotRun);
        Assert.Contains(result.Report.Findings, f => f.Code == DocxPackageRebuildFindingCodes.OutputResourceLimitExceeded);
    }

    [Fact]
    public void Build_OutputInspectionFailure_ReportsExactBoundary()
    {
        using MemoryStream source = RebuildTestPackage.Create();
        var result = new DocxBuildPipeline().Build(source, Plan(source, new string('x', 10_000)), new DocxBuildOptions
        {
            Opc = OpcInspectionLimits.Default with { MaxPackageSnapshotBytes = checked((int)source.Length + 500) },
        });
        AssertFailed(result, "outputInspection", BuildCheckStatus.Failed);
        AssertCheck(result, "structuralDiff", BuildCheckStatus.NotRun);
        Assert.Contains(result.Report.Findings, f => f.Code == DocxPackageRebuildFindingCodes.OutputInspectionFailed);
        Assert.Contains(result.Report.Findings, f => f.Stage == "outputInspection");
    }

    [Fact]
    public void Build_SnapshotLimit_LeavesSourcePositionAndUnknownHash()
    {
        using MemoryStream source = RebuildTestPackage.Create();
        PatchPlan plan = Plan(source);
        source.Position = 3;
        var result = new DocxBuildPipeline().Build(source, plan, new DocxBuildOptions
        {
            Rebuild = new DocxPackageRebuildLimits { MaxSourcePackageBytes = 1 },
        });
        AssertFailed(result, "snapshot", BuildCheckStatus.Failed);
        Assert.Equal(DocxBuildFindingCodes.SnapshotLimitExceeded, Assert.Single(result.Report.Findings).Code);
        Assert.Null(result.Report.SourcePackageSha256);
        Assert.Equal(3, source.Position);
        using var json = JsonDocument.Parse(result.Report.ToJson());
        Assert.Equal("failed", json.RootElement.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("outputPackageSha256").ValueKind);
    }

    [Fact]
    public void Build_ReturnedBuffersAndCollections_CannotMutatePublishedPair()
    {
        using MemoryStream source = RebuildTestPackage.Create();
        var result = new DocxBuildPipeline().Build(source, Plan(source));
        string report = result.Report.ToJson();
        Assert.True(MemoryMarshal.TryGetArray(result.Artifact!.Bytes, out ArraySegment<byte> bytes));
        bytes.Array![bytes.Offset] ^= 0xff;
        Assert.Equal(result.Report.OutputPackageSha256, Hash(result.Artifact.Bytes.ToArray()));
        Assert.Throws<NotSupportedException>(() => ((IList<BuildCheck>)result.Report.Checks).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<BuildOperationEvidence>)result.Report.Operations).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<RunTextSlice>)result.Report.Operations[0].RunSlices).Clear());
        Assert.Equal(report, result.Report.ToJson());
    }

    [Fact]
    public void Build_InvalidArgumentsAndCancellation_Throw()
    {
        using MemoryStream source = RebuildTestPackage.Create();
        PatchPlan plan = Plan(source);
        var pipeline = new DocxBuildPipeline();
        Assert.Throws<ArgumentNullException>(() => pipeline.Build(null!, plan));
        Assert.Throws<ArgumentNullException>(() => pipeline.Build(source, null!));
        Assert.Throws<ArgumentNullException>(() => pipeline.Build(source, plan, new DocxBuildOptions { Opc = null! }));
        Assert.Throws<ArgumentOutOfRangeException>(() => pipeline.Build(source, plan, new DocxBuildOptions
        {
            Rebuild = new DocxPackageRebuildLimits { MaxOutputPackageBytes = 0 },
        }));
        source.Position = 4;
        Assert.Throws<OperationCanceledException>(() => pipeline.Build(source, plan, cancellationToken: new CancellationToken(true)));
        Assert.Equal(4, source.Position);
    }

    [Fact]
    public void Build_ReadFailure_RestoresPositionAndReturnsDiagnostic()
    {
        using MemoryStream original = RebuildTestPackage.Create();
        using var source = new FailingReadStream(original.ToArray());
        source.Position = 3;
        var result = new DocxBuildPipeline().Build(source, Plan(original));
        AssertFailed(result, "snapshot", BuildCheckStatus.Failed);
        Assert.Equal(DocxBuildFindingCodes.SnapshotReadFailed, Assert.Single(result.Report.Findings).Code);
        Assert.Null(result.Report.SourcePackageSha256);
        Assert.Equal(3, source.Position);
    }

    [Fact]
    public void Build_CancelledDuringSnapshot_RestoresPositionAndThrows()
    {
        using MemoryStream original = RebuildTestPackage.Create();
        using CancellationTokenSource cancellation = new();
        using var source = new CancellingReadStream(original.ToArray(), cancellation);
        source.Position = 7;
        Assert.Throws<OperationCanceledException>(() => new DocxBuildPipeline().Build(
            source, Plan(original), cancellationToken: cancellation.Token));
        Assert.Equal(7, source.Position);
        Assert.True(source.CanRead);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void Build_ChangingSourceLength_RejectsIncompleteSnapshot(int lengthDelta)
    {
        using MemoryStream original = RebuildTestPackage.Create();
        using var source = new WrongLengthStream(original.ToArray(), lengthDelta);
        source.Position = 5;
        var result = new DocxBuildPipeline().Build(source, Plan(original));
        AssertFailed(result, "snapshot", BuildCheckStatus.Failed);
        Assert.Equal(DocxBuildFindingCodes.SnapshotReadFailed, Assert.Single(result.Report.Findings).Code);
        Assert.Null(result.Report.SourcePackageSha256);
        Assert.Equal(5, source.Position);
    }

    private sealed class WrongLengthStream(byte[] bytes, int delta) : MemoryStream(bytes)
    {
        public override long Length => base.Length + delta;
    }

    private sealed class CancellingReadStream(byte[] bytes, CancellationTokenSource cancellation) : MemoryStream(bytes)
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = base.Read(buffer, offset, count);
            cancellation.Cancel();
            return read;
        }
    }

    private sealed class FailingReadStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("Injected read failure.");
    }

    private static PatchPlan Plan(Stream source, string replacement = "replacement") =>
        new TextPatchPlanner().CreatePlan(new DocxIndexInspector().Inspect(source).Index!,
            new TextReplacementRequest("target", replacement)).Plan!;

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void AssertCheck(DocxBuildResult result, string stage, BuildCheckStatus status) =>
        Assert.Equal(status, Assert.Single(result.Report.Checks, check => check.Stage == stage).Status);

    private static void AssertFailed(DocxBuildResult result, string stage, BuildCheckStatus status)
    {
        Assert.Equal(DocxBuildStatus.Failed, result.Status);
        Assert.Null(result.Artifact);
        Assert.Null(result.Report.OutputPackageSha256);
        Assert.Null(result.Report.OutputPackageLength);
        Assert.Empty(result.Report.Entries);
        AssertCheck(result, stage, status);
    }
}
