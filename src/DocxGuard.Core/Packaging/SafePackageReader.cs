using System.IO.Compression;
using System.Text;
using ICSharpCode.SharpZipLib;
using ICSharpCode.SharpZipLib.Zip.Compression;
using ICSharpCode.SharpZipLib.Zip.Compression.Streams;

namespace DocxGuard.Core.Packaging;

/// <summary>
/// Performs a bounded, read-only preflight of an untrusted ZIP/OPC package.
/// It never extracts entries or parses XML.
/// </summary>
public sealed class SafePackageReader
{
    private const int ReadBufferSize = 81_920;
    private const string ContentTypesEntryName = "[Content_Types].xml";
    private const string RootRelationshipsEntryName = "_rels/.rels";
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public PackageInspectionResult Inspect(
        string path,
        PackageLimits? limits = null,
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

        return Inspect(stream, limits, cancellationToken);
    }

    public PackageInspectionResult Inspect(
        Stream packageStream,
        PackageLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packageStream);

        if (!packageStream.CanRead)
        {
            throw new ArgumentException("The package stream must be readable.", nameof(packageStream));
        }

        if (!packageStream.CanSeek)
        {
            throw new ArgumentException("The package stream must be seekable.", nameof(packageStream));
        }

        limits ??= PackageLimits.Default;
        limits.Validate();

        long initialPosition = packageStream.Position;
        long archiveLength = packageStream.Length;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (archiveLength > limits.MaxArchiveBytes)
            {
                return Failure(
                    archiveLength,
                    PackageFindingCodes.ArchiveTooLarge,
                    $"The package is {archiveLength} bytes; the configured limit is {limits.MaxArchiveBytes} bytes.");
            }

            packageStream.Position = 0;

            try
            {
                ZipDirectoryInfo directory = ZipStructureInspector.Inspect(
                    packageStream,
                    limits,
                    cancellationToken);
                packageStream.Position = 0;

                using ZipArchive archive = new(
                    packageStream,
                    ZipArchiveMode.Read,
                    leaveOpen: true,
                    entryNameEncoding: StrictUtf8);
                return InspectArchive(
                    archive,
                    directory,
                    packageStream,
                    archiveLength,
                    limits,
                    cancellationToken);
            }
            catch (PackagePreflightException exception)
            {
                return Failure(archiveLength, exception.FindingCode, exception.Message);
            }
            catch (Exception exception) when (exception is InvalidDataException or DecoderFallbackException)
            {
                return Failure(
                    archiveLength,
                    PackageFindingCodes.InvalidZip,
                    $"The stream is not a readable ZIP archive: {exception.Message}");
            }
        }
        finally
        {
            packageStream.Position = initialPosition;
        }
    }

    private static PackageInspectionResult InspectArchive(
        ZipArchive archive,
        ZipDirectoryInfo directory,
        Stream packageStream,
        long archiveLength,
        PackageLimits limits,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ZipArchiveEntry> archiveEntries = archive.Entries;
        ValidateArchiveProjection(archiveEntries, directory.Entries);

        if (archiveEntries.Count > limits.MaxEntryCount)
        {
            return Failure(
                archiveLength,
                PackageFindingCodes.EntryCountExceeded,
                $"The package declares {archiveEntries.Count} entries; the configured limit is {limits.MaxEntryCount}.");
        }

        List<InspectedEntry> inspectedEntries = new(archiveEntries.Count);
        List<PackageFinding> findings = [];
        HashSet<string> canonicalNames = new(StringComparer.OrdinalIgnoreCase);
        long declaredTotalCompressedBytes = 0;
        long declaredTotalUncompressedBytes = 0;
        bool totalSizeExceeded = false;
        bool payloadInspectionAllowed = true;
        bool hasContentTypes = false;
        bool hasRootRelationships = false;

        foreach (ZipArchiveEntry entry in archiveEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string name = entry.FullName;
            bool isDirectory = name.EndsWith('/');
            string? canonicalName = null;

            if (name.Length > limits.MaxEntryNameCharacters)
            {
                findings.Add(Error(
                    PackageFindingCodes.InvalidEntryPath,
                    $"The entry name exceeds the configured limit of {limits.MaxEntryNameCharacters} characters.",
                    name));
            }
            else if (!TryCanonicalizeEntryName(name, out string validatedName, out string pathError))
            {
                findings.Add(Error(
                    PackageFindingCodes.InvalidEntryPath,
                    $"The entry name is not a safe package path: {pathError}",
                    name));
            }
            else
            {
                canonicalName = validatedName;
                if (!canonicalNames.Add(validatedName))
                {
                    findings.Add(Error(
                        PackageFindingCodes.DuplicateEntryName,
                        "The package contains duplicate entry names after URI decoding and case folding.",
                        name));
                }
            }

            hasContentTypes |= string.Equals(name, ContentTypesEntryName, StringComparison.Ordinal);
            hasRootRelationships |= string.Equals(name, RootRelationshipsEntryName, StringComparison.Ordinal);

            long compressedLength = entry.CompressedLength;
            long uncompressedLength = entry.Length;
            double compressionRatio = CalculateCompressionRatio(compressedLength, uncompressedLength);
            bool isEncrypted = entry.IsEncrypted;

            declaredTotalCompressedBytes = SaturatingAdd(
                declaredTotalCompressedBytes,
                compressedLength);

            if (!totalSizeExceeded
                && uncompressedLength > limits.MaxTotalUncompressedBytes - declaredTotalUncompressedBytes)
            {
                totalSizeExceeded = true;
                payloadInspectionAllowed = false;
                findings.Add(Error(
                    PackageFindingCodes.TotalUncompressedSizeExceeded,
                    $"The declared total uncompressed size exceeds the configured limit of {limits.MaxTotalUncompressedBytes} bytes."));
            }

            declaredTotalUncompressedBytes = SaturatingAdd(
                declaredTotalUncompressedBytes,
                uncompressedLength);

            if (isEncrypted)
            {
                payloadInspectionAllowed = false;
                findings.Add(Error(
                    PackageFindingCodes.EncryptedEntry,
                    "Encrypted ZIP entries are outside the supported V1 package policy.",
                    name));
            }

            if (uncompressedLength > limits.MaxEntryUncompressedBytes)
            {
                payloadInspectionAllowed = false;
                findings.Add(Error(
                    PackageFindingCodes.EntryTooLarge,
                    $"The entry declares {uncompressedLength} uncompressed bytes; the configured per-entry limit is {limits.MaxEntryUncompressedBytes}.",
                    name));
            }

            if (compressionRatio > limits.MaxCompressionRatio)
            {
                payloadInspectionAllowed = false;
                findings.Add(Error(
                    PackageFindingCodes.CompressionRatioExceeded,
                    $"The entry compression ratio is {compressionRatio:F2}; the configured limit is {limits.MaxCompressionRatio:F2}.",
                    name));
            }

            PackageEntryInfo info = new(
                name,
                canonicalName,
                compressedLength,
                uncompressedLength,
                compressionRatio,
                entry.Crc32,
                isDirectory,
                isEncrypted,
                IntegrityVerified: false);

            inspectedEntries.Add(new InspectedEntry(directory.Entries[inspectedEntries.Count], info));
        }

        if (!hasContentTypes)
        {
            findings.Add(Error(
                PackageFindingCodes.MissingContentTypes,
                $"The required package entry '{ContentTypesEntryName}' is missing."));
        }

        if (!hasRootRelationships)
        {
            findings.Add(Error(
                PackageFindingCodes.MissingRootRelationships,
                $"The required package entry '{RootRelationshipsEntryName}' is missing."));
        }

        if (payloadInspectionAllowed)
        {
            VerifyEntryPayloads(
                packageStream,
                inspectedEntries,
                findings,
                limits,
                cancellationToken);
        }

        return new PackageInspectionResult(
            archiveLength,
            declaredTotalCompressedBytes,
            declaredTotalUncompressedBytes,
            inspectedEntries.Select(entry => entry.Info),
            findings);
    }

    private static void ValidateArchiveProjection(
        IReadOnlyList<ZipArchiveEntry> archiveEntries,
        IReadOnlyList<RawZipEntry> rawEntries)
    {
        if (archiveEntries.Count != rawEntries.Count)
        {
            throw new PackagePreflightException(
                PackageFindingCodes.HeaderMismatch,
                "ZipArchive exposed a different entry count than the validated central directory.");
        }

        for (int index = 0; index < archiveEntries.Count; index++)
        {
            ZipArchiveEntry archiveEntry = archiveEntries[index];
            RawZipEntry rawEntry = rawEntries[index];
            if (!string.Equals(archiveEntry.FullName, rawEntry.Name, StringComparison.Ordinal)
                || archiveEntry.CompressedLength != rawEntry.CompressedLength
                || archiveEntry.Length != rawEntry.UncompressedLength
                || archiveEntry.Crc32 != rawEntry.Crc32
                || archiveEntry.IsEncrypted != rawEntry.IsEncrypted)
            {
                throw new PackagePreflightException(
                    PackageFindingCodes.HeaderMismatch,
                    "ZipArchive exposed metadata that differs from the validated ZIP records.");
            }
        }
    }

    private static void VerifyEntryPayloads(
        Stream packageStream,
        IReadOnlyList<InspectedEntry> entries,
        ICollection<PackageFinding> findings,
        PackageLimits limits,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[ReadBufferSize];
        long actualTotalUncompressedBytes = 0;
        bool stopForResourceLimit = false;

        foreach (InspectedEntry inspectedEntry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            RawZipEntry rawEntry = inspectedEntry.RawEntry;
            long actualEntryBytes = 0;
            ZipCrc32 crc32 = new();

            try
            {
                using BoundedReadStream compressedContent = new(
                    packageStream,
                    rawEntry.DataOffset,
                    rawEntry.CompressedLength);
                using Stream content = OpenPayloadStream(
                    compressedContent,
                    rawEntry.CompressionMethod,
                    out Inflater? inflater);
                int bytesRead;

                while ((bytesRead = content.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (bytesRead > limits.MaxEntryUncompressedBytes - actualEntryBytes)
                    {
                        findings.Add(Error(
                            PackageFindingCodes.EntryTooLarge,
                            $"The actual entry data exceeds the configured per-entry limit of {limits.MaxEntryUncompressedBytes} bytes.",
                            rawEntry.Name));
                        stopForResourceLimit = true;
                        break;
                    }

                    if (bytesRead > limits.MaxTotalUncompressedBytes - actualTotalUncompressedBytes)
                    {
                        findings.Add(Error(
                            PackageFindingCodes.TotalUncompressedSizeExceeded,
                            $"The actual total uncompressed data exceeds the configured limit of {limits.MaxTotalUncompressedBytes} bytes."));
                        stopForResourceLimit = true;
                        break;
                    }

                    long prospectiveEntryBytes = actualEntryBytes + bytesRead;
                    if (CalculateCompressionRatio(rawEntry.CompressedLength, prospectiveEntryBytes)
                        > limits.MaxCompressionRatio)
                    {
                        findings.Add(Error(
                            PackageFindingCodes.CompressionRatioExceeded,
                            $"The actual entry data exceeds the configured compression-ratio limit of {limits.MaxCompressionRatio:F2}.",
                            rawEntry.Name));
                        stopForResourceLimit = true;
                        break;
                    }

                    crc32.Append(buffer.AsSpan(0, bytesRead));
                    actualEntryBytes += bytesRead;
                    actualTotalUncompressedBytes += bytesRead;
                }

                if (!stopForResourceLimit
                    && inflater is not null
                    && (!inflater.IsFinished
                        || inflater.TotalIn != rawEntry.CompressedLength
                        || inflater.RemainingInput != 0))
                {
                    findings.Add(Error(
                        PackageFindingCodes.EntryDataInvalid,
                        "The Deflate stream did not finish exactly at the validated compressed-span boundary.",
                        rawEntry.Name));
                    continue;
                }
            }
            catch (Exception exception) when (
                exception is InvalidDataException or SharpZipBaseException or FormatException)
            {
                findings.Add(Error(
                    PackageFindingCodes.EntryDataInvalid,
                    $"The entry data could not be decompressed: {exception.Message}",
                    rawEntry.Name));
                continue;
            }
            catch (NotSupportedException exception)
            {
                findings.Add(Error(
                    PackageFindingCodes.EntryDataInvalid,
                    $"The entry compression method is unsupported: {exception.Message}",
                    rawEntry.Name));
                continue;
            }

            if (stopForResourceLimit)
            {
                break;
            }

            if (actualEntryBytes != rawEntry.UncompressedLength)
            {
                findings.Add(Error(
                    PackageFindingCodes.EntryLengthMismatch,
                    $"The entry declared {rawEntry.UncompressedLength} bytes but produced {actualEntryBytes} bytes.",
                    rawEntry.Name));
                continue;
            }

            uint actualCrc32 = crc32.GetCurrentHash();
            if (actualCrc32 != rawEntry.Crc32)
            {
                findings.Add(Error(
                    PackageFindingCodes.CrcMismatch,
                    $"The entry CRC-32 does not match its declared value (expected {rawEntry.Crc32:X8}, actual {actualCrc32:X8}).",
                    rawEntry.Name));
                continue;
            }

            inspectedEntry.Info = inspectedEntry.Info with { IntegrityVerified = true };
        }
    }

    private static Stream OpenPayloadStream(
        BoundedReadStream compressedContent,
        ushort compressionMethod,
        out Inflater? inflater)
    {
        inflater = null;

        if (compressionMethod == 0)
        {
            return compressedContent;
        }

        if (compressionMethod == 8)
        {
            inflater = new Inflater(noHeader: true);
            return new InflaterInputStream(
                compressedContent,
                inflater,
                bufferSize: 8_192)
            {
                IsStreamOwner = false,
            };
        }

        throw new NotSupportedException(
            $"ZIP compression method {compressionMethod} is not supported.");
    }

    private static bool TryCanonicalizeEntryName(
        string name,
        out string canonicalName,
        out string error)
    {
        canonicalName = string.Empty;

        if (string.IsNullOrEmpty(name))
        {
            error = "the name is empty";
            return false;
        }

        if (name.StartsWith('/'))
        {
            error = "absolute paths are prohibited";
            return false;
        }

        if (name.Contains('\\'))
        {
            error = "backslash separators are prohibited";
            return false;
        }

        if (name.Any(character => char.IsControl(character)))
        {
            error = "control characters are prohibited";
            return false;
        }

        bool isDirectory = name.EndsWith('/');
        string path = isDirectory ? name[..^1] : name;
        if (path.Length == 0)
        {
            error = "the package root cannot be an entry";
            return false;
        }

        string[] segments = path.Split('/');
        List<string> canonicalSegments = new(segments.Length);

        foreach (string segment in segments)
        {
            if (segment.Length == 0)
            {
                error = "empty path segments are prohibited";
                return false;
            }

            if (!HasValidPercentEncoding(segment))
            {
                error = "percent escapes must contain two hexadecimal digits";
                return false;
            }

            string decodedSegment = Uri.UnescapeDataString(segment);
            if (decodedSegment is "." or "..")
            {
                error = "dot path segments are prohibited";
                return false;
            }

            if (decodedSegment.IndexOfAny(['/', '\\', ':', '?', '#']) >= 0)
            {
                error = "decoded segments cannot contain separators, drive markers, queries, or fragments";
                return false;
            }

            if (decodedSegment.Any(character => char.IsControl(character)))
            {
                error = "decoded control characters are prohibited";
                return false;
            }

            if (ContainsPercentEscape(decodedSegment))
            {
                error = "entry names cannot contain recursively encoded URI escapes";
                return false;
            }

            canonicalSegments.Add(decodedSegment);
        }

        canonicalName = string.Join('/', canonicalSegments);
        error = string.Empty;
        return true;
    }

    private static bool HasValidPercentEncoding(string value)
    {
        for (int index = 0; index < value.Length; index++)
        {
            if (value[index] != '%')
            {
                continue;
            }

            if (index + 2 >= value.Length
                || !IsHexadecimal(value[index + 1])
                || !IsHexadecimal(value[index + 2]))
            {
                return false;
            }

            index += 2;
        }

        return true;
    }

    private static bool ContainsPercentEscape(string value)
    {
        for (int index = 0; index <= value.Length - 3; index++)
        {
            if (value[index] == '%'
                && IsHexadecimal(value[index + 1])
                && IsHexadecimal(value[index + 2]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsHexadecimal(char value) =>
        value is >= '0' and <= '9'
        or >= 'A' and <= 'F'
        or >= 'a' and <= 'f';

    private static double CalculateCompressionRatio(long compressedLength, long uncompressedLength)
    {
        if (uncompressedLength == 0)
        {
            return 1d;
        }

        return compressedLength == 0
            ? double.PositiveInfinity
            : (double)uncompressedLength / compressedLength;
    }

    private static long SaturatingAdd(long left, long right) =>
        right > long.MaxValue - left ? long.MaxValue : left + right;

    private static PackageFinding Error(string code, string message, string? entryName = null) =>
        new(code, FindingSeverity.Error, message, entryName);

    private static PackageInspectionResult Failure(
        long archiveLength,
        string code,
        string message) =>
        new(
            archiveLength,
            declaredTotalCompressedBytes: 0,
            declaredTotalUncompressedBytes: 0,
            entries: [],
            findings: [Error(code, message)]);

    private sealed class InspectedEntry(RawZipEntry rawEntry, PackageEntryInfo info)
    {
        public RawZipEntry RawEntry { get; } = rawEntry;

        public PackageEntryInfo Info { get; set; } = info;
    }

    private sealed class BoundedReadStream : Stream
    {
        private readonly Stream inner;
        private readonly long length;
        private long bytesRead;

        public BoundedReadStream(Stream inner, long offset, long length)
        {
            this.inner = inner;
            this.length = length;
            inner.Position = offset;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position
        {
            get => bytesRead;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (buffer.Length - offset < count)
            {
                throw new ArgumentException("The buffer range is invalid.", nameof(count));
            }

            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            int allowed = (int)Math.Min(buffer.Length, length - bytesRead);
            if (allowed == 0)
            {
                return 0;
            }

            int read = inner.Read(buffer[..allowed]);
            bytesRead += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
