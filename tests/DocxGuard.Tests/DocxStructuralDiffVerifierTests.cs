using System.IO.Compression;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocxGuard.Core.Building;
using DocxGuard.Core.Execution;
using DocxGuard.Core.Packaging;
using DocxGuard.Core.Verification;

namespace DocxGuard.Tests;

public sealed class DocxStructuralDiffVerifierTests
{
    private readonly DocxStructuralDiffVerifier verifier = new();

    [Theory]
    [InlineData(false, "replacement")]
    [InlineData(true, "replacement")]
    [InlineData(false, " spaced\tvalue\nend ")]
    [InlineData(false, "")]
    public void Verify_CrossRunPatch_PassesAndPreservesEveryOtherEntry(bool strict, string replacement)
    {
        using MemoryStream source = RebuildTestPackage.Create(strict: strict);
        InMemoryMainDocumentPatch patch = RebuildTestPackage.Patch(source, replacement);
        DocxPackageRebuildResult rebuilt = new DocxPackageRebuilder().Rebuild(source, patch);

        Assert.Equal(DocxPackageRebuildStatus.Passed, rebuilt.Status);
        Assert.Equal(StructuralDiffStatus.Passed, rebuilt.StructuralDiff!.Status);
        using MemoryStream output = new(rebuilt.Artifact!.Bytes.ToArray(), writable: false);
        source.Position = 3;
        output.Position = 5;
        StructuralDiffResult diff = verifier.Verify(source, output, patch);

        Assert.Equal(StructuralDiffStatus.Passed, diff.Status);
        Assert.Equal(6, diff.Entries.Count);
        Assert.Single(diff.Entries, entry => entry.IsMainDocument);
        Assert.All(diff.Entries.Where(entry => !entry.IsMainDocument), entry =>
        {
            Assert.Equal(entry.SourceSha256, entry.OutputSha256);
            Assert.Equal(entry.SourceLength, entry.OutputLength);
        });
        Assert.Equal(3, source.Position);
        Assert.Equal(5, output.Position);
    }

    [Theory]
    [InlineData("word/styles.xml")]
    [InlineData("word/media/logo.bin")]
    [InlineData("[Content_Types].xml")]
    [InlineData("word/_rels/document.xml.rels")]
    [InlineData("_rels/.rels")]
    public void Verify_ValidButModifiedProtectedEntry_FailsWithEntryName(string entry)
    {
        using MemoryStream source = RebuildTestPackage.Create();
        InMemoryMainDocumentPatch patch = RebuildTestPackage.Patch(source);
        byte[] output = Rebuild(source, patch);
        byte[] tampered = RebuildTestPackage.Rewrite(output, entries =>
        {
            if (entry == RebuildTestPackage.Media)
            {
                entries[entry][0] ^= 1;
            }
            else if (entry.EndsWith(".rels", StringComparison.Ordinal))
            {
                RebuildTestPackage.ReplaceText(entries, entry, "Id=\"r", "Id=\"other");
            }
            else
            {
                // Even a legal lexical change outside the main XML is forbidden.
                entries[entry] = entries[entry].Concat(new byte[] { 0x20 }).ToArray();
            }
        });
        using MemoryStream candidate = new(tampered);

        StructuralDiffResult result = verifier.Verify(source, candidate, patch);

        Assert.Equal(PackageInspectionStatus.Passed, result.OutputInspection!.Status);
        AssertFailure(result, StructuralDiffFindingCodes.ProtectedEntryChanged, entry);
    }

    [Theory]
    [InlineData("<w:b />", "<w:u />", "DIFF_006")]
    [InlineData("w:val=\"left\"", "w:val=\"right\"", "DIFF_006")]
    [InlineData("12240", "12241", "DIFF_006")]
    [InlineData("untouched", "tampered!", "DIFF_006")]
    [InlineData("preserved comment", "modified comment", "DIFF_006")]
    [InlineData("<?docxguard preserved?>", "<?docxguard modified?>", "DIFF_006")]
    [InlineData("replacement", "unapproved", "DIFF_007")]
    [InlineData("<w:r>", "<w:r w:rsidR=\"00000001\">", "DIFF_006")]
    [InlineData("<w:document ", "<w:document xmlns:extra=\"urn:extra\" ", "DIFF_006")]
    public void Verify_AdditionalMainXmlEdit_FailsAtAnXmlPath(string expected, string replacement, string code)
    {
        using MemoryStream source = RebuildTestPackage.Create();
        InMemoryMainDocumentPatch patch = RebuildTestPackage.Patch(source);
        byte[] tampered = RebuildTestPackage.Rewrite(Rebuild(source, patch), entries =>
            RebuildTestPackage.ReplaceText(entries, RebuildTestPackage.Main, expected, replacement));
        using MemoryStream candidate = new(tampered);

        StructuralDiffResult result = verifier.Verify(source, candidate, patch);

        Assert.Equal(PackageInspectionStatus.Passed, result.OutputInspection!.Status);
        AssertFailure(result, code, RebuildTestPackage.Main);
        Assert.StartsWith("/", Assert.Single(result.Findings).XmlPath);
    }

    [Fact]
    public void Verify_UnchangedTextWithDifferentNamespaceBinding_IsRejected()
    {
        using MemoryStream baseSource = RebuildTestPackage.Create();
        byte[] bytes = RebuildTestPackage.Rewrite(baseSource.ToArray(), entries =>
            RebuildTestPackage.ReplaceText(entries, RebuildTestPackage.Main,
                "<w:document ", "<w:document xmlns:aux=\"urn:original\" "));
        using MemoryStream source = new(bytes);
        InMemoryMainDocumentPatch patch = RebuildTestPackage.Patch(source);
        byte[] output = RebuildTestPackage.Rewrite(Rebuild(source, patch), entries =>
            RebuildTestPackage.ReplaceText(entries, RebuildTestPackage.Main, "urn:original", "urn:changed"));
        using MemoryStream candidate = new(output);

        AssertFailure(verifier.Verify(source, candidate, patch),
            StructuralDiffFindingCodes.UnauthorizedXmlChange, RebuildTestPackage.Main);
    }

    [Theory]
    [InlineData("<w:br />", "<w:br w:type=\"page\" />")]
    [InlineData("<w:tab />", "<w:tab w:val=\"extra\" />")]
    [InlineData("xml:space=\"preserve\"", "xml:space=\"default\"")]
    public void Verify_ModifiedReplacementTokenAttributes_AreRejected(string expected, string replacement)
    {
        using MemoryStream source = RebuildTestPackage.Create();
        InMemoryMainDocumentPatch patch = RebuildTestPackage.Patch(source, " a\tb\nc ");
        byte[] output = RebuildTestPackage.Rewrite(Rebuild(source, patch), entries =>
            RebuildTestPackage.ReplaceText(entries, RebuildTestPackage.Main, expected, replacement));
        using MemoryStream candidate = new(output);

        AssertFailure(verifier.Verify(source, candidate, patch),
            StructuralDiffFindingCodes.UnauthorizedXmlChange, RebuildTestPackage.Main);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Verify_AddedOrRemovedUnreferencedEntry_IsRejected(bool add)
    {
        using MemoryStream original = RebuildTestPackage.Create();
        byte[] bytes = RebuildTestPackage.Rewrite(original.ToArray(), entries => entries["extra.bin"] = [1]);
        using MemoryStream source = new(bytes);
        InMemoryMainDocumentPatch patch = RebuildTestPackage.Patch(source);
        byte[] output = RebuildTestPackage.Rewrite(Rebuild(source, patch), entries =>
        {
            if (add)
            {
                entries["added.bin"] = [2];
            }
            else
            {
                entries.Remove("extra.bin");
            }
        });
        using MemoryStream candidate = new(output);

        AssertFailure(verifier.Verify(source, candidate, patch),
            StructuralDiffFindingCodes.EntryInventoryChanged, add ? "added.bin" : "extra.bin");
    }

    [Fact]
    public void Verify_RecompressedZipWithIdenticalEntryBytes_Passes()
    {
        using MemoryStream source = RebuildTestPackage.Create();
        InMemoryMainDocumentPatch patch = RebuildTestPackage.Patch(source);
        byte[] output = Rebuild(source, patch);
        byte[] recompressed = RebuildTestPackage.Rewrite(output, _ => { }, CompressionLevel.Optimal);
        using MemoryStream candidate = new(recompressed);

        Assert.False(output.SequenceEqual(recompressed));
        Assert.Equal(StructuralDiffStatus.Passed, verifier.Verify(source, candidate, patch).Status);
    }

    [Fact]
    public void Verify_SameMainXmlButDifferentSourceResource_IsRejected()
    {
        using MemoryStream source = RebuildTestPackage.Create();
        InMemoryMainDocumentPatch patch = RebuildTestPackage.Patch(source);
        byte[] changed = RebuildTestPackage.Rewrite(source.ToArray(), entries => entries[RebuildTestPackage.Media][0] ^= 1);
        using MemoryStream otherSource = new(changed);
        using MemoryStream output = new(Rebuild(source, patch));

        AssertFailure(verifier.Verify(otherSource, output, patch), StructuralDiffFindingCodes.SourceMismatch);
        DocxPackageRebuildResult rebuilt = new DocxPackageRebuilder().Rebuild(otherSource, patch);
        Assert.Null(rebuilt.Artifact);
        Assert.Contains(rebuilt.Findings, f => f.Code == DocxPackageRebuildFindingCodes.PatchDoesNotMatchSource);
    }

    [Fact]
    public void Verify_InvalidOutputOrResourceLimit_PublishesNoEvidence()
    {
        using MemoryStream source = RebuildTestPackage.Create();
        InMemoryMainDocumentPatch patch = RebuildTestPackage.Patch(source);
        using MemoryStream corrupt = new([1, 2, 3]);
        using MemoryStream output = new(Rebuild(source, patch));

        AssertFailure(verifier.Verify(source, corrupt, patch), StructuralDiffFindingCodes.InspectionFailed);
        AssertFailure(verifier.Verify(source, output, patch,
            PackageLimits.Default with { MaxArchiveBytes = 1 }), StructuralDiffFindingCodes.SnapshotLimitExceeded);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => verifier.Verify(source, output, patch,
            cancellationToken: cancellation.Token));
        Assert.Throws<ArgumentNullException>(() => verifier.Verify(null!, output, patch));
        Assert.Throws<ArgumentNullException>(() => verifier.Verify(source, null!, patch));
        Assert.Throws<ArgumentNullException>(() => verifier.Verify(source, output, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => verifier.Verify(source, output, patch,
            PackageLimits.Default with { MaxEntryCount = 0 }));
    }

    [Fact]
    public void Rebuild_SdkGeneratedDocument_ReopensAndPassesSdkValidation()
    {
        using MemoryStream source = new();
        using (WordprocessingDocument document = WordprocessingDocument.Create(source,
            DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            document.AddMainDocumentPart().Document = new DocumentFormat.OpenXml.Wordprocessing.Document(
                new DocumentFormat.OpenXml.Wordprocessing.Body(
                    new DocumentFormat.OpenXml.Wordprocessing.Paragraph(
                        new DocumentFormat.OpenXml.Wordprocessing.Run(
                            new DocumentFormat.OpenXml.Wordprocessing.Text("target")))));
        }

        InMemoryMainDocumentPatch patch = RebuildTestPackage.Patch(source);
        using MemoryStream output = new(Rebuild(source, patch));
        using WordprocessingDocument reopened = WordprocessingDocument.Open(output, false);

        Assert.Equal("replacement", reopened.MainDocumentPart!.Document!.InnerText);
        Assert.Empty(new OpenXmlValidator().Validate(reopened));
    }

    private static byte[] Rebuild(Stream source, InMemoryMainDocumentPatch patch)
    {
        DocxPackageRebuildResult result = new DocxPackageRebuilder().Rebuild(source, patch);
        Assert.Equal(DocxPackageRebuildStatus.Passed, result.Status);
        return result.Artifact!.Bytes.ToArray();
    }

    private static void AssertFailure(StructuralDiffResult result, string code, string? entry = null)
    {
        Assert.Equal(StructuralDiffStatus.Failed, result.Status);
        Assert.Empty(result.Entries);
        StructuralDiffFinding finding = Assert.Single(result.Findings);
        Assert.Equal(code, finding.Code);
        if (entry is not null)
        {
            Assert.Equal(entry, finding.EntryName);
        }
    }
}
