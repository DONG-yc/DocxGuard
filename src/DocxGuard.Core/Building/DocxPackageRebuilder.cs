using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DocxGuard.Core.Execution;
using DocxGuard.Core.Indexing;
using DocxGuard.Core.Packaging;
using DocxGuard.Core.Verification;
using DocxGuard.Core.Wordprocessing;

namespace DocxGuard.Core.Building;

/// <summary>
/// Rebuilds an inspected DOCX into a new in-memory package by replacing only
/// the relationship-selected main-document entry.
/// </summary>
public sealed class DocxPackageRebuilder
{
    private const int CopyBufferSize = 81_920;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly DocxIndexInspector inspector = new();

    public DocxPackageRebuildResult Rebuild(
        string sourcePath,
        InMemoryMainDocumentPatch patch,
        DocxPackageRebuildLimits? rebuildLimits = null,
        PackageLimits? packageLimits = null,
        OpcInspectionLimits? opcLimits = null,
        WordprocessingInspectionLimits? wordprocessingLimits = null,
        DocumentIndexLimits? indexLimits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        using FileStream source = new(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4_096,
            options: FileOptions.RandomAccess);
        return Rebuild(
            source,
            patch,
            rebuildLimits,
            packageLimits,
            opcLimits,
            wordprocessingLimits,
            indexLimits,
            cancellationToken);
    }

    public DocxPackageRebuildResult Rebuild(
        Stream sourcePackage,
        InMemoryMainDocumentPatch patch,
        DocxPackageRebuildLimits? rebuildLimits = null,
        PackageLimits? packageLimits = null,
        OpcInspectionLimits? opcLimits = null,
        WordprocessingInspectionLimits? wordprocessingLimits = null,
        DocumentIndexLimits? indexLimits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePackage);
        ArgumentNullException.ThrowIfNull(patch);
        if (!sourcePackage.CanRead)
        {
            throw new ArgumentException(
                "The source package stream must be readable.",
                nameof(sourcePackage));
        }

        if (!sourcePackage.CanSeek)
        {
            throw new ArgumentException(
                "The source package stream must be seekable.",
                nameof(sourcePackage));
        }

        rebuildLimits ??= DocxPackageRebuildLimits.Default;
        packageLimits ??= PackageLimits.Default;
        opcLimits ??= OpcInspectionLimits.Default;
        wordprocessingLimits ??= WordprocessingInspectionLimits.Default;
        indexLimits ??= DocumentIndexLimits.Default;
        rebuildLimits.Validate();
        packageLimits.Validate();
        opcLimits.Validate();
        wordprocessingLimits.Validate();
        indexLimits.Validate();

        long initialPosition = sourcePackage.Position;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            long snapshotLimit = Math.Min(
                rebuildLimits.MaxSourcePackageBytes,
                Math.Min(
                    packageLimits.MaxArchiveBytes,
                    opcLimits.MaxPackageSnapshotBytes));
            long sourceLength = sourcePackage.Length;
            if (sourceLength > snapshotLimit)
            {
                return Failure(
                    patch.PlanId,
                    DocxPackageRebuildFindingCodes.SourceSnapshotLimitExceeded,
                    $"The source package is {sourceLength} bytes; the effective rebuild snapshot limit is {snapshotLimit} bytes.");
            }

            byte[] sourceSnapshot;
            try
            {
                sourceSnapshot = ReadStableSnapshot(
                    sourcePackage,
                    checked((int)sourceLength),
                    cancellationToken);
            }
            catch (Exception exception) when (
                exception is IOException or InvalidDataException or OverflowException)
            {
                return Failure(
                    patch.PlanId,
                    DocxPackageRebuildFindingCodes.SourceEntryReadFailed,
                    $"The immutable source package snapshot could not be captured: {exception.Message}");
            }

            using MemoryStream inspectionStream = new(
                sourceSnapshot,
                writable: false);
            DocxIndexInspectionResult sourceInspection = inspector.Inspect(
                inspectionStream,
                packageLimits,
                opcLimits,
                wordprocessingLimits,
                indexLimits,
                cancellationToken);
            if (sourceInspection.Status == PackageInspectionStatus.Failed)
            {
                return Failure(
                    patch.PlanId,
                    DocxPackageRebuildFindingCodes.SourceInspectionNotPassed,
                    "The source package did not pass the complete package, OPC, Word, and index inspection pipeline.",
                    sourceInspection);
            }

            if (!PatchMatchesSource(sourceInspection, patch))
            {
                return Failure(
                    patch.PlanId,
                    DocxPackageRebuildFindingCodes.PatchDoesNotMatchSource,
                    "The patch source package hash, main-document identity, or original XML hash does not match the inspected source package.",
                    sourceInspection);
            }

            byte[] outputBytes;
            try
            {
                outputBytes = RebuildPackage(
                    sourceSnapshot,
                    sourceInspection,
                    patch,
                    rebuildLimits,
                    packageLimits,
                    cancellationToken);
            }
            catch (DocxPackageOutputLimitException exception)
            {
                return Failure(
                    patch.PlanId,
                    DocxPackageRebuildFindingCodes.OutputResourceLimitExceeded,
                    exception.Message,
                    sourceInspection);
            }
            catch (SourceEntryReadException exception)
            {
                return Failure(
                    patch.PlanId,
                    DocxPackageRebuildFindingCodes.SourceEntryReadFailed,
                    exception.Message,
                    sourceInspection,
                    exception.EntryName);
            }
            catch (Exception exception) when (
                exception is IOException
                    or InvalidDataException
                    or NotSupportedException
                    or EncoderFallbackException)
            {
                return Failure(
                    patch.PlanId,
                    DocxPackageRebuildFindingCodes.PackageRebuildFailed,
                    $"The new DOCX package could not be rebuilt safely: {exception.Message}",
                    sourceInspection);
            }

            using MemoryStream outputInspectionStream = new(
                outputBytes,
                writable: false);
            DocxIndexInspectionResult outputInspection = inspector.Inspect(
                outputInspectionStream,
                packageLimits,
                opcLimits,
                wordprocessingLimits,
                indexLimits,
                cancellationToken);
            if (outputInspection.Status == PackageInspectionStatus.Failed)
            {
                return Failure(
                    patch.PlanId,
                    DocxPackageRebuildFindingCodes.OutputInspectionFailed,
                    "The rebuilt DOCX did not pass the complete package, OPC, Word, and index inspection pipeline.",
                    sourceInspection,
                    outputInspection: outputInspection);
            }

            if (!OutputMatchesPatch(outputInspection, patch))
            {
                return Failure(
                    patch.PlanId,
                    DocxPackageRebuildFindingCodes.OutputPostconditionFailed,
                    "The rebuilt DOCX did not publish the expected main-document Part and patched XML hash.",
                    sourceInspection,
                    outputInspection: outputInspection);
            }

            StructuralDiffResult diff = DocxStructuralDiffVerifier.VerifyInspected(
                sourceSnapshot, outputBytes, sourceInspection, outputInspection,
                patch, wordprocessingLimits, cancellationToken);
            if (diff.Status != StructuralDiffStatus.Passed)
            {
                return new DocxPackageRebuildResult(
                    patch.PlanId, sourceInspection, outputInspection, null,
                    [new DocxPackageRebuildFinding(
                        DocxPackageRebuildFindingCodes.StructuralDiffFailed,
                        FindingSeverity.Error,
                        "The rebuilt DOCX contains changes outside the approved allowlist.")],
                    diff);
            }

            cancellationToken.ThrowIfCancellationRequested();
            InMemoryDocxPackage artifact = new(
                patch.PlanId,
                patch.MainDocumentPartName,
                sourceSnapshot,
                outputBytes,
                patch.OriginalXmlSha256,
                patch.PatchedXmlSha256);
            return new DocxPackageRebuildResult(
                patch.PlanId,
                sourceInspection,
                outputInspection,
                artifact,
                [],
                diff);
        }
        finally
        {
            sourcePackage.Position = initialPosition;
        }
    }

    private static bool PatchMatchesSource(
        DocxIndexInspectionResult sourceInspection,
        InMemoryMainDocumentPatch patch)
    {
        WordMainDocumentInfo main = sourceInspection.DocumentInspection.MainDocument!;
        return main.Conformance == patch.Conformance
            && string.Equals(
                sourceInspection.SourcePackageSha256,
                patch.SourcePackageSha256,
                StringComparison.Ordinal)
            && string.Equals(
                main.Part.PartName.Value,
                patch.MainDocumentPartName,
                StringComparison.Ordinal)
            && string.Equals(
                ComputeSha256(sourceInspection.MainDocumentXml!.Value.Span),
                patch.OriginalXmlSha256,
                StringComparison.Ordinal);
    }

    private static bool OutputMatchesPatch(
        DocxIndexInspectionResult outputInspection,
        InMemoryMainDocumentPatch patch)
    {
        WordMainDocumentInfo main = outputInspection.DocumentInspection.MainDocument!;
        return main.Conformance == patch.Conformance
            && string.Equals(
                main.Part.PartName.Value,
                patch.MainDocumentPartName,
                StringComparison.Ordinal)
            && string.Equals(
                ComputeSha256(outputInspection.MainDocumentXml!.Value.Span),
                patch.PatchedXmlSha256,
                StringComparison.Ordinal);
    }

    private static byte[] RebuildPackage(
        byte[] sourceSnapshot,
        DocxIndexInspectionResult sourceInspection,
        InMemoryMainDocumentPatch patch,
        DocxPackageRebuildLimits rebuildLimits,
        PackageLimits packageLimits,
        CancellationToken cancellationToken)
    {
        Dictionary<string, PackageEntryInfo> verifiedEntries = sourceInspection
            .DocumentInspection
            .OpcInspection
            .PackageInspection
            .Entries
            .ToDictionary(entry => entry.Name, StringComparer.Ordinal);
        using MemoryStream sourceStream = new(sourceSnapshot, writable: false);
        using ZipArchive sourceArchive = new(
            sourceStream,
            ZipArchiveMode.Read,
            leaveOpen: false,
            entryNameEncoding: StrictUtf8);
        using MemoryStream outputStream = new();
        using (LengthLimitedWriteStream boundedOutput = new(
            outputStream,
            Math.Min(rebuildLimits.MaxOutputPackageBytes, packageLimits.MaxArchiveBytes)))
        using (ZipArchive outputArchive = new(
            boundedOutput,
            ZipArchiveMode.Create,
            leaveOpen: true,
            entryNameEncoding: StrictUtf8))
        {
            byte[] buffer = new byte[CopyBufferSize];
            long totalUncompressedBytes = 0;
            string mainEntryName = sourceInspection.DocumentInspection
                .MainDocument!.Part.PackageEntryName;
            foreach (ZipArchiveEntry sourceEntry in sourceArchive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PackageEntryInfo verifiedEntry = verifiedEntries[sourceEntry.FullName];
                bool isMainDocument = string.Equals(
                    sourceEntry.FullName,
                    mainEntryName,
                    StringComparison.Ordinal);
                long outputEntryLength = isMainDocument
                    ? patch.XmlLength
                    : verifiedEntry.UncompressedLength;
                if (outputEntryLength > packageLimits.MaxEntryUncompressedBytes)
                {
                    throw new DocxPackageOutputLimitException(
                        $"Entry '{sourceEntry.FullName}' exceeds the output per-entry limit of {packageLimits.MaxEntryUncompressedBytes} bytes.");
                }

                if (outputEntryLength > packageLimits.MaxTotalUncompressedBytes
                    - totalUncompressedBytes)
                {
                    throw new DocxPackageOutputLimitException(
                        $"Output entries exceed the total uncompressed limit of {packageLimits.MaxTotalUncompressedBytes} bytes.");
                }

                ZipArchiveEntry outputEntry = outputArchive.CreateEntry(
                    sourceEntry.FullName,
                    CompressionLevel.NoCompression);
                outputEntry.LastWriteTime = sourceEntry.LastWriteTime;
                outputEntry.ExternalAttributes = sourceEntry.ExternalAttributes;
                using Stream outputEntryStream = outputEntry.Open();
                if (isMainDocument)
                {
                    outputEntryStream.Write(patch.Xml.Span);
                }
                else
                {
                    CopyVerifiedEntry(
                        sourceEntry,
                        outputEntryStream,
                        verifiedEntry.UncompressedLength,
                        buffer,
                        cancellationToken);
                }

                totalUncompressedBytes += outputEntryLength;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return outputStream.ToArray();
    }

    private static void CopyVerifiedEntry(
        ZipArchiveEntry sourceEntry,
        Stream destination,
        long expectedLength,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        try
        {
            using Stream input = sourceEntry.Open();
            long remaining = expectedLength;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = input.Read(
                    buffer,
                    0,
                    (int)Math.Min(buffer.Length, remaining));
                if (read == 0)
                {
                    throw new InvalidDataException(
                        "The entry ended before its inspected uncompressed length.");
                }

                destination.Write(buffer, 0, read);
                remaining -= read;
            }

            if (input.ReadByte() != -1)
            {
                throw new InvalidDataException(
                    "The entry exceeded its inspected uncompressed length.");
            }
        }
        catch (Exception exception) when (
            exception is not DocxPackageOutputLimitException
                && (exception is IOException or InvalidDataException))
        {
            throw new SourceEntryReadException(
                sourceEntry.FullName,
                $"The verified source entry '{sourceEntry.FullName}' could not be copied exactly: {exception.Message}",
                exception);
        }
    }

    private static byte[] ReadStableSnapshot(
        Stream source,
        int expectedLength,
        CancellationToken cancellationToken)
    {
        source.Position = 0;
        byte[] snapshot = new byte[expectedLength];
        int offset = 0;
        while (offset < snapshot.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = source.Read(snapshot, offset, snapshot.Length - offset);
            if (read == 0)
            {
                throw new IOException(
                    "The source package ended while its immutable snapshot was captured.");
            }

            offset += read;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (source.ReadByte() != -1)
        {
            throw new IOException(
                "The source package length changed while its immutable snapshot was captured.");
        }

        return snapshot;
    }

    private static string ComputeSha256(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static DocxPackageRebuildResult Failure(
        string planId,
        string code,
        string message,
        DocxIndexInspectionResult? sourceInspection = null,
        string? entryName = null,
        DocxIndexInspectionResult? outputInspection = null) =>
        new(
            planId,
            sourceInspection,
            outputInspection,
            null,
            [
                new DocxPackageRebuildFinding(
                    code,
                    FindingSeverity.Error,
                    message,
                    entryName),
            ]);

    private sealed class SourceEntryReadException : IOException
    {
        public SourceEntryReadException(
            string entryName,
            string message,
            Exception innerException)
            : base(message, innerException)
        {
            EntryName = entryName;
        }

        public string EntryName { get; }
    }
}
