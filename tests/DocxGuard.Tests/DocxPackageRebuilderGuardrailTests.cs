using System.IO.Compression;
using System.Runtime.InteropServices;
using DocxGuard.Core.Building;
using DocxGuard.Core.Execution;
using DocxGuard.Core.Packaging;
using DocxGuard.Core.Verification;
using DocxGuard.Core.Wordprocessing;

namespace DocxGuard.Tests;

public sealed class DocxPackageRebuilderGuardrailTests
{
    private readonly DocxPackageRebuilder rebuilder = new();

    [Theory]
    [InlineData("custom/%C3%A9.xml")]
    [InlineData("custom/main.xml")]
    public void Rebuild_EncodedOrNonstandardMainEntry_UsesPhysicalSelectedName(string mainName)
    {
        using MemoryStream source = RebuildTestPackage.Create(mainEntryName: mainName, resources: false);
        InMemoryMainDocumentPatch patch = RebuildTestPackage.Patch(source);

        DocxPackageRebuildResult result = rebuilder.Rebuild(source, patch);

        Assert.Equal(DocxPackageRebuildStatus.Passed, result.Status);
        Assert.Equal(mainName, result.OutputInspection!.DocumentInspection.MainDocument!.Part.PackageEntryName);
        Assert.Equal(patch.Xml.ToArray(), RebuildTestPackage.Read(result.Artifact!.Bytes.ToArray())[mainName]);
    }

    [Fact]
    public void Rebuild_CopyingMediaPastOutputLimit_ReportsOutputLimit()
    {
        using MemoryStream minimal = RebuildTestPackage.Create();
        byte[] bytes = RebuildTestPackage.Rewrite(minimal.ToArray(), entries =>
            entries[RebuildTestPackage.Media] = Enumerable.Range(0, 20_000).Select(i => (byte)i).ToArray());
        using MemoryStream source = new(bytes);
        InMemoryMainDocumentPatch patch = RebuildTestPackage.Patch(source);

        DocxPackageRebuildResult result = rebuilder.Rebuild(source, patch,
            DocxPackageRebuildLimits.Default with { MaxOutputPackageBytes = 4_000 });

        AssertFailure(result, DocxPackageRebuildFindingCodes.OutputResourceLimitExceeded);
    }

    [Fact]
    public void Rebuild_FinalCentralDirectoryPastLimit_IsRejected()
    {
        using MemoryStream source = RebuildTestPackage.Create();
        InMemoryMainDocumentPatch patch = RebuildTestPackage.Patch(source);
        int fullLength = rebuilder.Rebuild(source, patch).Artifact!.PackageLength;

        AssertFailure(rebuilder.Rebuild(source, patch,
            DocxPackageRebuildLimits.Default with { MaxOutputPackageBytes = fullLength - 1 }),
            DocxPackageRebuildFindingCodes.OutputResourceLimitExceeded);
        Assert.Equal(DocxPackageRebuildStatus.Passed, rebuilder.Rebuild(source, patch,
            DocxPackageRebuildLimits.Default with { MaxOutputPackageBytes = fullLength }).Status);
    }

    [Fact]
    public void Rebuild_ExpandedMainXml_EnforcesPerEntryTotalAndWordLimits()
    {
        using MemoryStream source = RebuildTestPackage.Create();
        InMemoryMainDocumentPatch patch = RebuildTestPackage.Patch(source, new string('x', 3_000));
        Dictionary<string, byte[]> entries = RebuildTestPackage.Read(source.ToArray());
        long largest = entries.Values.Max(bytes => bytes.Length);
        long total = entries.Values.Sum(bytes => (long)bytes.Length);

        AssertFailure(rebuilder.Rebuild(source, patch,
            packageLimits: PackageLimits.Default with { MaxEntryUncompressedBytes = largest }),
            DocxPackageRebuildFindingCodes.OutputResourceLimitExceeded);
        AssertFailure(rebuilder.Rebuild(source, patch,
            packageLimits: PackageLimits.Default with { MaxTotalUncompressedBytes = total }),
            DocxPackageRebuildFindingCodes.OutputResourceLimitExceeded);
        DocxPackageRebuildResult wordLimit = rebuilder.Rebuild(source, patch,
            wordprocessingLimits: WordprocessingInspectionLimits.Default with
            {
                MaxMainDocumentXmlBytes = entries[RebuildTestPackage.Main].Length,
            });
        AssertFailure(wordLimit, DocxPackageRebuildFindingCodes.OutputInspectionFailed);
        Assert.Equal(PackageInspectionStatus.Failed, wordLimit.OutputInspection!.Status);
    }

    [Fact]
    public void Rebuild_EntryCountLimit_RejectsSource()
    {
        using MemoryStream source = RebuildTestPackage.Create();
        InMemoryMainDocumentPatch patch = RebuildTestPackage.Patch(source);

        AssertFailure(rebuilder.Rebuild(source, patch,
            packageLimits: PackageLimits.Default with { MaxEntryCount = 3 }),
            DocxPackageRebuildFindingCodes.SourceInspectionNotPassed);
    }

    [Fact]
    public void Rebuild_DeflatedSourceAndDirectory_KeepContentsAndSourcePosition()
    {
        using MemoryStream minimal = RebuildTestPackage.Create();
        byte[] bytes = RebuildTestPackage.Rewrite(minimal.ToArray(), entries =>
            entries["empty/"] = [], CompressionLevel.Optimal);
        using MemoryStream source = new(bytes, writable: false);
        source.Position = 4;
        InMemoryMainDocumentPatch patch = RebuildTestPackage.Patch(source);

        DocxPackageRebuildResult result = rebuilder.Rebuild(source, patch);

        Assert.Equal(DocxPackageRebuildStatus.Passed, result.Status);
        Assert.Equal(4, source.Position);
        Assert.Empty(RebuildTestPackage.Read(result.Artifact!.Bytes.ToArray())["empty/"]);
        Assert.Equal(bytes, source.ToArray());
    }

    [Fact]
    public void Rebuild_ArtifactReturnedBytes_CannotModifyStoredPackageOrPatch()
    {
        using MemoryStream source = RebuildTestPackage.Create();
        InMemoryMainDocumentPatch patch = RebuildTestPackage.Patch(source);
        InMemoryDocxPackage artifact = rebuilder.Rebuild(source, patch).Artifact!;
        byte[] original = artifact.Bytes.ToArray();
        Assert.True(MemoryMarshal.TryGetArray(artifact.Bytes, out ArraySegment<byte> exposed));
        exposed.Array![exposed.Offset] ^= 1;
        Assert.Equal(original, artifact.Bytes.ToArray());
        byte[] originalXml = patch.Xml.ToArray();
        Assert.True(MemoryMarshal.TryGetArray(patch.Xml, out ArraySegment<byte> xml));
        xml.Array![xml.Offset] ^= 1;
        Assert.Equal(originalXml, patch.Xml.ToArray());
    }

    [Fact]
    public void Rebuild_CancellationDuringSnapshot_RestoresStreamAndThrows()
    {
        using MemoryStream original = RebuildTestPackage.Create();
        InMemoryMainDocumentPatch patch = RebuildTestPackage.Patch(original);
        using CancellationTokenSource cancellation = new();
        using CancelOnReadStream source = new(original.ToArray(), cancellation);
        source.Position = 9;

        Assert.Throws<OperationCanceledException>(() => rebuilder.Rebuild(source, patch,
            cancellationToken: cancellation.Token));
        Assert.Equal(9, source.Position);
    }

    [Fact]
    public void Rebuild_TableTargetAndUntouchedField_PassStructuralDiff()
    {
        using MemoryStream source = RebuildTestPackage.Create("""
            <w:tbl><w:tr><w:tc><w:p><w:r><w:t>target</w:t></w:r></w:p></w:tc></w:tr></w:tbl>
            <w:p><w:r><w:fldChar w:fldCharType="begin"/></w:r><w:r><w:t>untouched</w:t></w:r></w:p>
            """);
        InMemoryMainDocumentPatch patch = RebuildTestPackage.Patch(source);

        DocxPackageRebuildResult result = rebuilder.Rebuild(source, patch);

        Assert.Equal(DocxPackageRebuildStatus.Passed, result.Status);
        Assert.Equal(StructuralDiffStatus.Passed, result.StructuralDiff!.Status);
    }

    private static void AssertFailure(DocxPackageRebuildResult result, string code)
    {
        Assert.Equal(DocxPackageRebuildStatus.Failed, result.Status);
        Assert.Null(result.Artifact);
        Assert.Equal(code, Assert.Single(result.Findings).Code);
    }

    private sealed class CancelOnReadStream(byte[] bytes, CancellationTokenSource cancellation)
        : MemoryStream(bytes, writable: false)
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = base.Read(buffer, offset, count);
            cancellation.Cancel();
            return read;
        }
    }
}
