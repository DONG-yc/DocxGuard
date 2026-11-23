using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DocxGuard.Core.Packaging;
using DocxGuard.Core.Wordprocessing;

namespace DocxGuard.Core.Indexing;

/// <summary>
/// Runs all package and WordprocessingML trust boundaries before publishing a
/// read-only main-document index.
/// </summary>
public sealed class DocxIndexInspector
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly DocxDocumentInspector documentInspector = new();

    public DocxIndexInspectionResult Inspect(
        string path,
        PackageLimits? packageLimits = null,
        OpcInspectionLimits? opcLimits = null,
        WordprocessingInspectionLimits? wordprocessingLimits = null,
        DocumentIndexLimits? indexLimits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4_096,
            options: FileOptions.RandomAccess);
        return Inspect(
            stream,
            packageLimits,
            opcLimits,
            wordprocessingLimits,
            indexLimits,
            cancellationToken);
    }

    public DocxIndexInspectionResult Inspect(
        Stream packageStream,
        PackageLimits? packageLimits = null,
        OpcInspectionLimits? opcLimits = null,
        WordprocessingInspectionLimits? wordprocessingLimits = null,
        DocumentIndexLimits? indexLimits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packageStream);
        if (!packageStream.CanRead)
        {
            throw new ArgumentException(
                "The package stream must be readable.",
                nameof(packageStream));
        }

        if (!packageStream.CanSeek)
        {
            throw new ArgumentException(
                "The package stream must be seekable.",
                nameof(packageStream));
        }

        packageLimits ??= PackageLimits.Default;
        opcLimits ??= OpcInspectionLimits.Default;
        wordprocessingLimits ??= WordprocessingInspectionLimits.Default;
        indexLimits ??= DocumentIndexLimits.Default;
        packageLimits.Validate();
        opcLimits.Validate();
        wordprocessingLimits.Validate();
        indexLimits.Validate();

        long initialPosition = packageStream.Position;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            long packageLength = packageStream.Length;
            long snapshotLimit = Math.Min(
                packageLimits.MaxArchiveBytes,
                opcLimits.MaxPackageSnapshotBytes);
            if (packageLength > snapshotLimit)
            {
                DocxInspectionResult oversized = documentInspector.Inspect(
                    packageStream,
                    packageLimits,
                    opcLimits,
                    wordprocessingLimits,
                    cancellationToken);
                return new DocxIndexInspectionResult(
                    oversized,
                    null,
                    [],
                    null);
            }

            byte[] snapshot = ReadStableSnapshot(
                packageStream,
                checked((int)packageLength),
                cancellationToken);
            using MemoryStream documentStream = new(snapshot, writable: false);
            DocxInspectionResult documentInspection = documentInspector.Inspect(
                documentStream,
                packageLimits,
                opcLimits,
                wordprocessingLimits,
                cancellationToken);
            if (documentInspection.Status == PackageInspectionStatus.Failed)
            {
                return new DocxIndexInspectionResult(
                    documentInspection,
                    null,
                    [],
                    null);
            }

            return BuildIndex(
                snapshot,
                documentInspection,
                wordprocessingLimits,
                indexLimits,
                cancellationToken);
        }
        finally
        {
            packageStream.Position = initialPosition;
        }
    }

    private static DocxIndexInspectionResult BuildIndex(
        byte[] packageSnapshot,
        DocxInspectionResult documentInspection,
        WordprocessingInspectionLimits wordprocessingLimits,
        DocumentIndexLimits indexLimits,
        CancellationToken cancellationToken)
    {
        WordMainDocumentInfo main = documentInspection.MainDocument!;
        PackageEntryInfo verifiedEntry = documentInspection.OpcInspection
            .PackageInspection.Entries.Single(
                entry => string.Equals(
                    entry.Name,
                    main.Part.PackageEntryName,
                    StringComparison.Ordinal));

        try
        {
            using MemoryStream snapshotStream = new(
                packageSnapshot,
                writable: false);
            using ZipArchive archive = new(
                snapshotStream,
                ZipArchiveMode.Read,
                leaveOpen: false,
                entryNameEncoding: StrictUtf8);
            ZipArchiveEntry archiveEntry = archive.Entries.Single(
                entry => string.Equals(
                    entry.FullName,
                    main.Part.PackageEntryName,
                    StringComparison.Ordinal));
            byte[] xml = ReadVerifiedEntry(
                archiveEntry,
                checked((int)verifiedEntry.UncompressedLength),
                cancellationToken);
            DocumentIndexParseResult parsed = WordDocumentIndexer.Parse(
                xml,
                main.Part.PartName,
                main.Conformance,
                wordprocessingLimits,
                indexLimits,
                cancellationToken);
            return new DocxIndexInspectionResult(
                documentInspection,
                parsed.Index,
                parsed.Findings,
                parsed.Index is null
                    ? (ReadOnlyMemory<byte>?)null
                    : xml,
                parsed.Index is null
                    ? null
                    : Convert.ToHexString(SHA256.HashData(packageSnapshot))
                        .ToLowerInvariant());
        }
        catch (Exception exception) when (
            exception is InvalidDataException
                or DecoderFallbackException
                or InvalidOperationException)
        {
            return new DocxIndexInspectionResult(
                documentInspection,
                null,
                [
                    new DocumentIndexFinding(
                        DocumentIndexFindingCodes.XmlReaderRejected,
                        FindingSeverity.Error,
                        "The verified main-document entry could not be read for indexing.",
                        main.Part.PartName.Value),
                ],
                null);
        }
    }

    private static byte[] ReadStableSnapshot(
        Stream packageStream,
        int expectedLength,
        CancellationToken cancellationToken)
    {
        packageStream.Position = 0;
        byte[] snapshot = new byte[expectedLength];
        int offset = 0;
        while (offset < snapshot.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = packageStream.Read(
                snapshot,
                offset,
                snapshot.Length - offset);
            if (read == 0)
            {
                throw new IOException(
                    "The package ended while its immutable snapshot was captured.");
            }

            offset += read;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (packageStream.ReadByte() != -1)
        {
            throw new IOException(
                "The package length changed while its immutable snapshot was captured.");
        }

        return snapshot;
    }

    private static byte[] ReadVerifiedEntry(
        ZipArchiveEntry entry,
        int expectedLength,
        CancellationToken cancellationToken)
    {
        byte[] content = new byte[expectedLength];
        using Stream entryStream = entry.Open();
        int offset = 0;
        while (offset < content.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = entryStream.Read(
                content,
                offset,
                content.Length - offset);
            if (read == 0)
            {
                throw new InvalidDataException(
                    "The main document ended before its verified length.");
            }

            offset += read;
        }

        if (entryStream.ReadByte() != -1)
        {
            throw new InvalidDataException(
                "The main document exceeded its verified length.");
        }

        return content;
    }
}
