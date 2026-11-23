using System.IO.Compression;
using System.Text;
using DocxGuard.Core.Packaging;

namespace DocxGuard.Core.Wordprocessing;

/// <summary>
/// Runs the complete OPC inspection and then validates the DOCX main-document
/// relationship, content type, and minimum WordprocessingML structure.
/// </summary>
public sealed class DocxDocumentInspector
{
    public const string TransitionalOfficeDocumentRelationshipType =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument";
    public const string StrictOfficeDocumentRelationshipType =
        "http://purl.oclc.org/ooxml/officeDocument/relationships/officeDocument";
    public const string MainDocumentContentType =
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml";

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly OpcPackageInspector opcInspector = new();

    public DocxInspectionResult Inspect(
        string path,
        PackageLimits? packageLimits = null,
        OpcInspectionLimits? opcLimits = null,
        WordprocessingInspectionLimits? wordprocessingLimits = null,
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
            cancellationToken);
    }

    public DocxInspectionResult Inspect(
        Stream packageStream,
        PackageLimits? packageLimits = null,
        OpcInspectionLimits? opcLimits = null,
        WordprocessingInspectionLimits? wordprocessingLimits = null,
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
        packageLimits.Validate();
        opcLimits.Validate();
        wordprocessingLimits.Validate();

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
                OpcInspectionResult oversizedInspection = opcInspector.Inspect(
                    packageStream,
                    packageLimits,
                    opcLimits,
                    cancellationToken);
                return new DocxInspectionResult(oversizedInspection, null, []);
            }

            byte[] snapshot = ReadStableSnapshot(
                packageStream,
                checked((int)packageLength),
                cancellationToken);
            using MemoryStream opcStream = new(snapshot, writable: false);
            OpcInspectionResult opcInspection = opcInspector.Inspect(
                opcStream,
                packageLimits,
                opcLimits,
                cancellationToken);
            if (opcInspection.Status == PackageInspectionStatus.Failed)
            {
                return new DocxInspectionResult(opcInspection, null, []);
            }

            return InspectMainDocument(
                snapshot,
                opcInspection,
                wordprocessingLimits,
                cancellationToken);
        }
        finally
        {
            packageStream.Position = initialPosition;
        }
    }

    private static DocxInspectionResult InspectMainDocument(
        byte[] packageSnapshot,
        OpcInspectionResult opcInspection,
        WordprocessingInspectionLimits limits,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<OpcRelationship> packageRelationships =
            opcInspection.Relationships?.PackageRelationships?.Relationships
            ?? [];
        OpcRelationship[] mainRelationships = packageRelationships
            .Where(relationship => IsOfficeDocumentRelationship(
                relationship.Type))
            .ToArray();
        if (mainRelationships.Length == 0)
        {
            return Failure(
                opcInspection,
                WordprocessingFindingCodes.MainDocumentRelationshipMissing,
                "The package has no supported officeDocument relationship.");
        }

        if (mainRelationships.Length != 1)
        {
            return Failure(
                opcInspection,
                WordprocessingFindingCodes.MultipleMainDocumentRelationships,
                "The package must have exactly one supported officeDocument relationship.");
        }

        OpcRelationship mainRelationship = mainRelationships[0];
        WordprocessingConformance conformance =
            mainRelationship.Type == StrictOfficeDocumentRelationshipType
                ? WordprocessingConformance.Strict
                : WordprocessingConformance.Transitional;
        OpcPartInfo mainPart = opcInspection.Parts.Single(
            part => part.PartName.Equals(mainRelationship.TargetPartName));
        if (!AsciiCaseInsensitiveComparer.Instance.Equals(
            mainPart.ContentType.Value,
            MainDocumentContentType))
        {
            return Failure(
                opcInspection,
                WordprocessingFindingCodes.UnsupportedMainDocumentContentType,
                $"The officeDocument target has unsupported content type '{mainPart.ContentType.Value}'.",
                mainPart.PartName.Value,
                mainRelationship.Id);
        }

        PackageEntryInfo verifiedEntry = opcInspection.PackageInspection.Entries.Single(
            entry => string.Equals(
                entry.Name,
                mainPart.PackageEntryName,
                StringComparison.Ordinal));
        if (verifiedEntry.UncompressedLength > limits.MaxMainDocumentXmlBytes)
        {
            return Failure(
                opcInspection,
                WordprocessingFindingCodes.ResourceLimitExceeded,
                $"The main document is {verifiedEntry.UncompressedLength} bytes; the limit is {limits.MaxMainDocumentXmlBytes} bytes.",
                mainPart.PartName.Value,
                mainRelationship.Id);
        }

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
                    mainPart.PackageEntryName,
                    StringComparison.Ordinal));
            byte[] xml = ReadVerifiedEntry(
                archiveEntry,
                checked((int)verifiedEntry.UncompressedLength),
                cancellationToken);
            WordMainDocumentParseResult parsed = WordMainDocumentParser.Parse(
                xml,
                mainPart.PartName,
                conformance,
                limits,
                cancellationToken);
            if (parsed.Structure is null)
            {
                return new DocxInspectionResult(
                    opcInspection,
                    null,
                    parsed.Findings.Select(
                        finding => finding with
                        {
                            RelationshipId = mainRelationship.Id,
                        }));
            }

            return new DocxInspectionResult(
                opcInspection,
                new WordMainDocumentInfo(
                    mainRelationship.Id,
                    mainRelationship.Type,
                    mainPart,
                    conformance,
                    parsed.Structure.BodyChildElementCount,
                    parsed.Structure.HasSectionProperties),
                []);
        }
        catch (Exception exception) when (
            exception is InvalidDataException
                or DecoderFallbackException
                or InvalidOperationException)
        {
            return Failure(
                opcInspection,
                WordprocessingFindingCodes.XmlReaderRejected,
                "The verified main-document entry could not be read.",
                mainPart.PartName.Value,
                mainRelationship.Id);
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
            int read = packageStream.Read(snapshot, offset, snapshot.Length - offset);
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
            int read = entryStream.Read(content, offset, content.Length - offset);
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

    private static bool IsOfficeDocumentRelationship(string type) =>
        type == TransitionalOfficeDocumentRelationshipType
        || type == StrictOfficeDocumentRelationshipType;

    private static DocxInspectionResult Failure(
        OpcInspectionResult opcInspection,
        string code,
        string message,
        string? mainDocumentPartName = null,
        string? relationshipId = null) =>
        new(
            opcInspection,
            null,
            [
                new WordprocessingFinding(
                    code,
                    FindingSeverity.Error,
                    message,
                    mainDocumentPartName,
                    relationshipId),
            ]);
}
