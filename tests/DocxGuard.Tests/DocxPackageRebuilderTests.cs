using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DocxGuard.Core.Building;
using DocxGuard.Core.Execution;
using DocxGuard.Core.Indexing;
using DocxGuard.Core.Packaging;
using DocxGuard.Core.Planning;
using DocxGuard.Core.Wordprocessing;

namespace DocxGuard.Tests;

public sealed class DocxPackageRebuilderTests
{
    private const string ContentTypesNamespace =
        "http://schemas.openxmlformats.org/package/2006/content-types";
    private const string RelationshipsNamespace =
        "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string RelationshipsContentType =
        "application/vnd.openxmlformats-package.relationships+xml";

    private readonly DocxPackageRebuilder rebuilder = new();

    [Fact]
    public void Rebuild_RelationshipSelectedMainPart_ReplacesOnlyThatEntry()
    {
        using MemoryStream source = CreatePackage(
            "<w:p><w:r><w:t>before target after</w:t></w:r></w:p>",
            mainEntryName: "custom/main.xml");
        InMemoryMainDocumentPatch patch = CreatePatch(
            source,
            "target",
            "replacement");
        byte[] originalPackage = source.ToArray();
        Dictionary<string, byte[]> originalEntries = ReadEntries(originalPackage);
        source.Position = 7;

        DocxPackageRebuildResult result = rebuilder.Rebuild(source, patch);

        Assert.Equal(DocxPackageRebuildStatus.Passed, result.Status);
        Assert.Empty(result.Findings);
        Assert.Equal(PackageInspectionStatus.Passed, result.SourceInspection!.Status);
        Assert.Equal(PackageInspectionStatus.Passed, result.OutputInspection!.Status);
        Assert.Equal("before replacement after", result.OutputInspection.Index!.PlainText);
        Assert.Equal(7, source.Position);
        Assert.Equal(originalPackage, source.ToArray());

        InMemoryDocxPackage artifact = Assert.IsType<InMemoryDocxPackage>(
            result.Artifact);
        Assert.Equal(patch.PlanId, artifact.PlanId);
        Assert.Equal("/custom/main.xml", artifact.MainDocumentPartName);
        Assert.Equal(ComputeSha256(originalPackage), artifact.SourcePackageSha256);
        Assert.Equal(
            ComputeSha256(artifact.Bytes.Span),
            artifact.OutputPackageSha256);
        Assert.Equal(patch.OriginalXmlSha256,
            artifact.OriginalMainDocumentXmlSha256);
        Assert.Equal(patch.PatchedXmlSha256,
            artifact.PatchedMainDocumentXmlSha256);

        Dictionary<string, byte[]> rebuiltEntries = ReadEntries(
            artifact.Bytes.ToArray());
        Assert.Equal(originalEntries.Keys, rebuiltEntries.Keys);
        Assert.False(
            originalEntries["custom/main.xml"].SequenceEqual(
                rebuiltEntries["custom/main.xml"]));
        foreach (string name in originalEntries.Keys.Where(
            name => name != "custom/main.xml"))
        {
            Assert.Equal(originalEntries[name], rebuiltEntries[name]);
        }
    }

    [Fact]
    public void Rebuild_StrictDocument_RetainsConformanceAndParsesAgain()
    {
        using MemoryStream source = CreatePackage(
            "<w:p><w:r><w:t>target</w:t></w:r></w:p>",
            strict: true);
        InMemoryMainDocumentPatch patch = CreatePatch(
            source,
            "target",
            "strict replacement");

        DocxPackageRebuildResult result = rebuilder.Rebuild(source, patch);

        Assert.Equal(DocxPackageRebuildStatus.Passed, result.Status);
        Assert.Equal(
            WordprocessingConformance.Strict,
            result.OutputInspection!.DocumentInspection.MainDocument!.Conformance);
        Assert.Equal("strict replacement", result.OutputInspection.Index!.PlainText);
    }

    [Fact]
    public void Rebuild_PatchFromAnotherSource_IsRejected()
    {
        using MemoryStream plannedSource = CreatePackage(
            "<w:p><w:r><w:t>target planned</w:t></w:r></w:p>");
        using MemoryStream currentSource = CreatePackage(
            "<w:p><w:r><w:t>target current</w:t></w:r></w:p>");
        InMemoryMainDocumentPatch patch = CreatePatch(
            plannedSource,
            "target",
            "replacement");

        DocxPackageRebuildResult result = rebuilder.Rebuild(
            currentSource,
            patch);

        AssertFailure(
            result,
            DocxPackageRebuildFindingCodes.PatchDoesNotMatchSource);
        Assert.Equal(PackageInspectionStatus.Passed, result.SourceInspection!.Status);
    }

    [Fact]
    public void Rebuild_CorruptSource_IsRejectedWithoutArtifact()
    {
        using MemoryStream validSource = CreatePackage(
            "<w:p><w:r><w:t>target</w:t></w:r></w:p>");
        InMemoryMainDocumentPatch patch = CreatePatch(
            validSource,
            "target",
            "replacement");
        using MemoryStream corrupt = new([0x01, 0x02, 0x03]);

        DocxPackageRebuildResult result = rebuilder.Rebuild(corrupt, patch);

        AssertFailure(
            result,
            DocxPackageRebuildFindingCodes.SourceInspectionNotPassed);
    }

    [Fact]
    public void Rebuild_DuplicateEntrySource_IsRejectedWithoutArtifact()
    {
        using MemoryStream validSource = CreatePackage(
            "<w:p><w:r><w:t>target</w:t></w:r></w:p>");
        InMemoryMainDocumentPatch patch = CreatePatch(
            validSource,
            "target",
            "replacement");
        using MemoryStream duplicateSource = CreatePackage(
            "<w:p><w:r><w:t>target</w:t></w:r></w:p>",
            duplicateMainEntry: true);

        DocxPackageRebuildResult result = rebuilder.Rebuild(
            duplicateSource,
            patch);

        AssertFailure(
            result,
            DocxPackageRebuildFindingCodes.SourceInspectionNotPassed);
        Assert.Contains(
            result.SourceInspection!
                .DocumentInspection
                .OpcInspection
                .PackageInspection
                .Findings,
            finding => finding.Code == PackageFindingCodes.DuplicateEntryName);
    }

    [Fact]
    public void Rebuild_SourceSnapshotAndOutputLimits_AreEnforced()
    {
        using MemoryStream source = CreatePackage(
            "<w:p><w:r><w:t>target</w:t></w:r></w:p>");
        InMemoryMainDocumentPatch patch = CreatePatch(
            source,
            "target",
            "replacement");

        DocxPackageRebuildResult sourceLimit = rebuilder.Rebuild(
            source,
            patch,
            DocxPackageRebuildLimits.Default with
            {
                MaxSourcePackageBytes = checked((int)source.Length - 1),
            });
        DocxPackageRebuildResult outputLimit = rebuilder.Rebuild(
            source,
            patch,
            DocxPackageRebuildLimits.Default with
            {
                MaxOutputPackageBytes = 32,
            });

        AssertFailure(
            sourceLimit,
            DocxPackageRebuildFindingCodes.SourceSnapshotLimitExceeded);
        AssertFailure(
            outputLimit,
            DocxPackageRebuildFindingCodes.OutputResourceLimitExceeded);
    }

    [Fact]
    public void Rebuild_InvalidArgumentsLimitsAndCancellation_Throw()
    {
        using MemoryStream source = CreatePackage(
            "<w:p><w:r><w:t>target</w:t></w:r></w:p>");
        InMemoryMainDocumentPatch patch = CreatePatch(
            source,
            "target",
            "replacement");
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.Throws<ArgumentNullException>(() =>
            rebuilder.Rebuild((Stream)null!, patch));
        Assert.Throws<ArgumentNullException>(() =>
            rebuilder.Rebuild(source, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            rebuilder.Rebuild(
                source,
                patch,
                DocxPackageRebuildLimits.Default with
                {
                    MaxOutputPackageBytes = 0,
                }));
        Assert.Throws<OperationCanceledException>(() =>
            rebuilder.Rebuild(
                source,
                patch,
                cancellationToken: cancellation.Token));
    }

    private static InMemoryMainDocumentPatch CreatePatch(
        MemoryStream source,
        string expected,
        string replacement)
    {
        DocxIndexInspectionResult inspection =
            new DocxIndexInspector().Inspect(source);
        Assert.Equal(PackageInspectionStatus.Passed, inspection.Status);
        PatchPlan plan = new TextPatchPlanner().CreatePlan(
            inspection.Index!,
            new TextReplacementRequest(expected, replacement)).Plan!;
        TextPatchExecutionResult execution =
            new InMemoryTextPatchExecutor().Execute(inspection, plan);
        Assert.Equal(TextPatchExecutionStatus.Passed, execution.Status);
        return execution.Artifact!;
    }

    private static MemoryStream CreatePackage(
        string bodyContent,
        string mainEntryName = "word/document.xml",
        bool strict = false,
        bool duplicateMainEntry = false)
    {
        string wordNamespace = strict
            ? IndexTestPackage.StrictWordNamespace
            : IndexTestPackage.TransitionalWordNamespace;
        string relationshipType = strict
            ? DocxDocumentInspector.StrictOfficeDocumentRelationshipType
            : DocxDocumentInspector.TransitionalOfficeDocumentRelationshipType;
        string documentXml = $"""
            <w:document xmlns:w="{wordNamespace}">
              <w:body>{bodyContent}</w:body>
            </w:document>
            """;
        string contentTypes = $"""
            <Types xmlns="{ContentTypesNamespace}">
              <Default Extension="rels" ContentType="{RelationshipsContentType}" />
              <Override PartName="/{mainEntryName}" ContentType="{DocxDocumentInspector.MainDocumentContentType}" />
            </Types>
            """;
        string relationships = $"""
            <Relationships xmlns="{RelationshipsNamespace}">
              <Relationship Id="rId1" Type="{relationshipType}" Target="{mainEntryName}" />
            </Relationships>
            """;

        MemoryStream stream = new();
        using (ZipArchive archive = new(
            stream,
            ZipArchiveMode.Create,
            leaveOpen: true))
        {
            WriteEntry(archive, "[Content_Types].xml", contentTypes);
            WriteEntry(archive, "_rels/.rels", relationships);
            WriteEntry(archive, mainEntryName, documentXml);
            if (duplicateMainEntry)
            {
                WriteEntry(archive, mainEntryName, documentXml);
            }
        }

        stream.Position = 0;
        return stream;
    }

    private static void WriteEntry(
        ZipArchive archive,
        string name,
        string value)
    {
        ZipArchiveEntry entry = archive.CreateEntry(
            name,
            CompressionLevel.NoCompression);
        using Stream entryStream = entry.Open();
        entryStream.Write(Encoding.UTF8.GetBytes(value));
    }

    private static Dictionary<string, byte[]> ReadEntries(byte[] package)
    {
        using MemoryStream stream = new(package, writable: false);
        using ZipArchive archive = new(stream, ZipArchiveMode.Read);
        return archive.Entries.ToDictionary(
            entry => entry.FullName,
            entry =>
            {
                using Stream content = entry.Open();
                using MemoryStream copy = new();
                content.CopyTo(copy);
                return copy.ToArray();
            },
            StringComparer.Ordinal);
    }

    private static string ComputeSha256(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static void AssertFailure(
        DocxPackageRebuildResult result,
        string expectedCode)
    {
        Assert.Equal(DocxPackageRebuildStatus.Failed, result.Status);
        Assert.Null(result.Artifact);
        Assert.Contains(result.Findings, finding => finding.Code == expectedCode);
    }
}
