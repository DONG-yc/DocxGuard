using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DocxGuard.Core.Execution;
using DocxGuard.Core.Indexing;
using DocxGuard.Core.Packaging;
using DocxGuard.Core.Wordprocessing;

namespace DocxGuard.Core.Verification;

/// <summary>
/// Independently verifies a candidate package against its source and approved
/// text patch. Public stream inputs are copied and fully inspected before use.
/// </summary>
public sealed class DocxStructuralDiffVerifier
{
    public StructuralDiffResult Verify(
        Stream sourcePackage,
        Stream outputPackage,
        InMemoryMainDocumentPatch patch,
        PackageLimits? packageLimits = null,
        OpcInspectionLimits? opcLimits = null,
        WordprocessingInspectionLimits? wordprocessingLimits = null,
        DocumentIndexLimits? indexLimits = null,
        CancellationToken cancellationToken = default)
    {
        ValidateStream(sourcePackage, nameof(sourcePackage));
        ValidateStream(outputPackage, nameof(outputPackage));
        ArgumentNullException.ThrowIfNull(patch);
        packageLimits ??= PackageLimits.Default;
        opcLimits ??= OpcInspectionLimits.Default;
        wordprocessingLimits ??= WordprocessingInspectionLimits.Default;
        indexLimits ??= DocumentIndexLimits.Default;
        packageLimits.Validate();
        opcLimits.Validate();
        wordprocessingLimits.Validate();
        indexLimits.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        long limit = Math.Min(packageLimits.MaxArchiveBytes, opcLimits.MaxPackageSnapshotBytes);
        if (sourcePackage.Length > limit || outputPackage.Length > limit)
        {
            return Failure(null, null, StructuralDiffFindingCodes.SnapshotLimitExceeded,
                $"Each package snapshot must fit the effective limit of {limit} bytes.");
        }

        try
        {
            byte[] source = Capture(sourcePackage, limit, cancellationToken);
            byte[] output = Capture(outputPackage, limit, cancellationToken);
            using MemoryStream sourceStream = new(source, writable: false);
            using MemoryStream outputStream = new(output, writable: false);
            DocxIndexInspector inspector = new();
            DocxIndexInspectionResult sourceInspection = inspector.Inspect(
                sourceStream, packageLimits, opcLimits, wordprocessingLimits,
                indexLimits, cancellationToken);
            if (sourceInspection.Status != PackageInspectionStatus.Passed)
            {
                return Failure(sourceInspection, null, StructuralDiffFindingCodes.InspectionFailed,
                    "The source package failed inspection; no comparison was attempted.");
            }

            DocxIndexInspectionResult outputInspection = inspector.Inspect(
                outputStream, packageLimits, opcLimits, wordprocessingLimits,
                indexLimits, cancellationToken);
            if (outputInspection.Status != PackageInspectionStatus.Passed)
            {
                return Failure(sourceInspection, outputInspection, StructuralDiffFindingCodes.InspectionFailed,
                    "The output package failed inspection; no comparison was attempted.");
            }

            return VerifyInspected(source, output, sourceInspection, outputInspection,
                patch, wordprocessingLimits, cancellationToken);
        }
        catch (IOException exception)
        {
            return Failure(null, null, StructuralDiffFindingCodes.EntryReadFailed,
                $"A package snapshot could not be read: {exception.Message}");
        }
    }

    internal static StructuralDiffResult VerifyInspected(
        byte[] source,
        byte[] output,
        DocxIndexInspectionResult sourceInspection,
        DocxIndexInspectionResult outputInspection,
        InMemoryMainDocumentPatch patch,
        WordprocessingInspectionLimits wordprocessingLimits,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WordMainDocumentInfo sourceMain = sourceInspection.DocumentInspection.MainDocument!;
        WordMainDocumentInfo outputMain = outputInspection.DocumentInspection.MainDocument!;
        if (sourceInspection.SourcePackageSha256 != patch.SourcePackageSha256
            || sourceMain.Part.PartName.Value != patch.MainDocumentPartName
            || sourceMain.Conformance != patch.Conformance
            || Hash(sourceInspection.MainDocumentXml!.Value.Span) != patch.OriginalXmlSha256)
        {
            return Failure(sourceInspection, outputInspection, StructuralDiffFindingCodes.SourceMismatch,
                "The patch is not bound to this exact source package and main XML.");
        }

        if (sourceMain.Part.PackageEntryName != outputMain.Part.PackageEntryName
            || sourceMain.Conformance != outputMain.Conformance)
        {
            return Failure(sourceInspection, outputInspection, StructuralDiffFindingCodes.EntryInventoryChanged,
                "The relationship-selected main document or conformance changed.");
        }

        PackageInspectionResult sourcePackage = sourceInspection.DocumentInspection.OpcInspection.PackageInspection;
        PackageInspectionResult outputPackage = outputInspection.DocumentInspection.OpcInspection.PackageInspection;
        Dictionary<string, PackageEntryInfo> outputEntries = outputPackage.Entries
            .ToDictionary(entry => entry.Name, StringComparer.Ordinal);
        foreach (PackageEntryInfo entry in sourcePackage.Entries)
        {
            if (!outputEntries.TryGetValue(entry.Name, out PackageEntryInfo? other)
                || entry.IsDirectory != other.IsDirectory)
            {
                return Failure(sourceInspection, outputInspection, StructuralDiffFindingCodes.EntryInventoryChanged,
                    "A source entry was removed, renamed, or changed kind.", entry.Name);
            }
        }

        if (sourcePackage.Entries.Count != outputEntries.Count)
        {
            HashSet<string> sourceNames = sourcePackage.Entries.Select(entry => entry.Name)
                .ToHashSet(StringComparer.Ordinal);
            string added = outputEntries.Keys.First(name => !sourceNames.Contains(name));
            return Failure(sourceInspection, outputInspection, StructuralDiffFindingCodes.EntryInventoryChanged,
                "The output contains an added entry.", added);
        }

        List<VerifiedEntryChange> evidence = new(sourcePackage.Entries.Count);
        string? currentEntry = null;
        try
        {
            using MemoryStream sourceStream = new(source, writable: false);
            using MemoryStream outputStream = new(output, writable: false);
            using ZipArchive sourceZip = new(sourceStream, ZipArchiveMode.Read, false, Encoding.UTF8);
            using ZipArchive outputZip = new(outputStream, ZipArchiveMode.Read, false, Encoding.UTF8);
            Dictionary<string, ZipArchiveEntry> candidates = outputZip.Entries
                .ToDictionary(entry => entry.FullName, StringComparer.Ordinal);
            byte[] buffer = new byte[81_920];
            foreach (ZipArchiveEntry entry in sourceZip.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                currentEntry = entry.FullName;
                ZipArchiveEntry candidate = candidates[currentEntry];
                bool main = currentEntry == sourceMain.Part.PackageEntryName;
                string sourceHash = HashEntry(entry, buffer, cancellationToken);
                string outputHash = HashEntry(candidate, buffer, cancellationToken);
                if (!main && (entry.Length != candidate.Length || sourceHash != outputHash))
                {
                    return Failure(sourceInspection, outputInspection, StructuralDiffFindingCodes.ProtectedEntryChanged,
                        "An entry outside the main-document allowlist changed its decompressed bytes.", currentEntry);
                }

                evidence.Add(new VerifiedEntryChange(currentEntry, entry.Length,
                    candidate.Length, sourceHash, outputHash, main));
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            return Failure(sourceInspection, outputInspection, StructuralDiffFindingCodes.EntryReadFailed,
                $"A verified entry could not be hashed: {exception.Message}", currentEntry);
        }

        StructuralDiffFinding? xmlFinding = MainDocumentStructuralComparer.Compare(
            sourceInspection, outputInspection, patch, wordprocessingLimits, cancellationToken);
        if (xmlFinding is not null)
        {
            return new StructuralDiffResult(sourceInspection, outputInspection, [], [xmlFinding]);
        }

        if (Hash(outputInspection.MainDocumentXml!.Value.Span) != patch.PatchedXmlSha256)
        {
            return Failure(sourceInspection, outputInspection, StructuralDiffFindingCodes.PatchArtifactMismatch,
                "The main XML must also match the exact executor artifact.", sourceMain.Part.PackageEntryName);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new StructuralDiffResult(sourceInspection, outputInspection, evidence, []);
    }

    private static string HashEntry(ZipArchiveEntry entry, byte[] buffer, CancellationToken token)
    {
        using Stream input = entry.Open();
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long remaining = entry.Length;
        while (remaining > 0)
        {
            token.ThrowIfCancellationRequested();
            int read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0)
            {
                throw new InvalidDataException("The entry ended before its inspected length.");
            }

            hash.AppendData(buffer, 0, read);
            remaining -= read;
        }

        if (input.ReadByte() != -1)
        {
            throw new InvalidDataException("The entry exceeded its inspected length.");
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static byte[] Capture(Stream stream, long limit, CancellationToken token)
    {
        long position = stream.Position;
        try
        {
            stream.Position = 0;
            long length = stream.Length;
            if (length < 0 || length > limit)
            {
                throw new IOException("The package length changed beyond the snapshot limit.");
            }

            byte[] snapshot = new byte[checked((int)length)];
            int offset = 0;
            while (offset < snapshot.Length)
            {
                token.ThrowIfCancellationRequested();
                int read = stream.Read(snapshot, offset, snapshot.Length - offset);
                if (read == 0)
                {
                    throw new IOException("The package ended during snapshot capture.");
                }

                offset += read;
            }

            token.ThrowIfCancellationRequested();
            if (stream.ReadByte() != -1)
            {
                throw new IOException("The package grew during snapshot capture.");
            }

            return snapshot;
        }
        finally
        {
            stream.Position = position;
        }
    }

    private static void ValidateStream(Stream stream, string name)
    {
        ArgumentNullException.ThrowIfNull(stream, name);
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ArgumentException("The package stream must be readable and seekable.", name);
        }
    }

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static StructuralDiffResult Failure(
        DocxIndexInspectionResult? source,
        DocxIndexInspectionResult? output,
        string code,
        string message,
        string? entryName = null) =>
        new(source, output, [], [new StructuralDiffFinding(code, FindingSeverity.Error, message, entryName)]);
}
