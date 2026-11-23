using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxGuard.Core.Packaging;

namespace DocxGuard.Tests;

public sealed class SafePackageReaderTests
{
    private static readonly byte[] ContentTypesContent = "<Types/>"u8.ToArray();
    private static readonly byte[] RootRelationshipsContent = "<Relationships/>"u8.ToArray();

    private readonly SafePackageReader reader = new();

    [Fact]
    public void Inspect_ValidMinimalPackage_PassesAndVerifiesEveryEntry()
    {
        byte[] package = CreatePackage();
        byte[] hashBefore = SHA256.HashData(package);
        using MemoryStream stream = new(package, writable: false);
        stream.Position = 7;

        PackageInspectionResult result = reader.Inspect(stream);

        Assert.Equal(PackageInspectionStatus.Passed, result.Status);
        Assert.Empty(result.Findings);
        Assert.Equal(2, result.Entries.Count);
        Assert.All(result.Entries, entry => Assert.True(entry.IntegrityVerified));
        Assert.Equal(7, stream.Position);
        Assert.True(stream.CanRead);
        Assert.Equal(hashBefore, SHA256.HashData(stream.ToArray()));
    }

    [Fact]
    public void Inspect_DocumentCreatedByOpenXmlSdk_Passes()
    {
        using MemoryStream generatedDocument = new();
        using (WordprocessingDocument document = WordprocessingDocument.Create(
            generatedDocument,
            DocumentFormat.OpenXml.WordprocessingDocumentType.Document,
            autoSave: true))
        {
            MainDocumentPart mainPart = document.AddMainDocumentPart();
            mainPart.Document = new Document(
                new Body(
                    new Paragraph(
                        new Run(
                            new Text("DocxGuard")))));
        }

        using MemoryStream stream = new(generatedDocument.ToArray());

        PackageInspectionResult result = reader.Inspect(stream);

        Assert.Equal(PackageInspectionStatus.Passed, result.Status);
        Assert.Contains(result.Entries, entry => entry.Name == "word/document.xml");
        Assert.All(result.Entries, entry => Assert.True(entry.IntegrityVerified));
    }

    [Theory]
    [InlineData("")]
    [InlineData("this is not a zip file")]
    [InlineData("PK")]
    public void Inspect_InvalidZip_ReturnsStructuredFailure(string content)
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(content));

        PackageInspectionResult result = reader.Inspect(stream);

        AssertFailedWith(result, PackageFindingCodes.InvalidZip);
    }

    [Fact]
    public void Inspect_ArchiveOverConfiguredLimit_FailsBeforeZipParsing()
    {
        byte[] content = "not-a-zip"u8.ToArray();
        using MemoryStream stream = new(content);
        PackageLimits limits = PackageLimits.Default with
        {
            MaxArchiveBytes = content.Length - 1,
        };

        PackageInspectionResult result = reader.Inspect(stream, limits);

        AssertFailedWith(result, PackageFindingCodes.ArchiveTooLarge);
        Assert.DoesNotContain(
            result.Findings,
            finding => finding.Code == PackageFindingCodes.InvalidZip);
    }

    [Fact]
    public void Inspect_TooManyEntries_ReturnsOneBoundedFailure()
    {
        byte[] package = CreatePackage(
            new ArchiveEntrySpec("word/document.xml", "body"u8.ToArray()));
        using MemoryStream stream = new(package);
        PackageLimits limits = PackageLimits.Default with { MaxEntryCount = 2 };

        PackageInspectionResult result = reader.Inspect(stream, limits);

        AssertFailedWith(result, PackageFindingCodes.EntryCountExceeded);
        Assert.Empty(result.Entries);
        Assert.Single(result.Findings);
    }

    [Theory]
    [InlineData("../evil.xml")]
    [InlineData("word/../../evil.xml")]
    [InlineData("./word/document.xml")]
    [InlineData("/word/document.xml")]
    [InlineData("C:/evil.xml")]
    [InlineData("word\\document.xml")]
    [InlineData("word//document.xml")]
    [InlineData("word/%2e%2e/evil.xml")]
    [InlineData("word/%2F/evil.xml")]
    [InlineData("word/%5c/evil.xml")]
    [InlineData("word/%252e%252e/evil.xml")]
    [InlineData("word/document.xml?version=1")]
    public void Inspect_UnsafeEntryPath_FailsClosed(string entryName)
    {
        byte[] package = CreatePackage(
            new ArchiveEntrySpec(entryName, "unsafe"u8.ToArray()));
        using MemoryStream stream = new(package);

        PackageInspectionResult result = reader.Inspect(stream);

        PackageFinding finding = Assert.Single(
            result.Findings,
            candidate => candidate.Code == PackageFindingCodes.InvalidEntryPath);
        Assert.Equal(entryName, finding.EntryName);
        Assert.Equal(PackageInspectionStatus.Failed, result.Status);
    }

    [Fact]
    public void Inspect_EntryNameOverConfiguredLimit_FailsClosed()
    {
        string longName = $"word/{new string('a', 32)}.xml";
        byte[] package = CreatePackage(new ArchiveEntrySpec(longName, []));
        using MemoryStream stream = new(package);
        PackageLimits limits = PackageLimits.Default with { MaxEntryNameCharacters = 24 };

        PackageInspectionResult result = reader.Inspect(stream, limits);

        AssertFailedWith(result, PackageFindingCodes.InvalidEntryPath);
    }

    [Theory]
    [InlineData("word/value.xml", "word/value.xml")]
    [InlineData("word/value.xml", "WORD/VALUE.XML")]
    [InlineData("word/a.xml", "word/%61.xml")]
    public void Inspect_LogicallyDuplicateEntryNames_FailsClosed(string first, string second)
    {
        byte[] package = CreatePackage(
            new ArchiveEntrySpec(first, "first"u8.ToArray()),
            new ArchiveEntrySpec(second, "second"u8.ToArray()));
        using MemoryStream stream = new(package);

        PackageInspectionResult result = reader.Inspect(stream);

        AssertFailedWith(result, PackageFindingCodes.DuplicateEntryName);
    }

    [Fact]
    public void Inspect_MissingContentTypesEntry_ReturnsSpecificFinding()
    {
        byte[] package = CreateArchive(
            CompressionLevel.NoCompression,
            new ArchiveEntrySpec("_rels/.rels", RootRelationshipsContent));
        using MemoryStream stream = new(package);

        PackageInspectionResult result = reader.Inspect(stream);

        AssertFailedWith(result, PackageFindingCodes.MissingContentTypes);
        Assert.DoesNotContain(
            result.Findings,
            finding => finding.Code == PackageFindingCodes.MissingRootRelationships);
    }

    [Fact]
    public void Inspect_ContentTypesDirectory_DoesNotSatisfyRequiredFile()
    {
        byte[] package = CreateArchive(
            CompressionLevel.NoCompression,
            new ArchiveEntrySpec("[Content_Types].xml/", []),
            new ArchiveEntrySpec("_rels/.rels", RootRelationshipsContent));
        using MemoryStream stream = new(package);

        PackageInspectionResult result = reader.Inspect(stream);

        AssertFailedWith(result, PackageFindingCodes.MissingContentTypes);
    }

    [Fact]
    public void Inspect_MissingRootRelationshipsEntry_ReturnsSpecificFinding()
    {
        byte[] package = CreateArchive(
            CompressionLevel.NoCompression,
            new ArchiveEntrySpec("[Content_Types].xml", ContentTypesContent));
        using MemoryStream stream = new(package);

        PackageInspectionResult result = reader.Inspect(stream);

        AssertFailedWith(result, PackageFindingCodes.MissingRootRelationships);
        Assert.DoesNotContain(
            result.Findings,
            finding => finding.Code == PackageFindingCodes.MissingContentTypes);
    }

    [Fact]
    public void Inspect_SingleEntryOverUncompressedLimit_FailsWithoutReadingPayloads()
    {
        const string targetEntry = "word/document.xml";
        byte[] package = CreatePackage(
            new ArchiveEntrySpec(targetEntry, new byte[128]));
        CorruptCentralDirectoryCrc(package, targetEntry);
        using MemoryStream stream = new(package);
        PackageLimits limits = PackageLimits.Default with
        {
            MaxEntryUncompressedBytes = 64,
        };

        PackageInspectionResult result = reader.Inspect(stream, limits);

        AssertFailedWith(result, PackageFindingCodes.EntryTooLarge);
        Assert.DoesNotContain(
            result.Findings,
            finding => finding.Code == PackageFindingCodes.CrcMismatch);
        Assert.All(result.Entries, entry => Assert.False(entry.IntegrityVerified));
    }

    [Fact]
    public void Inspect_TotalUncompressedSizeOverLimit_FailsWithoutReadingPayloads()
    {
        byte[] package = CreatePackage(
            new ArchiveEntrySpec("word/document.xml", new byte[32]));
        long declaredTotal = ContentTypesContent.Length + RootRelationshipsContent.Length + 32;
        using MemoryStream stream = new(package);
        PackageLimits limits = PackageLimits.Default with
        {
            MaxTotalUncompressedBytes = declaredTotal - 1,
        };

        PackageInspectionResult result = reader.Inspect(stream, limits);

        AssertFailedWith(result, PackageFindingCodes.TotalUncompressedSizeExceeded);
        Assert.Equal(declaredTotal, result.DeclaredTotalUncompressedBytes);
        Assert.All(result.Entries, entry => Assert.False(entry.IntegrityVerified));
    }

    [Fact]
    public void Inspect_HighCompressionRatio_FailsWithoutInflatingEntry()
    {
        byte[] package = CreatePackage(
            CompressionLevel.SmallestSize,
            new ArchiveEntrySpec("word/document.xml", new byte[4_096]));
        using MemoryStream stream = new(package);
        PackageLimits limits = PackageLimits.Default with { MaxCompressionRatio = 2 };

        PackageInspectionResult result = reader.Inspect(stream, limits);

        AssertFailedWith(result, PackageFindingCodes.CompressionRatioExceeded);
        Assert.All(result.Entries, entry => Assert.False(entry.IntegrityVerified));
    }

    [Fact]
    public void Inspect_CorruptDeclaredCrc_IsDetectedAfterFullRead()
    {
        const string targetEntry = "word/document.xml";
        byte[] package = CreatePackage(
            new ArchiveEntrySpec(targetEntry, "123456789"u8.ToArray()));
        CorruptCentralDirectoryCrc(package, targetEntry);
        using MemoryStream stream = new(package);

        PackageInspectionResult result = reader.Inspect(stream);

        PackageFinding finding = Assert.Single(
            result.Findings,
            candidate => candidate.Code == PackageFindingCodes.CrcMismatch);
        Assert.Equal(targetEntry, finding.EntryName);
        Assert.False(Assert.Single(result.Entries, entry => entry.Name == targetEntry).IntegrityVerified);
    }

    [Fact]
    public void Inspect_ForgedSmallDeclaredLengthAndCrc_CannotHideRawDeflateOutput()
    {
        const string targetEntry = "word/document.xml";
        byte[] package = CreatePackage(
            CompressionLevel.SmallestSize,
            new ArchiveEntrySpec(targetEntry, new byte[4_096]));
        SetCentralDirectoryUncompressedSize(package, targetEntry, declaredSize: 1);
        SetDeclaredCrc(package, targetEntry, crc32: 0xD202EF8D);
        using MemoryStream stream = new(package);
        PackageLimits limits = PackageLimits.Default with
        {
            MaxEntryUncompressedBytes = 512,
            MaxCompressionRatio = 10_000,
        };

        PackageInspectionResult result = reader.Inspect(stream, limits);

        AssertFailedWith(result, PackageFindingCodes.EntryTooLarge);
        Assert.False(Assert.Single(result.Entries, entry => entry.Name == targetEntry).IntegrityVerified);
    }

    [Fact]
    public void Inspect_ActualLengthDifferentFromCentralDirectory_IsDetected()
    {
        const string targetEntry = "word/document.xml";
        byte[] package = CreatePackage(
            new ArchiveEntrySpec(targetEntry, "123456789"u8.ToArray()));
        SetCentralDirectoryUncompressedSize(package, targetEntry, declaredSize: 10);
        using MemoryStream stream = new(package);

        PackageInspectionResult result = reader.Inspect(stream);

        AssertFailedWith(result, PackageFindingCodes.EntryLengthMismatch);
    }

    [Fact]
    public void Inspect_EncryptedEntryFlag_IsRejectedBeforePayloadRead()
    {
        const string targetEntry = "word/document.xml";
        byte[] package = CreatePackage(
            new ArchiveEntrySpec(targetEntry, "body"u8.ToArray()));
        SetCentralDirectoryEncryptionFlag(package, targetEntry);
        using MemoryStream stream = new(package);

        PackageInspectionResult result = reader.Inspect(stream);

        AssertFailedWith(result, PackageFindingCodes.EncryptedEntry);
        Assert.True(Assert.Single(result.Entries, entry => entry.Name == targetEntry).IsEncrypted);
        Assert.All(result.Entries, entry => Assert.False(entry.IntegrityVerified));
    }

    [Fact]
    public void Inspect_InvalidUtf8EntryName_IsRejectedBeforeZipArchiveProjection()
    {
        const string targetEntry = "word/x.xml";
        byte[] package = CreatePackage(new ArchiveEntrySpec(targetEntry, []));
        CorruptEntryNameUtf8(package, targetEntry, targetEntry.IndexOf('x'));
        using MemoryStream stream = new(package);

        PackageInspectionResult result = reader.Inspect(stream);

        AssertFailedWith(result, PackageFindingCodes.InvalidEntryEncoding);
        Assert.Empty(result.Entries);
    }

    [Fact]
    public void Inspect_LocalAndCentralNamesDisagree_IsRejectedAsSplitView()
    {
        const string centralName = "safe0000.xml";
        const string localName = "../evil0.xml";
        byte[] package = CreatePackage(new ArchiveEntrySpec(centralName, []));
        ReplaceLocalEntryName(package, centralName, localName);
        using MemoryStream stream = new(package);

        PackageInspectionResult result = reader.Inspect(stream);

        AssertFailedWith(result, PackageFindingCodes.HeaderMismatch);
        Assert.Empty(result.Entries);
    }

    [Fact]
    public void Inspect_InflatedCompressedLengthOverlapsNextRecord_IsRejected()
    {
        const string targetEntry = "[Content_Types].xml";
        byte[] package = CreatePackage();
        IncreaseCompressedLength(package, targetEntry, increment: 1);
        using MemoryStream stream = new(package);

        PackageInspectionResult result = reader.Inspect(stream);

        AssertFailedWith(result, PackageFindingCodes.HeaderMismatch);
    }

    [Fact]
    public void Inspect_UnsupportedCompressionMethod_ReturnsEntryDataFinding()
    {
        const string targetEntry = "word/document.xml";
        byte[] package = CreatePackage(new ArchiveEntrySpec(targetEntry, "body"u8.ToArray()));
        SetCompressionMethod(package, targetEntry, method: 99);
        using MemoryStream stream = new(package);

        PackageInspectionResult result = reader.Inspect(stream);

        AssertFailedWith(result, PackageFindingCodes.EntryDataInvalid);
    }

    [Fact]
    public void Inspect_TruncatedDeflateWithForgedLengthAndCrc_IsRejected()
    {
        const string targetEntry = "word/document.xml";
        byte[] payload = Encoding.UTF8.GetBytes(string.Concat(
            Enumerable.Repeat("DocxGuard", 1_000)));
        byte[] package = CreatePackage(
            CompressionLevel.SmallestSize,
            new ArchiveEntrySpec(targetEntry, payload));
        package = TruncateDeflateAndForgeOutputMetadata(package, targetEntry);
        using MemoryStream stream = new(package);
        PackageLimits limits = PackageLimits.Default with { MaxCompressionRatio = 10_000 };

        PackageInspectionResult result = reader.Inspect(stream, limits);

        AssertFailedWith(result, PackageFindingCodes.EntryDataInvalid);
        Assert.False(Assert.Single(result.Entries, entry => entry.Name == targetEntry).IntegrityVerified);
    }

    [Fact]
    public void Inspect_DeflateWithTrailingCompressedSpanByte_IsRejected()
    {
        const string targetEntry = "word/document.xml";
        byte[] package = CreatePackage(
            CompressionLevel.SmallestSize,
            new ArchiveEntrySpec(targetEntry, new byte[4_096]));
        package = AppendByteToCompressedSpan(package, targetEntry, value: 0x00);
        using MemoryStream stream = new(package);
        PackageLimits limits = PackageLimits.Default with { MaxCompressionRatio = 10_000 };

        PackageInspectionResult result = reader.Inspect(stream, limits);

        AssertFailedWith(result, PackageFindingCodes.EntryDataInvalid);
    }

    [Fact]
    public void Inspect_ArchiveUsingDataDescriptors_PassesStructuralAndContentChecks()
    {
        byte[] package = CreateArchiveWithDataDescriptors(
            new ArchiveEntrySpec("[Content_Types].xml", ContentTypesContent),
            new ArchiveEntrySpec("_rels/.rels", RootRelationshipsContent));
        using MemoryStream stream = new(package);

        PackageInspectionResult result = reader.Inspect(stream);

        Assert.Equal(PackageInspectionStatus.Passed, result.Status);
        Assert.All(result.Entries, entry => Assert.True(entry.IntegrityVerified));
    }

    [Fact]
    public void Inspect_InvalidLimitConfiguration_ThrowsBeforeInspectingInput()
    {
        using MemoryStream stream = new(CreatePackage());
        PackageLimits limits = PackageLimits.Default with { MaxEntryCount = 0 };

        Assert.Throws<ArgumentOutOfRangeException>(() => reader.Inspect(stream, limits));
    }

    [Fact]
    public void Inspect_NonSeekableStream_RejectsCallerContractViolation()
    {
        using NonSeekableReadStream stream = new(CreatePackage());

        Assert.Throws<ArgumentException>(() => reader.Inspect(stream));
    }

    [Fact]
    public void Inspect_CancelledOperation_PropagatesCancellation()
    {
        using MemoryStream stream = new(CreatePackage());
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => reader.Inspect(stream, cancellationToken: cancellation.Token));
    }

    private static void AssertFailedWith(PackageInspectionResult result, string findingCode)
    {
        Assert.Equal(PackageInspectionStatus.Failed, result.Status);
        Assert.Contains(result.Findings, finding => finding.Code == findingCode);
    }

    private static byte[] CreatePackage(params ArchiveEntrySpec[] additionalEntries) =>
        CreatePackage(CompressionLevel.NoCompression, additionalEntries);

    private static byte[] CreatePackage(
        CompressionLevel compressionLevel,
        params ArchiveEntrySpec[] additionalEntries)
    {
        List<ArchiveEntrySpec> entries =
        [
            new("[Content_Types].xml", ContentTypesContent),
            new("_rels/.rels", RootRelationshipsContent),
        ];
        entries.AddRange(additionalEntries);

        return CreateArchive(compressionLevel, entries.ToArray());
    }

    private static byte[] CreateArchive(
        CompressionLevel compressionLevel,
        params ArchiveEntrySpec[] entries)
    {
        using MemoryStream stream = new();
        using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (ArchiveEntrySpec specification in entries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(
                    specification.Name,
                    compressionLevel);
                using Stream content = entry.Open();
                content.Write(specification.Content);
            }
        }

        return stream.ToArray();
    }

    private static byte[] CreateArchiveWithDataDescriptors(params ArchiveEntrySpec[] entries)
    {
        using NonSeekableWriteStream stream = new();
        using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (ArchiveEntrySpec specification in entries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(
                    specification.Name,
                    CompressionLevel.Optimal);
                using Stream content = entry.Open();
                content.Write(specification.Content);
            }
        }

        return stream.ToArray();
    }

    private static void CorruptCentralDirectoryCrc(byte[] package, string targetEntryName)
    {
        int offset = FindCentralDirectoryEntryOffset(package, targetEntryName);
        package[offset + 16] ^= 0x01;
        int localOffset = ReadLocalHeaderOffset(package, offset);
        package[localOffset + 14] ^= 0x01;
    }

    private static void SetDeclaredCrc(byte[] package, string targetEntryName, uint crc32)
    {
        int centralOffset = FindCentralDirectoryEntryOffset(package, targetEntryName);
        int localOffset = ReadLocalHeaderOffset(package, centralOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(
            package.AsSpan(centralOffset + 16, 4),
            crc32);
        BinaryPrimitives.WriteUInt32LittleEndian(
            package.AsSpan(localOffset + 14, 4),
            crc32);
    }

    private static void SetCentralDirectoryUncompressedSize(
        byte[] package,
        string targetEntryName,
        uint declaredSize)
    {
        int offset = FindCentralDirectoryEntryOffset(package, targetEntryName);
        BinaryPrimitives.WriteUInt32LittleEndian(package.AsSpan(offset + 24, 4), declaredSize);
        int localOffset = ReadLocalHeaderOffset(package, offset);
        BinaryPrimitives.WriteUInt32LittleEndian(package.AsSpan(localOffset + 22, 4), declaredSize);
    }

    private static void SetCentralDirectoryEncryptionFlag(byte[] package, string targetEntryName)
    {
        int offset = FindCentralDirectoryEntryOffset(package, targetEntryName);
        Span<byte> flagBytes = package.AsSpan(offset + 8, 2);
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(flagBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(flagBytes, (ushort)(flags | 0x0001));

        int localOffset = ReadLocalHeaderOffset(package, offset);
        Span<byte> localFlagBytes = package.AsSpan(localOffset + 6, 2);
        ushort localFlags = BinaryPrimitives.ReadUInt16LittleEndian(localFlagBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(
            localFlagBytes,
            (ushort)(localFlags | 0x0001));
    }

    private static void CorruptEntryNameUtf8(byte[] package, string targetEntryName, int byteIndex)
    {
        int centralOffset = FindCentralDirectoryEntryOffset(package, targetEntryName);
        int localOffset = ReadLocalHeaderOffset(package, centralOffset);
        package[centralOffset + 46 + byteIndex] = 0xFF;
        package[localOffset + 30 + byteIndex] = 0xFF;

        SetFlag(package.AsSpan(centralOffset + 8, 2), 0x0800);
        SetFlag(package.AsSpan(localOffset + 6, 2), 0x0800);
    }

    private static void ReplaceLocalEntryName(
        byte[] package,
        string centralEntryName,
        string replacementLocalName)
    {
        int centralOffset = FindCentralDirectoryEntryOffset(package, centralEntryName);
        int localOffset = ReadLocalHeaderOffset(package, centralOffset);
        byte[] replacementBytes = Encoding.UTF8.GetBytes(replacementLocalName);
        ushort localNameLength = BinaryPrimitives.ReadUInt16LittleEndian(
            package.AsSpan(localOffset + 26, 2));
        Assert.Equal(localNameLength, replacementBytes.Length);
        replacementBytes.CopyTo(package, localOffset + 30);
    }

    private static void IncreaseCompressedLength(
        byte[] package,
        string targetEntryName,
        uint increment)
    {
        int centralOffset = FindCentralDirectoryEntryOffset(package, targetEntryName);
        int localOffset = ReadLocalHeaderOffset(package, centralOffset);
        uint compressedLength = BinaryPrimitives.ReadUInt32LittleEndian(
            package.AsSpan(centralOffset + 20, 4));
        uint modifiedLength = checked(compressedLength + increment);
        BinaryPrimitives.WriteUInt32LittleEndian(
            package.AsSpan(centralOffset + 20, 4),
            modifiedLength);
        BinaryPrimitives.WriteUInt32LittleEndian(
            package.AsSpan(localOffset + 18, 4),
            modifiedLength);
    }

    private static void SetCompressionMethod(
        byte[] package,
        string targetEntryName,
        ushort method)
    {
        int centralOffset = FindCentralDirectoryEntryOffset(package, targetEntryName);
        int localOffset = ReadLocalHeaderOffset(package, centralOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(
            package.AsSpan(centralOffset + 10, 2),
            method);
        BinaryPrimitives.WriteUInt16LittleEndian(
            package.AsSpan(localOffset + 8, 2),
            method);
    }

    private static byte[] TruncateDeflateAndForgeOutputMetadata(
        byte[] package,
        string targetEntryName)
    {
        int centralOffset = FindCentralDirectoryEntryOffset(package, targetEntryName);
        int localOffset = ReadLocalHeaderOffset(package, centralOffset);
        int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(
            package.AsSpan(localOffset + 26, 2));
        int extraLength = BinaryPrimitives.ReadUInt16LittleEndian(
            package.AsSpan(localOffset + 28, 2));
        int dataOffset = localOffset + 30 + nameLength + extraLength;
        int compressedLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
            package.AsSpan(centralOffset + 20, 4)));

        byte[]? truncatedOutput = null;
        int bytesToRemove = 0;
        for (int candidate = 1; candidate <= Math.Min(10, compressedLength - 1); candidate++)
        {
            try
            {
                using MemoryStream compressed = new(
                    package.AsSpan(dataOffset, compressedLength - candidate).ToArray());
                using DeflateStream inflater = new(compressed, CompressionMode.Decompress);
                using MemoryStream output = new();
                inflater.CopyTo(output);
                if (output.Length > 0)
                {
                    truncatedOutput = output.ToArray();
                    bytesToRemove = candidate;
                    break;
                }
            }
            catch (InvalidDataException)
            {
            }
        }

        Assert.NotNull(truncatedOutput);
        int removalOffset = dataOffset + compressedLength - bytesToRemove;
        byte[] modified = new byte[package.Length - bytesToRemove];
        package.AsSpan(0, removalOffset).CopyTo(modified);
        package.AsSpan(removalOffset + bytesToRemove).CopyTo(modified.AsSpan(removalOffset));

        int modifiedCentralOffset = FindCentralDirectoryEntryOffset(modified, targetEntryName);
        int modifiedLocalOffset = ReadLocalHeaderOffset(modified, modifiedCentralOffset);
        uint modifiedCompressedLength = checked((uint)(compressedLength - bytesToRemove));
        BinaryPrimitives.WriteUInt32LittleEndian(
            modified.AsSpan(modifiedCentralOffset + 20, 4),
            modifiedCompressedLength);
        BinaryPrimitives.WriteUInt32LittleEndian(
            modified.AsSpan(modifiedLocalOffset + 18, 4),
            modifiedCompressedLength);

        int endRecordOffset = FindEndOfCentralDirectoryOffset(modified);
        uint originalDirectoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(
            modified.AsSpan(endRecordOffset + 16, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(
            modified.AsSpan(endRecordOffset + 16, 4),
            checked(originalDirectoryOffset - (uint)bytesToRemove));

        SetCentralDirectoryUncompressedSize(
            modified,
            targetEntryName,
            checked((uint)truncatedOutput!.Length));
        SetDeclaredCrc(modified, targetEntryName, GetZipCrc32(truncatedOutput));
        return modified;
    }

    private static byte[] AppendByteToCompressedSpan(
        byte[] package,
        string targetEntryName,
        byte value)
    {
        int centralOffset = FindCentralDirectoryEntryOffset(package, targetEntryName);
        int localOffset = ReadLocalHeaderOffset(package, centralOffset);
        int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(
            package.AsSpan(localOffset + 26, 2));
        int extraLength = BinaryPrimitives.ReadUInt16LittleEndian(
            package.AsSpan(localOffset + 28, 2));
        int compressedLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
            package.AsSpan(centralOffset + 20, 4)));
        int dataEnd = localOffset + 30 + nameLength + extraLength + compressedLength;

        byte[] modified = new byte[package.Length + 1];
        package.AsSpan(0, dataEnd).CopyTo(modified);
        modified[dataEnd] = value;
        package.AsSpan(dataEnd).CopyTo(modified.AsSpan(dataEnd + 1));

        int modifiedCentralOffset = FindCentralDirectoryEntryOffset(modified, targetEntryName);
        int modifiedLocalOffset = ReadLocalHeaderOffset(modified, modifiedCentralOffset);
        uint modifiedCompressedLength = checked((uint)compressedLength + 1);
        BinaryPrimitives.WriteUInt32LittleEndian(
            modified.AsSpan(modifiedCentralOffset + 20, 4),
            modifiedCompressedLength);
        BinaryPrimitives.WriteUInt32LittleEndian(
            modified.AsSpan(modifiedLocalOffset + 18, 4),
            modifiedCompressedLength);

        int endRecordOffset = FindEndOfCentralDirectoryOffset(modified);
        uint originalDirectoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(
            modified.AsSpan(endRecordOffset + 16, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(
            modified.AsSpan(endRecordOffset + 16, 4),
            checked(originalDirectoryOffset + 1));

        return modified;
    }

    private static int FindEndOfCentralDirectoryOffset(byte[] package)
    {
        ReadOnlySpan<byte> signature = [0x50, 0x4B, 0x05, 0x06];
        for (int offset = package.Length - 22; offset >= 0; offset--)
        {
            if (package.AsSpan(offset, 4).SequenceEqual(signature))
            {
                return offset;
            }
        }

        throw new InvalidOperationException("The end-of-central-directory record was not found.");
    }

    private static uint GetZipCrc32(byte[] content)
    {
        byte[] archiveBytes = CreateArchive(
            CompressionLevel.NoCompression,
            new ArchiveEntrySpec("value", content));
        using MemoryStream stream = new(archiveBytes);
        using ZipArchive archive = new(stream, ZipArchiveMode.Read);
        return Assert.Single(archive.Entries).Crc32;
    }

    private static void SetFlag(Span<byte> flagBytes, ushort flag)
    {
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(flagBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(flagBytes, (ushort)(flags | flag));
    }

    private static int ReadLocalHeaderOffset(byte[] package, int centralDirectoryOffset) =>
        checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
            package.AsSpan(centralDirectoryOffset + 42, 4)));

    private static int FindCentralDirectoryEntryOffset(byte[] package, string targetEntryName)
    {
        ReadOnlySpan<byte> centralDirectorySignature = [0x50, 0x4B, 0x01, 0x02];

        for (int offset = 0; offset <= package.Length - 46; offset++)
        {
            if (!package.AsSpan(offset, 4).SequenceEqual(centralDirectorySignature))
            {
                continue;
            }

            int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(package.AsSpan(offset + 28, 2));
            int extraLength = BinaryPrimitives.ReadUInt16LittleEndian(package.AsSpan(offset + 30, 2));
            int commentLength = BinaryPrimitives.ReadUInt16LittleEndian(package.AsSpan(offset + 32, 2));
            int recordLength = 46 + nameLength + extraLength + commentLength;
            if (offset + recordLength > package.Length)
            {
                break;
            }

            string entryName = Encoding.UTF8.GetString(package, offset + 46, nameLength);
            if (string.Equals(entryName, targetEntryName, StringComparison.Ordinal))
            {
                return offset;
            }

            offset += recordLength - 1;
        }

        throw new InvalidOperationException($"Central directory entry '{targetEntryName}' was not found.");
    }

    private sealed record ArchiveEntrySpec(string Name, byte[] Content);

    private sealed class NonSeekableReadStream(byte[] content) : Stream
    {
        private readonly MemoryStream inner = new(content, writable: false);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class NonSeekableWriteStream : Stream
    {
        private readonly MemoryStream inner = new();

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public byte[] ToArray() => inner.ToArray();

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            inner.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
